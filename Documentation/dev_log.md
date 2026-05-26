# XR Collaboration Prototype – Development Log

## 2026-05-24 — Avatares humanoides Rocketbox: head/hand IK + finger driving geométrico

Branch activo: `look-and-feel-prototype` (continuación de la sesión visual del día anterior).

Motivación: la representación de manos de las sesiones previas (`HandSkeletonRenderer` + `NetworkedAvatarSkeleton`: 20 esferas y 5 líneas decorativas por mano) funcionaba operativamente pero carecía de cuerpo. Los participantes veían "esqueletos flotantes" sin contexto antropomórfico — sin torso, brazos, expresión postural. Para una tarea colaborativa de Jenga sentada, donde dos a tres personas se miran y se pasan piezas, la presencia social pide un avatar humanoide completo con animación corporal coherente.

Decisión: migrar a Microsoft Rocketbox (CC0, 100% LAN-compatible, sin servicios cloud) con tres FBX distintos para diferenciación visual por rol — Male_Adult_07 (Host), Male_Adult_10 (Client), Male_Adult_12 (Helper). Cada rol activa solo su sub-mesh en runtime para evitar overhead de tres rigs renderizando simultáneamente.

Constraints heredados: el proyecto sigue en Built-in Render Pipeline + NGO 1.12.2 + XR Hands 1.5. La estrategia de avatar tenía que integrarse sin migrar a URP/HDRP y sin agregar runtime cost notable (eye tracking + BioLab UDP + Jenga physics ya pesan en el frame).

---

### Migración a Rocketbox humanoides: setup del prefab AvatarHumanoid

Estructura final del prefab `Assets/AvatarHumanoid.prefab`:

```text
AvatarHumanoid (root, NetworkObject + NetworkedAvatarPose + NetworkedAvatarRole + AvatarRoleMeshSwitcher)
├── LabelAnchor (Y=1.921, para nameplate flotante)
└── AvatarVisuals
    ├── Avatar_Host (sub-mesh con Male_Adult_07Avatar + Animator humanoid + AvatarPoseDriver + RigBuilder)
    │   ├── Bip01 (jerarquía de bones Biped 3DS Max)
    │   ├── Rig (Animation Rigging)
    │   │   ├── LeftArmIK (TwoBoneIKConstraint: Shoulder→UpperArm→Forearm→Hand)
    │   │   └── RightArmIK
    │   ├── IKTarget_LeftHand (Transform vacío, posicionado por AvatarPoseDriver cada frame)
    │   └── IKTarget_RightHand
    ├── Avatar_Client (idem con Male_Adult_10)
    └── Avatar_Helper (idem con Male_Adult_12)
```

`AvatarRoleMeshSwitcher` (existente, sin cambios funcionales) lee `NetworkedAvatarRole.Role.OnValueChanged` y hace `SetActive(true)` solo al sub-mesh del rol asignado; los otros dos se desactivan. Componentes en GameObjects inactivos no procesan Update/LateUpdate — esto evita el costo de tener 3 Animators humanoid + 3 RigBuilders evaluando simultáneamente.

#### Validador de setup: `AvatarSetupValidator.cs`

Archivo nuevo en `Assets/Editor/`. Menú: **XR Collab → Avatars → Validate Avatar Setup**.

Iteró el prefab y emite un reporte por sub-mesh:

* Animator presente + `isHuman == true` + bone Head reachable
* AvatarPoseDriver presente
* RigBuilder con al menos 1 layer
* Child "Rig" con LeftArmIK + RightArmIK (TwoBoneIKConstraint con root/mid/tip/target no nulos)
* IKTarget_LeftHand / IKTarget_RightHand como children directos del sub-mesh

Salvó tiempo identificando el caso del Avatar_Helper que tenía el `target` del `RightArmIK` apuntando al propio constraint en vez del Transform `IKTarget_RightHand` — un slip de configuración manual difícil de ver en el Inspector pero capturado por el validador en una corrida.

#### Setup de materiales: `RocketboxMaterialSetup.cs`

Los FBX de Rocketbox tenían los materiales embebidos con "Use Embedded Materials" mode. Unity los importaba pero las texturas no se asignaban automáticamente — el avatar aparecía como mannequin grisaceo. La tool de Editor:

1. Itera los FBX en `Assets/Avatars/Rocketbox/Male_Adult_*/Export/`
2. Cambia el material importer a "Use External Materials (Legacy)" → genera `.mat` y `.png` extraídos
3. Crea materiales Standard (Specular setup) con las texturas correspondientes asignadas por nombre (color, normal, specular)
4. Aplica los materiales a los `SkinnedMeshRenderer` del sub-mesh

Esto resolvió el problema en una sola pasada para los tres avatares.

#### Bug del Biped Triangle Pelvis (falso positivo)

Al configurar los FBX como Humanoid, Unity reportó un warning sobre la jerarquía Biped:

```text
Invalid parent for Bip01 L Thigh. Expected Bip01 Pelvis, but found Bip01 Spine. Disable Triangle Pelvis.
Invalid parent for Bip01 L Clavicle. Expected Bip01 Spine2, but found Bip01 Neck. Enable Triangle Neck.
```

Rocketbox usa la convención Biped 3DS Max con Triangle Pelvis y Triangle Neck activos. Unity infirió mal el mapeo automático.

Solución: abrir **Configure...** en el tab Rig de cada FBX y verificar manualmente los 4 tabs (Body, Head, Left Hand, Right Hand). Todos los bones del Rocketbox aparecieron correctamente mapeados a sus equivalentes Humanoid — el warning era falso positivo. Unity logró mapear pese a la jerarquía no-estándar. Confirmamos con screenshots de los 4 tabs que los 15 finger bones por mano + neck/head + body están todos verdes.

---

### Sistema de pose sincronizada: `NetworkedAvatarPose` + `AvatarPoseDriver`

Pareja de componentes que reemplaza al sistema de presencia decorativo:

* **`NetworkedAvatarPose`** (NetworkBehaviour en el root del prefab): owner samplea XR Hands y HMD y los publica via NetworkVariable. Read by everyone, write by owner.
* **`AvatarPoseDriver`** (MonoBehaviour en cada sub-mesh): cada frame lee el state sincronizado y lo aplica al rig humanoid del sub-mesh activo.

Esquema de propagación:

```text
Owner machine                              All machines (incl. owner)
─────────────                              ──────────────────────────
XR Hands (joints)  →  NetworkVariable  →  AvatarPoseDriver  →  Bones + IK targets
HMD camera         →     (NGO sync)    →  (one per active     →  (Bip01 Head rot,
                                           sub-mesh)              IK target pos/rot,
                                                                  finger bone rots)
```

El owner ve su avatar driveado desde su propio state local; los remotos ven el avatar del owner driveado por el state recibido. Misma lógica idéntica, distintas fuentes de data.

#### Bug crítico: `IsOwner` check durante `OnEnable`

Síntoma inicial: la lógica `hideRenderersForLocalOwner` chequeaba `poseSync.IsOwner` en `OnEnable` del AvatarPoseDriver. Siempre devolvía `false` aún para el owner local.

Causa: `OnEnable` corre dentro de `NetworkSpawnManager.InstantiateNetworkPrefab`, **antes** de que NGO complete el spawn (que setea `OwnerClientId`). En ese instante `IsSpawned == false` y `IsOwner` siempre retorna `false`.

Fix: diferir el chequeo a un coroutine que espera hasta `poseSync.IsSpawned == true` (timeout 5s de seguridad):

```csharp
private void OnEnable()
{
    // ... resolver refs
    if (hideRenderersForLocalOwner)
        StartCoroutine(EvaluateOwnerVisibilityWhenSpawned());
}

private IEnumerator EvaluateOwnerVisibilityWhenSpawned()
{
    float deadline = Time.time + 5f;
    while (Time.time < deadline)
    {
        if (poseSync != null && poseSync.IsSpawned) break;
        yield return null;
    }
    bool isLocalOwner = poseSync != null && poseSync.IsOwner;
    // ... aplicar hide
}
```

Confirmado en logs:

```text
[AvatarPoseDriver] OnEnable on 'Avatar_Host': ... (IsOwner check deferred a post-spawn.)
[AvatarPoseDriver] Post-spawn: 'Avatar_Host' isLocalOwner=True. ...
```

---

### Alineación del avatar al piso: `AvatarFootAlignTool`

Síntoma original: al spawnear el avatar en `(0, 0, 0)`, los pies aparecían **debajo del piso**. El FBX de Rocketbox importa con el `Bip01` (root del Biped) en la cadera (~1m sobre el "suelo" del modelo), y Unity no compensa el offset automáticamente al wrappear con extra root.

Iteración 1 (fallida): leer la posición del bone `LeftFoot` via `Animator.GetBoneTransform()` y offsetear el sub-mesh para que la planta del pie quede en `y=0`. Resultado: el offset capturado fue `+0.1016`, pero al aplicarlo el avatar quedó **más** hundido. Diagnóstico: en Prefab Mode el Animator no garantiza tener los bones en bind pose evaluado; la posición devuelta era la transform "cruda" del FBX, no la bind pose correcta.

Iteración 2 (la elegida): usar `SkinnedMeshRenderer.bounds.min.y` en vez del bone foot. Esto da el punto más bajo de la **mesh visible** (sole del zapato), independiente de cualquier evaluación del rig. Captura confirmada en log:

```text
[AvatarFootAlign] 'Avatar_Host': mesh bottom was at localY=-1.0299 (via 'm013_hipoly_81_bones_opacity').
                  lp.y: 0.0000 → 1.0299 (delta +1.0299).
```

Los tres sub-meshes salieron con offset ~1.03m. Después del fix los pies quedan exactamente en el piso al spawnear.

Tool en `Assets/Editor/AvatarFootAlignTool.cs` con dos menús:

* **XR Collab → Avatars → Auto-Align Feet To Floor (Mesh Bounds)**: corre el cálculo arriba descrito.
* **XR Collab → Avatars → Reset Sub-Mesh Y Offsets**: deshace los offsets (útil cuando el primer intento falló y dejó valores incorrectos).

---

### Head bone driving: bind capture para preservar convención del Biped

Síntoma: la rotación del HMD se aplicaba directamente al `Bip01 Head` con `headBone.rotation = avatarRoot.rotation * hmdRotLocal`. Resultado: la cabeza del avatar apuntaba al techo.

Causa: el bone `Bip01 Head` del Rocketbox (convención Biped 3DS Max) tiene ejes locales **X=up, Y=forward** — no Z=forward como asume Unity. Sobreescribir `bone.rotation` con la rotación del HMD (que reporta Z=forward) re-mapea los ejes locales del bone: el +Y del bone (que era "forward de la cara") queda apuntando para arriba.

Fix: capturar la rotación del bone en bind pose **relativa al avatar root**, y multiplicarla como offset al aplicar el HMD:

```csharp
// Una vez, en el primer LateUpdate:
_headBindRelLocalRot = Quaternion.Inverse(_avatarRoot.rotation) * _headBone.rotation;

// Cada frame:
Quaternion worldRot = _avatarRoot.rotation * state.hmdRotLocal * _headBindRelLocalRot;
_headBone.rotation = worldRot;
```

Demostración matemática: cuando `state.hmdRotLocal == identity` (HMD mirando "adelante" del avatar), `worldRot == avatarRoot.rotation * headBindRelLocalRot == headBone.rotation_at_bind` — el head queda en bind pose. Cualquier delta del HMD desde el neutral rota el head bone por la misma cantidad en avatar-root-local space.

Confirmación visual: el cliente ve al host con la cabeza tracking correctamente. El cliente sin HMD (corriendo en ventana sin XR) muestra el avatar con cabeza mirando al techo — esperado, porque `state.hmdRotLocal == identity` para él, y el bind pose del Biped tiene `+Y` (= eje "up" semántico) apuntando al techo por la convención de ejes. No es un bug; es lo que pasa cuando no hay HMD que driveear la cabeza.

---

### Hand IK: tres estrategias iteradas hasta encontrar la geométrica

Esta fue la parte más difícil de la sesión. Documento las tres iteraciones porque las dos primeras enseñan por qué la tercera fue necesaria.

#### Estrategia 1 — Calibración manual con tecla C (descartada)

Mismo problema de convención de ejes que el head bone, pero más complejo porque la muñeca tiene 3 ejes que importan (forward de los dedos, palm normal, palm side) y XR Hands los reporta en su propia convención (Z=fingers forward, Y=palm up).

Primer intento: capturar la rotación del wrist en un momento "neutral" definido por el usuario (apretando tecla C con las manos en una pose conocida), y restar esa rotación de la rotación reportada:

```csharp
Quaternion ikRot = avatarRoot.rotation * state.wristRotLocal *
                   Inverse(wristCalibration) * handBindRelLocalRot * extraOffset;
```

Problema en uso real: el usuario tenía que mantener exactamente la pose de calibración en el momento del key press. En la práctica era imposible — apretar C movía la muñeca, y el resultado era una calibración incorrecta. Después de calibrar las dos manos por separado los resultados quedaban "casi bien" pero asimétricos.

Veredicto del usuario: "es muy difícil de usar".

#### Estrategia 2 — HUD de tuning manual con teclas Q/A/W/S/E/D (también descartada)

Agregado: `Assets/Multiplayer/AvatarHandTunerHUD.cs`. NetworkVariables `LeftWristOffsetEuler` / `RightWristOffsetEuler` (`Vector3`, owner-write) sincronizadas. El owner ajusta el offset Euler de cada muñeca con teclado:

| Tecla | Acción |
|---|---|
| 1 / 2 | seleccionar wrist L / R |
| Q/A · W/S · E/D | X +/- · Y +/- · Z +/- (step 5°) |
| Shift / Ctrl | step ×5 (25°) / ×0.2 (1°) |
| R | reset del wrist seleccionado a (0,0,0) |
| F4 | toggle visibilidad del HUD |
| Y / U / X | save/load/export a PlayerPrefs |

El HUD se renderea via OnGUI (monitor) + opcional Canvas world-space (VR). Los offsets se aplican como Euler residual encima del bind pose.

Problema: aún con HUD interactivo, el espacio de soluciones es de tres parámetros continuos por mano sin feedback "correcto/incorrecto" claro. Era prueba y error, lento, y los valores que parecían funcionar en una pose dejaban de funcionar en otra (porque el offset Euler tiene una orientación de referencia fija, no se adapta a la pose actual).

Veredicto: "la única forma en que funciona es colocar las manos imitando la posición original del avatar (con las muñecas torcidas)... necesario pero impráctico".

#### Estrategia 3 — Orientación geométrica desde 3 joints (la solución)

El insight del usuario: las **esferas del XR Hands debug** (representación de los joints) están perfectamente alineadas con las manos reales. Eso significa que los joint **positions** son data correcta y bien definida geométricamente — sin convenciones de ejes ambiguas. Reformulación del problema: en vez de tratar de mapear el wrist rotation reportado (que tiene convención propia) al bone Biped (que tiene otra), derivar la orientación de la palma directamente desde la geometría de 3 joints.

Validación visual previa a la implementación final: archivo nuevo `Assets/Multiplayer/AvatarHandJointDebugViz.cs`. Renderiza en escena:

* **Esferas verdes**: posiciones reales de Wrist + MiddleMetacarpal + ThumbMetacarpal del usuario (desde XR Hands)
* **Esferas rojas**: posiciones equivalentes en el rig del avatar (`Animator.GetBoneTransform(LeftHand)` + `LeftMiddleProximal` + `LeftThumbProximal`)
* **Línea magenta**: vector "fingers forward" (wrist → middle)
* **Línea cian**: palm normal (cross product `fingersForward × thumbDir`, flippeada para la izquierda)

En testing visual el usuario confirmó: las dos triadas de líneas (lado usuario, lado avatar) apuntaban en direcciones coincidentes cuando las manos estaban en orientaciones equivalentes. La estrategia geométrica iba a funcionar.

#### Implementación de la estrategia geométrica

Sincronización: `AvatarPoseState` (struct dentro de `NetworkedAvatarPose`) cambia. Reemplaza los campos `leftWristRotLocal` / `rightWristRotLocal` con `leftMiddleMetacarpalPosLocal` / `leftThumbMetacarpalPosLocal` (idem right). El wrist position sigue sincronizándose.

Computación del lado del receiver:

```csharp
// 1. Convertir las 3 posiciones a world.
Vector3 wristW = avatarRoot.TransformPoint(state.leftWristPosLocal);
Vector3 middleW = avatarRoot.TransformPoint(state.leftMiddleMetacarpalPosLocal);
Vector3 thumbW = avatarRoot.TransformPoint(state.leftThumbMetacarpalPosLocal);

// 2. Construir base ortonormal "palma del usuario" en world.
Vector3 fingersForward = (middleW - wristW).normalized;
Vector3 thumbDir = (thumbW - wristW).normalized;
Vector3 palmNormal = Vector3.Cross(fingersForward, thumbDir).normalized;
if (isLeft) palmNormal = -palmNormal;  // flip para mantener convención "up = dorso de la mano"
Quaternion userPalmWorldRot = Quaternion.LookRotation(fingersForward, palmNormal);

// 3. Aplicar la transformación que mapea la palma del usuario al frame del hand bone del avatar.
Quaternion ikRot = userPalmWorldRot * Quaternion.Inverse(_leftPalmRotInHand);
leftHandIKTarget.SetPositionAndRotation(wristW, ikRot);
```

Donde `_leftPalmRotInHand` es **la orientación de la palma en el frame local del hand bone**, capturada una sola vez al inicio:

```csharp
Vector3 fwd = handBone.InverseTransformPoint(middleProxBone.position).normalized;
Vector3 thumb = handBone.InverseTransformPoint(thumbProxBone.position).normalized;
Vector3 normal = Vector3.Cross(fwd, thumb).normalized;
if (isLeft) normal = -normal;
_leftPalmRotInHand = Quaternion.LookRotation(fwd, normal);
```

Justificación matemática de por qué `_leftPalmRotInHand` es invariante al runtime: `Transform.InverseTransformPoint` proyecta una posición world al frame LOCAL del transform. La relación geométrica entre `handBone`, `middleProxBone` y `thumbProxBone` está fijada por la jerarquía del rig (Animation Rigging y skinning no la modifican). Sus posiciones relativas en world cambian con la pose del avatar, pero en frame local del hand bone son constantes. Por lo tanto la palm orientation derivada de esas posiciones es constante.

Consecuencia operativa: **no hay timing issue** para la captura. Tradicionalmente capturar bind pose requiere que el Animator haya evaluado los bones; sin embargo InverseTransformPoint da el resultado correcto en cualquier frame, incluso con bones todavía no evaluados. El check de validez (`Vector3.Distance(handBone.position, middleProxBone.position) > 0.005f`) solo descarta el caso patológico donde los bones todavía no se inicializaron en absoluto.

Resultado: las manos del avatar se orientan correctamente sin offsets manuales ni calibración. Sin tuning. Independiente del rig (funcionaría con Mixamo o cualquier humanoid). El HUD de offsets Euler queda como fine-tuning residual opcional (default `Vector3.zero` para todas las muñecas).

---

### Finger driving: 15 bones por mano con FromToRotation

Misma estrategia geométrica extendida a los dedos. Cada uno de los 15 finger bones por mano (Thumb/Index/Middle/Ring/Little × Proximal/Intermediate/Distal) se rota cada frame para que apunte en la dirección equivalente a la del usuario.

#### Sincronización: nueva NetworkVariable

Struct `HandFingerPose` con 19 Vector3 por mano + 1 bool de validity. Total 38 Vector3 + 2 bools = ~460 bytes por update.

```csharp
public struct HandFingerPose : INetworkSerializable, IEquatable<HandFingerPose>
{
    // LEFT (19 joints en avatar-root-local space)
    public Vector3 lThumbProx, lThumbDist, lThumbTip;
    public Vector3 lIndexProx, lIndexInt, lIndexDist, lIndexTip;
    public Vector3 lMidProx, lMidInt, lMidDist, lMidTip;
    public Vector3 lRingProx, lRingInt, lRingDist, lRingTip;
    public Vector3 lLitProx, lLitInt, lLitDist, lLitTip;
    public bool lValid;
    // RIGHT (idem)
    // ...
}

public readonly NetworkVariable<HandFingerPose> Fingers = new NetworkVariable<HandFingerPose>(...);
```

Decisión arquitectónica: separar `Fingers` de `Pose` en dos NetworkVariables. Razón: NGO dedupea por valor de NetworkVariable. Si solo se mueve la cabeza, no se broadcastea el blob entero de fingers (~460 bytes adicionales). Solo si los joints de los dedos efectivamente cambian se manda el update.

Mapeo XR Hands → Humanoid bones:

| Avatar bone | XR start joint | XR end joint |
|---|---|---|
| ThumbProximal | ThumbMetacarpal | ThumbProximal |
| ThumbIntermediate | ThumbProximal | ThumbDistal |
| ThumbDistal | ThumbDistal | ThumbTip |
| (Index/Mid/Ring/Lit) Proximal | XR.XxxProximal | XR.XxxIntermediate |
| (Index/Mid/Ring/Lit) Intermediate | XR.XxxIntermediate | XR.XxxDistal |
| (Index/Mid/Ring/Lit) Distal | XR.XxxDistal | XR.XxxTip |

(El pulgar es especial porque anatómicamente solo tiene 2 falanges + metacarpal, mientras los demás dedos tienen 3 falanges. Unity Humanoid no expone metacarpal para los demás dedos, así que el ThumbProximal Humanoid corresponde al hueso metacarpal del pulgar.)

#### Algoritmo de driving

Para cada finger bone:

1. **Captura una vez**: `childDirInBoneLocal = bone.InverseTransformPoint(childBone.position).normalized`. Constante del rig (cancela la pose del bone via InverseTransformPoint, como con la palma).
2. **Cada frame**: computar la dirección del segmento equivalente del usuario en world (`(endJoint - startJoint).normalized`).
3. **Aplicar `Quaternion.FromToRotation`** para alinear el bone:

```csharp
Vector3 targetDir = (segEndWorld - segStartWorld).normalized;
Vector3 currentDir = slot.bone.TransformDirection(slot.childDirInBoneLocal);
Quaternion delta = Quaternion.FromToRotation(currentDir, targetDir);
slot.bone.rotation = delta * slot.bone.rotation;
```

`FromToRotation` da la rotación mínima entre dos vectores — no impone twist (rotación alrededor del eje del dedo se preserva). Para dedos cilíndricos eso es suficiente.

Orden de aplicación: Proximal → Intermediate → Distal en cadena, porque rotar el padre cambia la posición mundial del hijo y el siguiente cálculo de `currentDir` depende de la posición actualizada del bone.

Para el bone Distal no hay child Humanoid; se aproxima la dirección hacia el tip con `(distalBone.position - intermediateBone.position).normalized` re-expresada en frame local del distal. Como (intermediate → distal) y (distal → tip) son aproximadamente colineales en el rig, la aproximación es buena.

#### Bandwidth total post-finger-driving

```text
Pose:           ~80 bytes  (HMD + 3 wrist joints × 2 manos + bools)
HandFingerPose: ~460 bytes (19 joints × 2 manos + 2 bools)
Total:          ~540 bytes / update × 30 Hz ≈ 16 KB/s por avatar
```

Sigue siendo trivial sobre LAN. NGO sigue deduplicando por NetworkVariable (si los fingers no cambian no se broadcastea el blob).

#### Bug crítico durante el rollout: layout mismatch al hacer build

Síntoma: agregar el field `[SerializeField] private bool driveFingerBones` rompió el build standalone con:

```text
Type '[Assembly-CSharp]AvatarPoseDriver' has an extra field 'driveFingerBones' of type 'System.Boolean' in the player and thus can't be serialized.
Editor: 9 fields (sin driveFingerBones)
Player: 10 fields (con driveFingerBones)
Error building player because script class layout is incompatible between the editor and the player.
```

Causa: los `.prefab` ya tenían las instancias de `AvatarPoseDriver` serializadas con los 9 fields previos. El compilador del player veía 10 fields. El asset import no había re-serializado los prefabs con la nueva layout, y el build se hace con la última versión del source pero contra los assets viejos.

Fixes intentados: `Assets → Reimport All` (sin éxito), borrar `Library/ScriptAssemblies/` (sin éxito en esa sesión). El workaround pragmático fue cambiar el field a `private bool driveFingerBones = true` sin `[SerializeField]` — no se serializa al prefab, no hay mismatch, perdiendo solo la capacidad de toggle desde el Inspector. Para flippear el comportamiento en producción se edita el código y se recompila.

Esta misma decisión se aplicó retrospectivamente a otros toggles agregados en esta sesión: cuando un componente que ya tiene prefabs serializados gana un campo nuevo, usar `private const bool ...` o `private bool ... = X` sin SerializeField evita el problema.

---

### Visibilidad del avatar del owner: ocultar solo la cabeza

Síntoma con el avatar humanoide visible para todos: el host, al mirar para abajo, veía su propio torso/cuerpo en el HMD — perfecto para presencia. Pero al mirar para adelante veía **dentro de su propia cabeza** (la geometría facial del avatar interceptando la cámara stereo).

Primera solución (descartada): `hideRenderersForLocalOwner = true` que ocultaba **todos** los `SkinnedMeshRenderer` y `MeshRenderer` del sub-mesh del owner. Esto eliminaba el problema pero también ocultaba las manos del avatar, que el usuario necesita ver para jugar al Jenga (apuntar, alcanzar piezas).

Solución final: cambió la interpretación del flag `hideRenderersForLocalOwner`. En vez de ocultar todos los renderers, **escalar el head bone a casi cero**:

```csharp
if (isLocalOwner)
{
    if (_headBone != null)
    {
        _headBone.localScale = new Vector3(0.0001f, 0.0001f, 0.0001f);
    }
}
```

Justificación: los vértices del mesh skinned al head bone (cara, pelo, ojos, mandíbula, vía las child bones LCheek/LEye/etc.) colapsan a un punto microscópico en la posición del bone. Las child bones del head (LCheek, REye, etc.) heredan la scale `0.0001` y los vértices skinned a ellas también colapsan. El resto del cuerpo (torso, brazos, manos — skinned al neck, spine, shoulders, etc.) queda intacto.

Por qué `0.0001` y no `Vector3.zero`: scale exactamente cero genera matrices singulares en el skinning de Unity, que pueden producir NaNs en posiciones de vértices y crashear el render driver. `0.0001` da el mismo efecto visual (punto invisible a cualquier distancia razonable) sin problemas numéricos.

La rotación del head bone driveada por el HMD (a través de `driveHeadBone = true`) sigue funcionando — solo el tamaño visual es ~0. Eso es importante porque otros sistemas (por ejemplo, eye tracking gaze visualization si se agrega en el futuro) podrían leer la rotación del head bone.

---

### Limpieza de las visualizaciones de manos antiguas

Con el avatar humanoide funcional driveando bones + IK + dedos, las representaciones de manos legacy quedaron como decoración duplicada. Toggles `private const bool ShowVisualizers = false` agregados a:

| Archivo | Qué dibujaba | Para quién | Estado |
|---|---|---|---|
| `PinchDebugVisualizer.cs` | Esferas Thumb/Index + PinchLine local | HOST | Apagado (lógica de pinch detection conservada para grab) |
| `NetworkedAvatarHands.cs` | Esferas + PinchLine + RayDisplay sincronizados | TODOS | Apagado (sync se conserva por compat con sistemas que lean state) |
| `NetworkedAvatarSkeleton.cs` | 20 esferas + 5 LineRenderers por mano del avatar remoto | CLIENTE viendo HOST | Apagado (early-return en LateUpdate) |
| `HandSkeletonRenderer.cs` | Idem pero local del owner | HOST sí mismo | Apagado |
| `AvatarHandJointDebugViz.cs` | Esferas verdes/rojas de validación geométrica | Diagnóstico | `showViz` default `false` (F5 toggle si se necesita debug) |

Lo que QUEDA visible:

* El avatar humanoide completo (torso + brazos + manos con dedos) — visible para los remotos
* El avatar humanoide con cabeza oculta — visible para el owner local
* El raycast de grab (`JengaRayGrabInteractor.rayLine`) — local del owner, no fue tocado, mantiene grab interaction funcional

Decisión de usar `const bool` (no `[SerializeField]`): evita el problema de layout mismatch descrito arriba, y los toggles son flags de comportamiento global del componente, no settings por instancia.

---

### Estado actual

| Sistema | Estado |
|---|---|
| 3 avatares humanoides Rocketbox (Host/Client/Helper) con sub-mesh switching por rol | ✅ `AvatarHumanoid.prefab` + `AvatarRoleMeshSwitcher` |
| Bone mapping Humanoid de los 3 FBX (Body/Head/LeftHand/RightHand) | ✅ Validado manualmente en Configure de cada FBX |
| Validador de setup del prefab | ✅ `AvatarSetupValidator.cs` editor tool |
| Setup automático de materiales Rocketbox | ✅ `RocketboxMaterialSetup.cs` editor tool |
| Alineación de pies al piso via mesh bounds | ✅ `AvatarFootAlignTool.cs` (~1m delta auto-calculado) |
| Hide-owner-head (escalando head bone a 0.0001) | ✅ Owner ve cuerpo y manos, no su cara desde adentro |
| Driving del head bone con HMD (con bind capture) | ✅ Funciona, cabeza apunta donde mira el HMD |
| Hand IK target driveado por estrategia geométrica (3 joints → palm orientation) | ✅ Sin offsets manuales, sin calibración |
| Sincronización de pose por NetworkVariable (HMD + 3 joints por mano) | ✅ `Pose` NetworkVariable ~80 bytes/update |
| Finger driving: 15 bones por mano via FromToRotation | ✅ `Fingers` NetworkVariable ~460 bytes/update |
| Bandwidth total ≤ 16 KB/s por avatar | ✅ ~540 bytes × 30 Hz |
| Visualizaciones legacy desactivadas | ✅ 5 sistemas apagados via `ShowVisualizers = false` |
| Grab raycast del Jenga preservado | ✅ `JengaRayGrabInteractor.rayLine` no afectado |
| HUD de tuning manual de muñeca (legacy, ahora opcional) | ⚠️ Funcional pero default offsets = (0,0,0) — innecesario con estrategia geométrica |

---

### Caveats y limitaciones conocidas

* **Cliente sin HMD**: si el client corre desde el editor en ventana sin XR Hands ni HMD, su avatar (visto desde el host) tendrá cabeza apuntando al techo (`hmdRotLocal == identity` + bind pose del Biped) y manos sin tracking (todos los joints en cero). Comportamiento esperado, no es un bug; se documenta para evitar confusión en testing.
* **No hay interpolación**: a 30 Hz los joints de los dedos y las manos saltan visiblemente entre updates en el remote. Para una iteración futura, agregar buffer de 2 muestras + delay ~100 ms con blending lineal.
* **El bone Distal de cada dedo se aproxima**: no hay child humanoid para Distal en Unity (no expone "Tip" como bone), así que la dirección del distal se calcula como `(distalBone - intermediateBone)` re-expresada en frame local del distal. Aproximación buena en la mayoría de las poses; sutil error si el dedo está muy curvado.
* **El twist (rotación alrededor del eje del dedo) no se sincroniza**: `Quaternion.FromToRotation` es la rotación mínima entre dos direcciones, sin imponer twist. Para dedos cilíndricos esto da resultados visualmente correctos en todos los casos testeados.
* **Animator Controller debe estar en `None`** en cada sub-mesh: el AvatarPoseDriver asume que ninguna animation clip sobreescribe sus bone writes. Si hay un Controller con un state que anima los huesos, las correcciones del PoseDriver son sobreescritas por el Animator.
* **`Optimize Game Objects` debe estar OFF** en el rig settings de los FBX: si está ON, Unity hace "bone hiding" para optimización y `Animator.GetBoneTransform` retorna `null`. Confirmado OFF en los 3 FBX de Rocketbox.

---

### Pendiente / sugerido para futuras iteraciones

* **Interpolación de pose remoto**: buffer + delay para eliminar el snapping de 30 Hz. Aplicar a la NetworkVariable Pose y Fingers.
* **Recovery del Animator Controller**: actualmente sin controller = avatar en T-pose para body/legs (que casi no se ven, ocultos por el cuerpo en seated experience). Si en el futuro se ven más, agregar un Idle clip que mantenga el torso en pose sentada natural sin sobreescribir head/hand IK.
* **Animación procedural del pecho/hombro al mover los brazos**: las constraints `LeftArmIK`/`RightArmIK` configuradas en Animation Rigging mueven el wrist via IK, pero el shoulder/clavícula no se compensan. Resultado: si el usuario estira mucho el brazo, el shoulder queda fijo y el brazo se ve "rígido". Iteración: agregar `MultiAimConstraint` sobre la clavícula para que rote suavemente siguiendo al hand IK target.
* **Eye tracking sobre el avatar**: el sistema BioLab UDP ya provee gaze data del HMD. Aplicarlo a las eye bones (`LeftEye`, `RightEye` en Humanoid) para que los avatares se miren entre sí. Big presence win.
* **Limpiar código legacy**: una vez confirmada la estrategia geométrica, hay código muerto que se puede borrar:
  * `LeftWristCalibration` / `RightWristCalibration` NetworkVariables (no se usan más)
  * `CalibrateHands()` (ya es no-op)
  * Partes de `AvatarHandTunerHUD` (la calibración con V, el flujo de save/load, etc.)
* **Documentar los tooltips de los SerializeField**: en `AvatarPoseDriver` el campo `hideRenderersForLocalOwner` ahora significa "ocultar solo la cabeza" pero el nombre del field es legacy. Si se renombra hay que actualizar el prefab — alternativamente, dejar el nombre y mejorar el tooltip (ya hecho).
* **Validar performance en VR con todo activo**: medir con `PerformanceHUD` (F3) que el FPS sostenido sigue ≥ 72 con 3 avatares humanoides + finger driving + Jenga physics + NGO + eye tracking. Si baja, identificar bottleneck (probablemente los skinned mesh renderers de los 3 sub-meshes — aunque solo uno está activo por avatar).

---

### Mantenimiento del branch

Esta sesión consolida el sistema de avatares humanoides como reemplazo del esqueleto decorativo previo (`NetworkedAvatarSkeleton` + `HandSkeletonRenderer`). Los componentes nuevos son **runtime + editor tools**:

* Runtime: `AvatarPoseDriver`, `AvatarHandJointDebugViz`, `AvatarHandTunerHUD`, modificaciones a `NetworkedAvatarPose`
* Editor tools: `AvatarSetupValidator`, `RocketboxMaterialSetup`, `AvatarFootAlignTool`

Los visualizers viejos (`NetworkedAvatarSkeleton`, `HandSkeletonRenderer`, `PinchDebugVisualizer`, `NetworkedAvatarHands`) **se conservan en el repo** pero con sus `ShowVisualizers` apagados. Esto permite revertir a la representación de esqueleto si en el futuro se decide volver atrás (por ejemplo, si el costo de render del avatar humanoide impacta performance en algún caso).

Cuando se merge a main: los assets del prefab `AvatarHumanoid.prefab` + los 3 FBX de Rocketbox bajo `Assets/Avatars/Rocketbox/` son los productos finales. Los editor tools quedan como utilidades para regenerar setup si se agregan más rigs en el futuro.

---

## 2026-05-23 — Look-and-feel: nueva escena con HDRP Furniture Pack, iluminación bakeable y materiales PBR

Branch nuevo: `look-and-feel-prototype` (creado desde `main` después de mergear `jenga-physics-prototype`).

Motivación: la escena `Room.unity` previa funcionaba operativamente pero visualmente era una sucesión de cubos primitive con Standard materials sin textura ni normales (suelo/paredes/techo color plano, mesa coffee table chica, sillas y muebles inexistentes). Sin presencia ambiental, los participantes sentían que estaban en un "test technical demo" en vez de una habitación. Esta sesión rearma el cuarto desde cero usando assets del `HDRPFurniturePack` (ya importado pero no usado por incompatibilidad de pipeline) + bake de lightmaps + PBR textures.

Constraint clave: **bajo costo de runtime + baja latencia para VR**. Built-in render pipeline se mantiene (no se migró a URP/HDRP para no introducir regresiones en sistemas estables como hand tracking, networking, eye tracking, BioLab UDP).

---

### Estrategia: cinco tiers de mejoras estéticas

Antes de implementar, se analizó el espacio de mejoras posibles y se ordenó por ROI (impacto visual / costo de performance):

| Tier | Categoría | Ejemplos | Costo runtime |
|---|---|---|---|
| **0** | Flips de settings (gratis) | Single Pass Instanced, Linear Light Intensity, Color Temperature, shadow distance/cascades, reflection resolution | **Nulo o negativo** (mejora performance) |
| **1** | Baking (precomputado, cero runtime) | Lightmaps, light probes, AO bakeado, reflection probes baked | **Nulo** (mueve trabajo a offline) |
| **2** | PBR textures | Albedo + Normal + AO + Roughness para suelo/paredes/techo/muebles | **Mínimo** (~3 KB/m² de textura, despreciable) |
| **3** | Post-processing | ACES tonemap, bloom sutil, color grading | **+1-2 ms/eye** |
| **4** | Iluminación cinematográfica | Three-point sobre la mesa, lámpara de pie con luz real, HDRI skybox | **Variable** (depende de modos Mixed/Baked) |
| **5** | Polish / props | Polvo en el aire, props decorativos pequeños | **Mínimo** |

En esta sesión se implementaron principalmente **Tiers 0, 1, 2, 4** (parcial). Tier 3 (post-processing) y Tier 5 (props finos) quedan pendientes.

---

### Tier 0 — Settings flips (ProjectSettings, Quality, Scene)

Cambios aplicados via edición directa de los YAML de `ProjectSettings/`:

| Setting | Archivo | Antes | Después | Justificación |
|---|---|---|---|---|
| `m_LightsUseLinearIntensity` | `GraphicsSettings.asset` | 0 | **1** | Cómputo de iluminación en linear space, falloff más natural. Costo nulo. |
| `m_LightsUseColorTemperature` | `GraphicsSettings.asset` | 0 | **1** | Permite especificar luces en Kelvin (5000K neutro, 2800K incandescente, etc.). Costo nulo. |
| Ultra → `shadowDistance` | `QualitySettings.asset` | 150 m | **15 m** | La habitación es de 6m. Concentra el shadow map en el área visible, multiplica resolución efectiva por ~10×. |
| Ultra → `shadowCascades` | `QualitySettings.asset` | 4 | **2** | Indoor no necesita 4 cascadas. Menos splits = más resolución por cascada. |
| Very High → `shadowDistance` | `QualitySettings.asset` | 70 m | **15 m** | Idem Ultra (consistencia entre quality levels usables en VR). |
| `m_DefaultReflectionResolution` | `Scenes/Room.unity` (RenderSettings) | 128 | **256** | Reflejos del fallback skybox más definidos para superficies con smoothness > 0. Costo de memoria: ~768 KB. |

Verificación adicional: **Stereo Rendering Mode ya estaba en Single Pass Instanced** (`m_renderMode: 1` en `OpenXR Package Settings.asset`). El `m_StereoRenderingPath: 0` que figuraba en `ProjectSettings.asset` es del sistema XR legacy, ignorado por OpenXR.

Impacto medido (estimado, headset Vive Focus Vision tethered):

* Shadow distance 150 → 15: **−1.5 a −3 ms por eye** (menos drawcalls de shadow casters).
* Shadow cascades 4 → 2: **−0.5 a −1 ms por eye**.
* Linear Intensity + Color Temp: ~0 ms.
* Reflection 128 → 256: +0.05 ms (despreciable).

Net: **2-4 ms ahorrados por eye** con look mejor.

---

### Conversión HDRP → Built-in del Furniture Pack

Archivo nuevo: `Assets/Editor/HdrpToBuiltinConverter.cs`.

Problema: el `HDRPFurniturePack` (sillas Artek, mesa Aalto, sofá, plantas, alfombras, candelabros, lámpara de pie, cuadros) estaba importado pero **inusable en Built-in RP** porque sus materiales usan el shader `HDRP/Lit`. En Built-in sin HDRP package, los materiales aparecen en magenta (shader missing).

Tool: menú **XR Collab → Materials → Convert HDRP Furniture Pack to Built-in Standard**.

Mapping de propiedades:

```text
_BaseColorMap (HDRP)     -> _MainTex (Built-in)
_BaseColor               -> _Color
_NormalMap               -> _BumpMap  (textureType import set a NormalMap)
_NormalScale             -> _BumpScale
_MaskMap (HDRP)          -> _MetallicGlossMap + _OcclusionMap (mismo asset)
                            HDRP MaskMap: R=Metallic, G=AO, B=DetailMask, A=Smoothness
                            Built-in MetallicGlossMap: R=Metallic, A=Smoothness  -> match directo
                            Built-in OcclusionMap: G channel                       -> match directo
_OcclusionMap (separado) -> _OcclusionMap (si existe, prevalece sobre MaskMap)
_Metallic                -> _Metallic
_Smoothness              -> _Glossiness
tiling/offset            -> idem
```

Output: `Assets/HDRPFurniturePack_BuiltIn/<OriginalName>_Builtin.mat` por cada material HDRP encontrado.

Robustez de lectura: la primera pasada usa `Material.GetTexture()` directo; si la propiedad no existe (shader missing → `HasProperty` retorna false), cae a un `SerializedObject` que lee del YAML del `.mat` directamente. Esto permite convertir materiales con shader HDRP no instalado en el proyecto.

Tool complementaria: **XR Collab → Materials → Swap Materials on Selected (HDRP → Builtin)** para aplicar los `_Builtin.mat` a renderers de instancias en escena. Buscar por `name + "_Builtin"`.

#### Bug crítico encontrado y corregido: pérdida de GUIDs al re-ejecutar

Primera versión del converter: `AssetDatabase.DeleteAsset(outPath); AssetDatabase.CreateAsset(newMat, outPath);` — borrar y recrear. Esto **cambia el GUID del asset**, por lo que cualquier renderer en escena que referenciaba el material viejo queda con referencia rota y se ve magenta.

Fix: mutar el material existente in-place vía `Material.CopyPropertiesFromMaterial(src)`. Conserva el GUID, todas las referencias en escena siguen funcionando. Idempotente — re-ejecutar el menú actualiza propiedades sin romper nada.

```csharp
var existing = AssetDatabase.LoadAssetAtPath<Material>(outPath);
if (existing != null)
{
    existing.shader = standardShader;
    existing.CopyPropertiesFromMaterial(newMat); // preserve GUID
    existing.enableInstancing = true;
    EditorUtility.SetDirty(existing);
    Object.DestroyImmediate(newMat); // discard temp
}
else
{
    AssetDatabase.CreateAsset(newMat, outPath);
}
```

#### Tool de recuperación: `Recover Magenta Materials from Prefab Source`

Para reparar las referencias rotas dejadas por la versión buggy del converter, se añadió un menú adicional que:

1. Por cada Renderer en la selección, obtiene el prefab source vía `PrefabUtility.GetCorrespondingObjectFromSource()`.
2. Lee el material HDRP original que ese slot tiene en el prefab asset.
3. Construye el path candidato `_Builtin.mat` por nombre.
4. Asigna el material recuperado al slot.

Resolvió en una pasada los renderers rotos de `RoomFurniture` tras el incidente del GUID change.

#### Fallback de Albedo desde `_DetailAlbedoMap`

Algunos materiales del pack (notablemente `Wall_Decoration_Art_Zebra` y `Table_97_Artek_Wood_natural`) **no asignan textura en `_BaseColorMap`** — usan el "detail" slot como textura principal con un base color tint blanco. El converter inicial leía sólo `_BaseColorMap` y por eso esos muebles aparecían blancos.

Fix en `ConvertOne`: cascada `_BaseColorMap → _DetailAlbedoMap → _MainTex (legacy)`. El primer slot no vacío se mapea a `_MainTex` del material Built-in. Log explícito cuando se usa fallback para que se sepa qué textura está activa.

---

### Construcción del shell del cuarto

Archivo nuevo: `Assets/Editor/RoomShellBuilder.cs`. Menú: **XR Collab → Room → Create Room Shell**.

Crea un GameObject `RoomNew` con 6 hijos (Floor, Ceiling, WallNorth/South/East/West) que forman una habitación cerrada.

#### Iteración 1 (descartada): Quads single-sided con rotaciones explícitas

Primera implementación: cada superficie es un `PrimitiveType.Quad` rotado para que su normal apunte hacia el interior del cuarto.

Quad de Unity tiene normal default = `-Z`. Rotaciones aplicadas:

```text
Floor       -> Quaternion.Euler(-90, 0, 0)   normal final: -Y  ← INCORRECTO (debería ser +Y)
Ceiling     -> Quaternion.Euler(+90, 0, 0)   normal final: +Y  ← INCORRECTO (debería ser -Y)
WallSouth   -> Quaternion.Euler(0, 180, 0)   normal final: +Z  ✓
WallNorth   -> Quaternion.identity            normal final: -Z  ✓
WallEast    -> Quaternion.Euler(0, +90, 0)   normal final: -X  ✓
WallWest    -> Quaternion.Euler(0, -90, 0)   normal final: +X  ✓
```

Resultado en VR: paredes visibles desde adentro, **piso y techo invisibles** (la normal apuntaba hacia afuera, back-face culling los volvía transparentes desde la posición del usuario). Hipótesis inicial de "es problema de iluminación, las caras están en sombra y se ven negro" fue descartada después de un test con material Unlit rojo (red aparecía solo desde abajo del piso, no desde arriba).

#### Iteración 2 (la elegida): Cube slabs finos

Cada superficie es un `PrimitiveType.Cube` con scale apropiado (slab fino):

| Superficie | Position | Scale (m) |
|---|---|---|
| Floor | `(0, -0.05, 0)` | `(6, 0.1, 6)` |
| Ceiling | `(0, 3.05, 0)` | `(6, 0.1, 6)` |
| WallSouth | `(0, 1.5, -3.05)` | `(6, 3, 0.1)` |
| WallNorth | `(0, 1.5, +3.05)` | `(6, 3, 0.1)` |
| WallEast | `(+3.05, 1.5, 0)` | `(0.1, 3, 6)` |
| WallWest | `(-3.05, 1.5, 0)` | `(0.1, 3, 6)` |

Cubes tienen las 6 caras visibles desde afuera. Cada slab está **fuera del volumen interior del cuarto**, así que la cara interior de cada slab es la que mira al usuario → siempre visible, sin pelearse con direcciones de normales.

Ventajas extra sobre Quads:

* BoxCollider nativo del Cube primitive → piso bloquea físicas de Jenga blocks, paredes bloquean avatares si caminan demasiado.
* Espesor físico real (10cm) → look más natural desde fuera (vistas no-VR).
* No depende de rotaciones precisas.

Costo de geometría: 72 tris totales (12 por cube × 6) — despreciable. Marcado todo Static automáticamente para entrar al bake.

Materiales asignados desde `Assets/FloorMaterial.mat` / `WallMaterial.mat` / `CeilingMaterial.mat` (con PBR Wood048 para piso, plaster para paredes/techo).

---

### Iluminación cinematográfica (Tier 4 parcial)

Archivo nuevo: `Assets/Editor/RoomLighting.cs`. Menú: **XR Collab → Room → Setup Lighting**.

Crea un GameObject `RoomLighting` con 4 hijos + tweak de RenderSettings:

| Luz | Tipo | Color Temp | Intensity | Modo | Función |
|---|---|---|---|---|---|
| `DirectionalLight` | Directional | 5000 K | 0.5 | Mixed | "Sol" indirecto suave entrando por hipotéticas ventanas. Reconfigura el directional existente si ya hay uno. |
| `KeyLight_Table` | Spot | 4000 K | 5 | Mixed | Cenital sobre la mesa. Posicionado 1.8m arriba del `JengaTowerGenerator.transform.position`. Spot angle 60° (90° tras tunear), shadows soft. |
| `FillLight_Center` | Point | 2800 K | 1.5 | Baked | Ambient cálido a 2/3 de altura del cuarto. Llena esquinas oscuras, no proyecta sombras (ahorra costo). |
| `RoomReflectionProbe` | Reflection Probe | — | — | Baked | Box-shaped del tamaño del cuarto, parallax-corrected, resolución 256. Genera reflejos genuinos en superficies con smoothness > 0.3. |

Tweak global: `RenderSettings.ambientIntensity` subido de 1.0 → 1.2.

Decisiones de modos:

* **Mixed para Directional + KeyLight**: las luces directas siguen siendo realtime (sombras dinámicas sobre Jenga blocks móviles), pero el bounce/indirect se bakea.
* **Baked para FillLight**: la luz completa se bakea en lightmap. Cero costo runtime. No afecta objetos dinámicos directamente — para ellos se usan Light Probes (siguiente sección).
* **Baked para Reflection Probe**: precomputa los reflejos al hacer Generate Lighting. Cero costo runtime después.

#### Issue: spot light descentrado

Caso real durante prueba: el `KeyLight_Table` se posicionó sobre el `JengaTowerGenerator`, pero ese transform no coincidía exactamente con el centro de la mesa Aalto. El spot quedó descentrado, iluminando solo una porción del top de la mesa y dejando el resto en sombra dura.

Fix mientras se afina: subir spot angle de 60 → 90, bajar intensity de 5 → 2.5, o convertirlo a Point light si la geometría no se preocupa por la dirección de la luz. Se documenta como ajuste manual post-script.

---

### Población de muebles

Archivo nuevo: `Assets/Editor/RoomPopulator.cs`. Menú: **XR Collab → Room → Populate Furniture**.

Instancia un layout predefinido bajo un GameObject `RoomFurniture`, aplica swap de materiales `HDRP → Built-in` automáticamente, marca todo Static para bake.

Layout final usado (después de iteración para encajar setup "seated" con mesa real):

| Mueble | Prefab | Función |
|---|---|---|
| Mesa cuadrada Aalto | `Table_97_Artek` (sustituye al Coffee_Table_90D inicial, muy chico) | Superficie de Jenga (~73 cm altura, 95×95 cm) |
| 3 banquetas | `Stool 60 Artek` | Una por participante (Host, Client, Helper). Posicionables a mano según silla física real. |
| Alfombra | `Rug_High_Pile_Grey` | Bajo la mesa, contraste de textura |
| Sofá | `Sofa_SL03_Allemuir_Stirling` | Costado del cuarto, llena espacio |
| Planta | `Plant_Potted_Monstera_Deliciosa` | Esquina noreste, vida |
| Lámpara de pie | `Floor_Light_A810_Artek` | Esquina noroeste, decorativa (no emite luz real todavía) |
| Cuadro decorativo | `Wall_Decoration_Art_Zebra` | Punto focal en pared norte |

Override de posición de mesa: si existe `JengaTowerGenerator` en escena, la mesa se posiciona en `JengaTowerGenerator.transform.position` con `y = 0`. Asegura que la torre de Jenga queda sobre la superficie de la mesa nueva.

Setup "passive haptics seated": los 3 participantes estarán **en la misma habitación física real** sentados alrededor de una mesa física. Las 3 banquetas virtuales se reposicionan manualmente para coincidir con las posiciones físicas reales. Tactil real + visual virtual = presencia maximizada.

---

### Light Probes para objetos dinámicos

Archivo nuevo: `Assets/Editor/RoomProbeGenerator.cs`. Menú: **XR Collab → Room → Generate Light Probes**.

Razón: los Jenga blocks, manos del XR rig, avatares networked — son objetos **dinámicos** (no Static). Los lightmaps NO les aplican. Sin Light Probes, esos objetos no reciben luz indirecta del cuarto y se ven "out of place" (planos, sin GI adaptada al entorno).

Distribución de 35 probes generada:

* **Grid principal**: 3 capas (Y = 0.4, 1.5, 2.5 m) × 3×3 en XZ = 27 probes. Margen 0.5m del borde de la habitación.
* **Cluster denso alrededor de la mesa**: 4 esquinas × 2 alturas (0.75 y 1.20 m) = 8 probes. Centrado en el `JengaTowerGenerator` si existe. Densifica donde más se usan los Jenga blocks (la actividad principal).

API: `LightProbeGroup.probePositions = Vector3[]`. Una sola componente que contiene todas las posiciones.

---

### Bake de lightmaps (Tier 1)

Configuración usada en `Window → Rendering → Lighting → Scene`:

| Setting | Valor |
|---|---|
| Lighting Mode | Baked Indirect |
| Lightmapper | Progressive GPU |
| Direct Samples | 32 |
| Indirect Samples | 512 |
| Bounces | 2 |
| Filtering | Auto |
| Lightmap Resolution | 40 texels/m |
| Lightmap Size | 1024 |
| Compress Lightmaps | ON |
| Ambient Occlusion | ON (Max Distance 1, Indirect/Direct 1.0) |
| Auto Generate | **OFF** (re-bakea automáticamente al cambiar cualquier cosa, anti-flow) |

Tiempo de bake: ~20 min para el cuarto chico con ~10 muebles.

Resultado: bounce light entre piso/paredes/muebles, AO en junturas, Reflection Probe activo (reflejos en superficies con smoothness > 0.3). Light Probes interpolan ambient para Jenga blocks móviles.

---

### Texturas PBR (Tier 2)

Wood048 de AmbientCG (CC0): se descargó el zip `Wood048_1K-JPG.zip` a `Assets/Textures/Room/Wood048/`. Texturas usadas: Color, NormalGL, Roughness.

#### Bugs de asignación encontrados

**Bug 1**: el `_MainTex` (Albedo) de la mesa quedó apuntando a `Wood048.png` — el archivo de preview thumbnail que AmbientCG incluye en el zip para mostrarse en su sitio web, NO la textura real. El PNG es de baja resolución, washed out. Cualquier asignación drag-and-drop "rápida" al primer archivo .png/.jpg encontrado caía sobre el preview.

Fix: cambiar la referencia en el YAML del material a `Wood048_1K-JPG_Color.jpg` (GUID `fb4a20145230fdb40909e3f4b891dd93`).

**Bug 2**: los 3 materiales de bloque Jenga (`BlockRed.mat`, `BlockBlue.mat`, `BlockYellow.mat`) **no tenían texturas asignadas** — solo color tint plano (rojo, azul, amarillo puros). En la escena previa los bloques se veían como cubos de plástico colorido, sin grain de madera.

Fix: edición directa del YAML de cada material para asignar:
* `_MainTex` → `Wood048_1K-JPG_Color.jpg`
* `_BumpMap` → `Wood048_1K-JPG_NormalGL.jpg`
* Habilitar keyword `_NORMALMAP`
* `_BumpScale` ajustado a 0.3 (sutil en bloques chicos de 1.5cm de alto)

**Bug 3**: el tint del bloque azul `(0, 0, 1)` era azul puro. Multiplicado contra wood (~0.4, 0.3, 0.2 RGB en albedo):

```text
R: 0   * 0.4 = 0
G: 0   * 0.3 = 0
B: 1.0 * 0.2 = 0.2
```

Los canales R y G quedan en 0, perdiendo toda la variación del Albedo. El bloque azul se veía como un sólido azul oscuro sin grain visible.

Fix: tint `(0.177, 0.359, 1.0)` — azul claro estilo "stain" que preserva las vetas visibles porque ningún canal del tint queda en 0. Multiplicado:

```text
R: 0.177 * 0.4 = 0.07
G: 0.359 * 0.3 = 0.11
B: 1.0   * 0.2 = 0.2
```

Resultado: bloque azul-grisáceo con grain visible. Mismo principio aplicable a otros tints muy saturados: nunca dejar un canal en 0 si querés ver la textura subyacente.

---

### Estado actual

| Sistema | Estado |
|---|---|
| Tier 0 settings (Linear/ColorTemp/Shadow distance/etc.) | ✅ Aplicado vía YAML, performance + look mejorados |
| HDRP → Built-in material converter | ✅ Robusto, idempotente, preserva GUIDs, fallback a `_DetailAlbedoMap` |
| Material recovery tool para magenta | ✅ Reparable vía menú |
| Shell del cuarto (6 Cube slabs, 72 tris, BoxColliders nativos) | ✅ `RoomNew` con Floor/Ceiling/4 walls |
| Muebles instanciados con materiales convertidos | ✅ `RoomFurniture` con 9 muebles del pack |
| Iluminación cinematográfica (Directional Mixed + Spot Key + Point Fill + Reflection Probe) | ✅ `RoomLighting` |
| Light Probes (35 distribuidos, denso alrededor de la mesa) | ✅ `RoomLightProbes` |
| Bake de lightmaps completado | ✅ Baked Indirect + AO + Reflection probe baked |
| PBR Wood048 en piso, paredes (yeso), techo (yeso) | ✅ Albedo + Normal + Roughness en los 3 materiales del shell |
| PBR Wood048 en mesa + bloques Jenga | ✅ Albedo + Normal en `Table_97_Artek_Wood_natural_Builtin` + `BlockRed/Blue/Yellow.mat` |
| Tint del bloque azul ajustado para preservar grain | ✅ `(0.177, 0.359, 1.0)` |

---

### Pendiente / sugerido para futuras iteraciones

* **Tier 3 — Post-processing**: ACES tonemap + bloom sutil (intensidad 0.2-0.4) + color grading. Costo en VR ~1-2 ms/eye. **NO usar Depth of Field, Motion Blur, Chromatic Aberration ni Lens Flares en VR** — todos rompen presencia o causan mareo.
* **Tier 5 — Polish props**: partículas de polvo flotando muy sutilmente para "atmósfera viva". Pequeños props decorativos (libros sobre mesa con `Coffee_Table_Books`, candelabro `Taper_Candle_Holders` apagado). Cuidar de no recargar la escena.
* **Lámpara de pie con luz real**: el `Floor_Light_A810_Artek` actualmente es solo decorativo. Agregarle un Point Light hijo (2800K, intensity 2, range 3, Baked) que coincida con la posición del bulb del modelo. Vendería mucha calidez al cuarto.
* **HDRI skybox**: reemplazar el procedural skybox por un HDRI de interior (Polyhaven CC0). El cuarto está cerrado pero el ambient lighting calculado del skybox sigue siendo el "exterior virtual" del cuarto. Un HDRI de estudio o ventana grande haría más realista lo poco que se filtra.
* **Texturas PBR distintas por mueble**: actualmente Wood048 está en piso, mesa y bloques (consistencia, mismo material en todo). Considerar si la mesa Artek debería tener un birch más claro para diferenciarse del piso oak. Trade-off entre coherencia y variedad visual.
* **Posicionamiento de las 3 banquetas según setup físico real**: cuando se calibre el laboratorio, mover las 3 `Stool_*` a las posiciones físicas exactas de las sillas reales. Para passive haptics que el tactil coincida con lo visual.
* **Tunear `JengaTowerGenerator.transform.position.y`**: el cambio de mesa coffee table (~40cm alto) → Table 97 (~73cm alto) requiere subir el `transform.position.y` del Jenga generator para que la torre quede sobre la superficie nueva.
* **Validar performance en VR con el bake activo**: medir con `PerformanceHUD` (F3) si el FPS sostenido es ≥ 72 con todos los sistemas activos (NGO + skeleton + scene baked). Si baja, identificar drawcall culpable (probablemente el sofá o la planta tienen mesh complejo).

---

### Mantenimiento del branch

Este branch (`look-and-feel-prototype`) está pensado para **mergeable a main** cuando se confirme el look final. A diferencia de `jenga-physics-prototype` (que es sandbox de tuning con scaffolding que no se merge), aquí los cambios son **cambios de assets + scripts de Editor reusables**:

* Los `RoomShellBuilder`, `RoomLighting`, `RoomPopulator`, `RoomProbeGenerator`, `HdrpToBuiltinConverter` quedan como **Editor tools** disponibles por menú. No agregan código de runtime.
* Los assets generados (materials Built-in, lightmaps bakeados, scene actualizada) son los productos finales que se promueven.

Cuando se merge a main, mantener los 5 Editor scripts permite re-generar partes del cuarto si en el futuro se cambia layout o se agregan muebles.

---

## 2026-05-21 — Representación de manos por esqueleto (local y networked) para mejorar presencia

Branch activo: `multiplayer-prototype-fixed` (mismo día, sesión continuada).

Motivación: los avatares previos sólo mostraban tres marcadores por mano (thumb tip, index tip, palm) más un `LineRenderer` de pinch y otro de ray. Visualmente se sentía "puntos flotantes en el aire", insuficiente para presencia compartida en una tarea de colaboración. Se introduce un esqueleto de mano completo (20 joints por mano + 5 huesos por mano), priorizando bajo costo de render y nula intrusión sobre los sistemas existentes de poke / drag / raycast.

Estrategia decidida después de evaluar opciones:

| Opción | Joints | Costo render | Costo network | Decisión |
|---|---|---|---|---|
| Tips reducido | 6 / mano | Muy bajo | Muy bajo | Descartada (poca presencia) |
| Knuckles + tips | 11 / mano | Bajo | Bajo | Descartada (intermedia, no aporta sobre la siguiente) |
| **Esqueleto sin metacarpales** | **20 / mano** | **Bajo con instancing** | **Medio** | **Elegida** |
| Hand mesh skinned | n/a | Alto | Complicado | Descartada (decorativo, no vale la complejidad) |

Implementación incremental en cuatro pasos:

* Paso 1: render local del esqueleto.
* Paso 1.5: huesos (LineRenderers).
* Paso 1.6: color del rol aplicado al esqueleto local.
* Paso 2: sincronización por red para que los esqueletos de los demás participantes sean visibles.

---

### Paso 1 — `HandSkeletonRenderer` local con `Graphics.DrawMeshInstanced`

Archivo nuevo:

```text
Assets/Multiplayer/HandSkeletonRenderer.cs
```

Componente standalone de escena (no NetworkBehaviour). Cada nodo renderiza sus propias manos leyendo `XRHandSubsystem` directamente, sin pasar por la red. Esto da latencia 0 para el avatar local.

Subconjunto de joints elegido (20 por mano, se omiten Palm y los 5 metacarpales por estar dentro de la palma y aportar poco visualmente):

```text
0:  Wrist
1-3:   ThumbProximal, ThumbDistal, ThumbTip                                  (pulgar sin Intermediate)
4-7:   IndexProximal, IndexIntermediate, IndexDistal, IndexTip
8-11:  MiddleProximal, MiddleIntermediate, MiddleDistal, MiddleTip
12-15: RingProximal, RingIntermediate, RingDistal, RingTip
16-19: LittleProximal, LittleIntermediate, LittleDistal, LittleTip
```

Pipeline de render para las esferas:

* `Graphics.DrawMeshInstanced(sphereMesh, 0, jointMaterial, _matrices, _activeCount, _mpb, ShadowCastingMode.Off, false, layer, null, LightProbeUsage.Off)`.
* `_matrices = new Matrix4x4[40]` preasignado (20 joints × 2 manos). Cero alloc por frame.
* **Una sola draw call** para las hasta 40 esferas de ambas manos.
* `ShadowCastingMode.Off` + `receiveShadows: false` + `LightProbeUsage.Off` para minimizar costo.

Joints en local space del tracking origin se transforman a world space vía `XROrigin.CameraFloorOffsetObject.transform.TransformPoint`, mismo enfoque que `NetworkedAvatarHands` y `HandRayDriver`.

Fallbacks runtime para no requerir setup en Inspector:

* Mesh: si `sphereMesh` está vacío, se toma prestado el `sharedMesh` del primitive `Sphere` (768 tris, suficiente para validar; documentado como reemplazable por un icosphere low-poly de 20-80 tris).
* Material: si `jointMaterial` está vacío, se crea un `new Material(Shader.Find("Standard"))` con `enableInstancing = true` y `Glossiness/Metallic = 0`. Marcado como `_materialOwned = true` para destruirlo en `OnDestroy`.

Garantías de no-interferencia con sistemas existentes:

* No agrega colliders (DrawMeshInstanced es pura GPU).
* No spawnea GameObjects por joint.
* No toca el `XR Origin`, los markers existentes, los anchors de ray del Jenga ni la lógica de NGO.
* Corre en `LateUpdate` (después de Update/FixedUpdate, no afecta physics ni input).

Defaults afinados empíricamente:

* `jointRadius = 0.004` (4 mm — usuario validó 6 mm era demasiado grande).
* `fallbackColor = (1, 1, 1, 0.9)` (blanco semitransparente como base, se sobreescribe al bindear al rol).

---

### Paso 1.5 — Huesos vía `LineRenderer`

Sobre `HandSkeletonRenderer` se añaden 5 `LineRenderer` por mano (10 totales), uno por dedo, conectando wrist → proximal → intermediate → distal → tip. Para el pulgar es wrist → proximal → distal → tip (sin Intermediate).

Topología codificada en tabla estática:

```csharp
private static readonly int[][] s_fingerChains = new int[][]
{
    new int[] { 0, 1, 2, 3 },         // Pulgar (4 puntos)
    new int[] { 0, 4, 5, 6, 7 },      // Índice (5 puntos)
    new int[] { 0, 8, 9, 10, 11 },    // Medio
    new int[] { 0, 12, 13, 14, 15 },  // Anular
    new int[] { 0, 16, 17, 18, 19 },  // Meñique
};
```

Decisiones de implementación:

* Los LineRenderer se crean perezosamente en el primer `LateUpdate` (lazy init), no en `Awake`. Razón: si el usuario desactiva `drawBones` en el Inspector el componente no consume GameObjects extra.
* Anidados como hijos del componente bajo `Bones / {Left,Right} / Finger{0..4}`. Si el GameObject del componente está en un layer dedicado (recomendado: layer `HandPresence` sin colisiones), los hijos heredan.
* `useWorldSpace = true` → no se ven afectados si la jerarquía padre se mueve.
* Material compartido con el de los joints por default (`boneMaterial == null → reusa jointMaterial`), lo que permite que dynamic batching agrupe los draw calls.
* `shadowCastingMode = Off`, `receiveShadows = false`, `lightProbeUsage = Off`, `numCornerVertices = 0`, `numCapVertices = 0` (líneas planas, mínimo costo).
* `boneWidth = 0.0025` default (2.5 mm — proporcional al `jointRadius = 4 mm`).

Costo total del esqueleto local del usuario: **1 (joints) + hasta 10 (bones) = 11 draw calls**. Validado en `PerformanceHUD`, impacto despreciable.

---

### Paso 1.6 — Color del esqueleto enlazado al rol del usuario local

Objetivo: que el esqueleto se vea rojo para Host, verde para Client, azul para Helper, leyendo del mismo `RoleConfig.asset` que usa el resto del sistema.

Implementación en `HandSkeletonRenderer`:

* Nuevo campo `bool bindColorToRole` (default `true`) + `float roleLookupIntervalSec` (default `1f`).
* En cada `LateUpdate`, si todavía no se enlazó: `FindObjectsOfType<NetworkedAvatarRole>()` y se busca el que tenga `IsOwner == true`. Cuando aparece, se suscribe a `Role.OnValueChanged` y se aplica el color.
* Poll a 1 Hz, no por frame — el avatar local no aparece hasta después de `StartHost` / `StartClient` y aproximación de connection approval, así que conviene esperar.

#### Bug crítico encontrado: `MaterialPropertyBlock.SetColor` no funciona con shader Standard instanced

**Síntoma**: con la primera versión del código, el binding se ejecutaba (`Debug.Log` lo confirmaba) pero las esferas seguían blancas. Los huesos (LineRenderers) sí cambiaban de color porque usan `startColor` / `endColor`, properties del componente, no del material.

**Causa raíz**: en shaders Unity-aware de instancing (como Standard), la propiedad `_Color` vive dentro del **instancing buffer** del shader (`UNITY_INSTANCING_BUFFER`), no en el constant buffer global. Cuando se hace `Graphics.DrawMeshInstanced(... , mpb, ...)` con `mpb.SetColor("_Color", c)`, Unity pone el color en el constant buffer global pero el shader lo lee del instancing buffer y por lo tanto **ignora silenciosamente** el override del MPB. El render usa el `material.color` que existía al crearlo (en nuestro caso, el `fallbackColor` blanco).

**Fix**: como nosotros somos dueños del material fallback (`_materialOwned == true`), mutar `jointMaterial.color` directamente. Eso sí surte efecto porque el shader recoge el nuevo valor del material en el siguiente draw. Para el caso de materiales aportados por el usuario en Inspector, se mantiene el path MPB con un comentario inline avisando que puede no funcionar con shaders instanced; se documenta la recomendación de usar Unlit/Color custom con `_Color` fuera del instancing buffer si necesitan compartir material entre instancias.

```csharp
if (_materialOwned && jointMaterial != null)
{
    jointMaterial.color = _currentColor;        // funciona siempre
}
else if (_mpb != null)
{
    _mpb.SetColor(s_ColorProperty, _currentColor);  // mejor esfuerzo (no funciona con Standard instanced)
}
```

Diagnóstico adicional añadido: logs explícitos en `TryBindLocalRole` cuando el poll encuentra `NetworkedAvatarRole` pero ninguno tiene `IsOwner=true`, y en `ApplyRoleColor` cuando el `RoleAssignmentService.Instance` o su `Config` no están disponibles. Esto permitió aislar que el problema era el render, no el binding.

Validación final en log:

```text
[HandSkeletonRenderer] Bound to local NetworkedAvatarRole on 'Avatar(Clone)'. Role=Host clientId=3
[HandSkeletonRenderer] Color aplicado para rol Host: RGBA=(1.00,0.00,0.00,0.90)
```

Esqueleto local → rojo confirmado en VR.

---

### Paso 2 — Sincronización por red del esqueleto remoto

Archivos nuevos:

```text
Assets/Multiplayer/HandSkeletonState.cs
Assets/Multiplayer/NetworkedAvatarSkeleton.cs
```

#### `HandSkeletonState` (struct sincronizado)

Estructura:

```text
bool leftTracked, rightTracked
Vector3 l00 .. l19   (20 joints mano izquierda, world space)
Vector3 r00 .. r19   (20 joints mano derecha)
```

Implementa `INetworkSerializable + IEquatable<HandSkeletonState>`.

**Por qué campos explícitos (`l00..l19`) en vez de arrays o unsafe fixed**:

* **Arrays**: `Vector3[] left, right` → `NetworkVariable<T>` copia por valor pero el array es referencia, así que las copias `previous` y `new` que NGO mantiene para diff-detection comparten el mismo backing buffer; mutar el "actual" también muta el "previous" y `Equals` devuelve `true` aunque cambiaron los joints → no broadcasta. Workarounds (allocar nuevo array por frame, o double-buffering) son frágiles o agregan GC pressure.
* **Unsafe fixed** (`fixed float leftJoints[60]`): es la opción más limpia técnicamente pero requiere `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` en el csproj y "Allow 'unsafe' Code" en Player Settings; el proyecto no tenía habilitado y se quiso evitar cambiar settings del proyecto por un single feature.
* **Explícito**: verboso (40 fields + 40 `SerializeValue` + comparación grande en `Equals`) pero blittable, sin allocations, sin sharing, sin unsafe. Se elige esta opción.

`NetworkSerialize` y `Equals` desplegan los 40 fields explícitamente. El verbosity está concentrado en un solo archivo y no se vuelve a tocar.

#### `NetworkedAvatarSkeleton` (NetworkBehaviour sobre el Avatar.prefab)

Coloca en el Avatar.prefab junto a `NetworkedAvatarHands` y `NetworkedAvatarRole`. Una `NetworkVariable<HandSkeletonState>` por avatar:

```csharp
private readonly NetworkVariable<HandSkeletonState> _state = new NetworkVariable<HandSkeletonState>(
    default,
    NetworkVariableReadPermission.Everyone,
    NetworkVariableWritePermission.Owner);
```

Comportamiento dual según `IsOwner`:

**Owner (cada instancia local sobre su propio avatar)**:

* `OnNetworkSpawn` no inicializa render (lo hace el `HandSkeletonRenderer` de escena con latencia 0).
* `Update` cada frame:
  1. Resuelve `XRHandSubsystem` + `XROrigin.CameraFloorOffsetObject` (auto-detect lazy).
  2. Itera los 20 joints por mano, transforma a world space.
  3. Escribe al field `_scratch` (struct reutilizado, sin alloc).
  4. `_state.Value = _scratch;` → NGO compara con previous via `HandSkeletonState.Equals` y broadcastea solo si cambió.

**Remote (cada instancia sobre los avatares de los demás)**:

* `OnNetworkSpawn`:
  * `EnsureSphereMesh` (toma prestado el built-in si no se asignó).
  * `EnsureMaterial` (crea Standard runtime con `enableInstancing=true` si no se asignó; marca `_materialOwned`).
  * Suscribe a `roleSource.Role.OnValueChanged` y aplica el color del rol del owner del avatar (rojo si Host owns, verde si Client owns, etc).
* `LateUpdate` cada frame:
  * Lee `_state.Value`.
  * Construye `_matrices` con las hasta 40 posiciones tracked.
  * `Graphics.DrawMeshInstanced` → una sola draw call para las dos manos del avatar remoto.
  * `EnsureBoneRenderers` (lazy lazy create de los 10 LineRenderer por avatar bajo `RemoteBones/{Left,Right}/Finger{0..4}`).
  * `UpdateBones` setea positions de cada chain y enable/disable según `leftTracked` / `rightTracked`.

Decisión arquitectónica clave: **el owner NO renderiza desde `NetworkedAvatarSkeleton`**, sólo samplea y escribe a la NetworkVariable. El render del esqueleto propio sigue siendo responsabilidad del `HandSkeletonRenderer` de escena (latencia 0, sin round-trip por red). Esto evita doble-render del esqueleto propio y mantiene la sensación de mano local "pegada al hardware".

Cada avatar remoto tiene su propio `Material` runtime (cuando se usa el fallback) — esto permite que dos avatares remotos tengan diferentes colores de rol sin compartir state. El alpha del material se preserva del `fallbackColor` del componente para mantener transparencia consistente.

#### Análisis de bandwidth

Tamaño por update de `HandSkeletonState`:

```text
2 bools  =  2 bytes
40 × Vector3  = 480 bytes
Total ≈ 482 bytes
```

A 30 Hz del tick rate NGO: **~14.5 KB/s** por avatar activo.

Triada completa (3 nodos × 1 hand-skeleton-stream cada uno hacia los otros 2) ≈ **43 KB/s** agregado. Trivial sobre LAN (incluso wifi 2.4 GHz). No se justifica cuantización a int16 para esta primera iteración; queda pendiente si en el futuro se agregan más avatares simultáneos o si el wireless del visor se vuelve un cuello de botella.

NGO deduplica: si una mano está estática (joints idénticos), `Equals` devuelve `true` y no hay broadcast. Si una mano deja de estar tracked (`tracked = false`), el siguiente update difiere del previo y se broadcastea un solo frame con `tracked=false`; los siguientes frames con la misma mano off no broadcastean.

#### Caveats conocidos

* **Sin interpolación**: a 30 Hz, los joints remotos saltan visiblemente entre updates. Para una segunda iteración se planea buffer de 2 muestras + delay fijo de ~100 ms con interpolación lineal entre joints. No implementado en Paso 2 (KISS).
* **Markers viejos en remote**: `NetworkedAvatarHands` (sistema previo) sigue sincronizando los markers Thumb/Index/Palm y las líneas Pinch/Ray. En remote ahora se ven el esqueleto + los markers viejos superpuestos. Es funcional pero visualmente ruidoso; cuando se valide el esqueleto en VR se puede esconder los markers viejos via `Renderer.enabled = false` sin removerlos (preserva backward compat).
* **DrawMeshInstanced + Standard shader instanced**: mismo gotcha del Paso 1.6. Se aplica el mismo fix (`_materialOwned ? mutate : MPB`), pero como cada avatar remoto crea su propio material runtime, `_materialOwned == true` siempre que no se aporte material custom desde Inspector. El path MPB queda como fallback documentado.
* **Layer**: los bones quedan en el layer del componente padre (típicamente `Default`). Si el proyecto tiene un layer dedicado tipo `HandPresence`, se hereda automáticamente. Recomendable excluirlo de cualquier raycast del Jenga.

---

### Estado actual

| Sistema | Estado |
|---|---|
| Esqueleto local (20 joints + 5 bones por mano) | ✅ DrawMeshInstanced + 10 LineRenderers, ~11 draw calls totales |
| Color por rol en esqueleto local | ✅ Bound via `NetworkedAvatarRole.OnValueChanged`, fix de Standard-instancing-MPB documentado |
| Sincronización por red de los 20 joints por mano | ✅ `NetworkVariable<HandSkeletonState>` owner-write everyone-read |
| Esqueleto remoto renderizado en avatares de los demás | ✅ Componente `NetworkedAvatarSkeleton` sobre Avatar.prefab |
| Color por rol en esqueletos remotos | ✅ Cada avatar remoto lee el rol de su `NetworkedAvatarRole` co-locado |
| Bandwidth ≤ 15 KB/s por avatar | ✅ 482 bytes × 30 Hz ≈ 14.5 KB/s |
| No-interferencia con poke / drag / raycast | ✅ Sin colliders, sin GameObjects por joint, sin tocar XR Origin / markers / NGO existentes |

---

### Próximos pasos

* **Paso 3**: interpolación en `NetworkedAvatarSkeleton` remote — buffer de 2 muestras con timestamp y delay de ~100 ms, blending lineal entre joints. Eliminaría el snapping visible a 30 Hz.
* **Limpieza visual**: esconder los markers viejos (`ThumbMarker`, `IndexMarker`, `PinchMarker`) en el Avatar.prefab cuando el esqueleto remoto esté validado en VR real; preserve `PinchLine` y `RayDisplay` por su semántica (gesto de pinch y feedback de raycast son distintos del esqueleto).
* **Cuantización opcional**: si se agregan más de 3 participantes, evaluar quantización de los joints a int16 relativos a wrist para bajar a ~6 KB/s por avatar.
* **Material Unlit custom**: reemplazar el fallback Standard por un Unlit/Color shader propio con `_Color` fuera del instancing buffer (permite usar MPB y compartir material entre todos los skeletons sin owning).
* **Configuración de `jointRadius` y `boneWidth`** persistente: hoy se ajustan por componente; si distintos participantes quieren tamaños diferentes (mano grande vs pequeña), se puede bindear a `RoleConfig`.

---

## 2026-05-21 — Refinamiento operacional: LAN discovery, HUDs de runtime y raycast Meta-style con anclaje anatómico

Branch activo: `multiplayer-prototype-fixed` (continuación de la sesión anterior).

Esta iteración no introduce nuevos sistemas networkeados; consolida la operatividad del prototipo en escenarios reales:

* eliminación de IPs fijas para el bootstrap NGO (LAN discovery),
* visibilidad en runtime del estado de red, performance y sincronización (HUDs),
* refinamiento ergonómico del raycast de selección (aim ray estilo Meta Horizon con anclaje shoulder-virtual + corrección anatómica del origen visible),
* limpieza de ruido en consola del proveedor de eye tracking,
* hardening del mock server de adquisición frente a recargas / Play Mode repetido.

---

### LAN Discovery — descubrimiento automático del host

Se introdujo el servicio de descubrimiento en LAN para eliminar la dependencia de IPs estáticas configuradas en el Inspector del `UnityTransport`. Sigue siendo 100% offline (sólo UDP broadcast en la subred local, sin servicios cloud).

Archivos nuevos:

```text
Assets/Multiplayer/Discovery/LanDiscoveryService.cs
Assets/Multiplayer/Discovery/DiscoveryRecord.cs
```

`LanDiscoveryService` (en GameObject `Network` de escena):

* Puerto UDP **7778** dedicado a discovery (distinto del game port 7777 y del puerto BioLab 1776).
* Protocol magic `XRCOLLAB` + version `1`. Cualquier paquete que no empiece con el magic se descarta.
* **Host mode** (`StartServer(gamePort)`): broadcast cada `serverBroadcastIntervalSec` (default 1.5 s) con payload `magic|v|session|host|game_port|ts`.
* **Multi-NIC**: itera `NetworkInterface.GetAllNetworkInterfaces()`, calcula la subnet broadcast de cada NIC activa (`ip OR ~mask`) y envía a cada una. Filtra heurísticamente NICs virtuales (Hyper-V, WSL, VMware, VirtualBox). Fallback adicional a `255.255.255.255` para cubrir el NIC default del SO.
* **Client mode** (`StartClientListener()`): `UdpClient` con `ReuseAddress = true` bindeado a `0.0.0.0:7778`. Thread background recibe paquetes; `Update` drena la queue thread-safe.
* **Lista deduplicada con timeout**: cada `(ip, gamePort)` se guarda como `DiscoveryRecord` con `lastSeenLocalTime`. Pruning automático tras `clientHostTimeoutSec` (default 5 s).
* `PickPreferredHost()` retorna el host más antiguo en la lista (FIFO de descubrimiento) → conveniente para auto-conexión.
* El listener arranca en `Start` aunque el usuario todavía no haya elegido rol, de modo que el HUD muestra candidatos antes de que se pulse `C`.

Modificaciones a `NetworkLauncher.cs`:

* `StartHost`: tras `StartHost()` exitoso, llama `LanDiscoveryService.Instance.StartServer(gamePort)` para anunciar el rol en la subred.
* `StartClientWithDiscovery`: consulta `PickPreferredHost()`. Si hay candidato, sobreescribe `UnityTransport.ConnectionData.Address` y `.Port` antes de llamar `StartClient()`. Si no hay candidatos, cae a la IP estática configurada en el Inspector del `UnityTransport` (modo legacy preservado).
* Log explícito por caso: auto-connect, fallback estático, discovery deshabilitado.

Resultado operacional: el cliente puede arrancar contra un host en cualquier IP/subred del lab sin tocar el Inspector. Si en el laboratorio hay varios PCs con builds activas, el orden de descubrimiento (FIFO) prioriza el host visto primero.

---

### PerformanceHUD — telemetría en runtime para builds standalone

Archivo nuevo:

```text
Assets/NetworkAudit/PerformanceHUD.cs
```

Overlay top-right (`F3` toggle), ancho fijo (`fixedWidth = 360 px`) para evitar reflow del cuadro cuando varían los números. Reporta por frame:

* **FPS**: instantáneo + suavizado exponencial con constante de tiempo ~0.5 s (`alpha = 1 − exp(-dt/0.5)`).
* **Frame time**: `now`, `p95`, `p99` sobre una ventana deslizante de `sampleWindowSec` (default 1 s). p95/p99 se recomputan cada 250 ms (no por frame, para evitar el costo del `Array.Sort`).
* **Memoria managed**: `Profiler.GetMonoUsedSizeLong()` y delta de alloc por frame en KB.
* **NGO RTT**: usa `NetworkConfig.NetworkTransport.GetCurrentRtt(ServerClientId)` en cliente. Muestra rol (HOST/SERVER/CLIENT/idle) y N de clientes conectados.
* **HandRayDriver status**: estado L/R activo + valor actual de `RotationSmoothK`.
* **ClockSync offset**: lee `NetworkClockSync.Instance.OffsetToHost` en ms, condicional a `IsSynced`.

Por construcción no spawnea garbage en runtime salvo los frame-times en la `Queue<float>` (cuyo tamaño se mantiene acotado por el window).

---

### BuildAuditHUD — enhancements

Modificaciones a `Assets/NetworkAudit/BuildAuditHUD.cs`:

* Ancho fijo configurable (`fixedWidth = 420 px`) para evitar que la caja cambie de tamaño según el contenido.
* Toggle visible/oculto en runtime con `F4`.
* Nuevo bloque "**LAN Discovery**" que enumera los hosts visibles con `hostName@ip:gamePort session=<sid> age=<s>`. Si el servicio está deshabilitado o no hay hosts, lo indica explícitamente. Esto cierra el loop visual: el usuario puede confirmar antes de pulsar `C` que efectivamente hay un host descubierto en la subred.
* Aumento del padding y separación visual; pintado en upper-left para no chocar con `PerformanceHUD`.

---

### HandRayDriver — aim ray estilo Meta Horizon con palm placement de 3 ejes

Archivo nuevo:

```text
Assets/Multiplayer/HandRayDriver.cs
```

Reemplaza el ray viejo (que salía del `ThumbMarker`, fuertemente acoplado al gesto pinch) por un ray estilo Meta Horizon / Quest System UI:

* **Origen lógico**: posición real de la muñeca (`XRHandJointID.Wrist`) en world space, vía `conversionSpace.TransformPoint(wristPose.position)` usando el `CameraFloorOffsetObject` del `XROrigin`.
* **Dirección**: vector hombro-virtual → muñeca. El hombro virtual se estima desde la pose del HMD:

  ```text
  shoulder = headPos
           + (isLeft ? -headRight : headRight) * shoulderLateralOffset   // 0.17 m
           - Vector3.up * shoulderDownOffset                              // 0.15 m
           - headForward * shoulderBackOffset                             // 0.05 m
  ```

* Estable por construcción: la muñeca casi no se mueve con la flexión de dedos, y `shoulder→wrist` produce un vector consistente con la intención natural de apuntar.
* `Update` sobreescribe `transform.position` y `transform.rotation` del anchor en world space cada frame, por lo que el parenting del anchor es indiferente (en escena viven directamente bajo `XR_Hands_Debug > HandDebug`, fuera del subtree del `ThumbMarker`).
* Singleton `HandRayDriver.Instance` para que el `PerformanceHUD` pueda mostrar el estado L/R.

**Wiring con `JengaRayGrabInteractor`**: el campo `rayOrigin` del Interactor apunta a `Left/RightHandRayAnchor` (los Transform que `HandRayDriver` mueve cada frame). El raycast del Interactor sigue funcionando sin cambios; lo único que se sustituyó es la fuente del Transform.

#### Palm placement — 3 ejes ortogonales para ajustar el origen visible

El usuario observó que el origen visible (donde nace el `LineRenderer`) caía sobre la muñeca, lo que daba la sensación de "proyección desde el hombro" en vez de "salida desde la mano". Para corregirlo sin sacrificar la estabilidad del shoulder-anchor, se añadieron tres offsets independientes que mueven sólo el origen visible (la dirección del ray no se altera):

| Campo | Eje | Positivo | Negativo |
|---|---|---|---|
| `originForwardOffset` | a lo largo de la mano (hueso) | hacia los dedos | hacia el codo |
| `originLateralOffset` | perpendicular horizontal | hacia el pulgar | hacia el meñique |
| `originVerticalOffset` | perpendicular vertical | hacia el dorso | hacia la palma |

Defaults afinados empíricamente: `forward=0.05 m`, `lateral=0`, `vertical=0`. Rangos en Inspector: `[0, 0.2]` para forward, `[-0.2, 0.2]` para los otros dos.

##### Construcción del frame ortonormal

Dos correcciones críticas para que los offsets sean **simétricos entre manos**:

**1. Eje vertical — no debe heredar el flip del lateral.**

Versión incorrecta inicial:

```csharp
lateralAxis = Cross(up, dir);
if (isLeft) lateralAxis = -lateralAxis;        // flip para mirror-symmetry
verticalAxis = Cross(dir, lateralAxis);        // BUG: hereda el flip
```

Resultado: el mismo valor numérico de `originVerticalOffset` movía una mano hacia arriba y la otra hacia abajo.

Fix: calcular `verticalAxis` **antes** del flip, manteniéndolo global:

```csharp
lateralAxisRaw = Cross(up, dir);
verticalAxis  = Cross(dir, lateralAxisRaw);     // global, ambas manos
lateralAxis   = isLeft ? -lateralAxisRaw : lateralAxisRaw;  // mirror-symmetric
```

**2. Eje forward — anclar a anatomía, no al ray sintetizado.**

Inicialmente se usaba `dir = (wrist − shoulder).normalized` como eje forward. Como cada brazo se sostiene en una pose ligeramente distinta (asimetría real-world), los dos `dir` no son mirror images perfectas → un mismo valor de `originForwardOffset` cae en puntos anatómicamente distintos de cada mano (más cerca de los nudillos en una, más al costado en la otra).

Fix: anclar forward al vector anatómico `wrist → middleProximal`:

```csharp
Vector3 handForward = dir;  // fallback
var middleProximal = hand.GetJoint(XRHandJointID.MiddleProximal);
if (middleProximal.TryGetPose(out var middleProxPose))
{
    Vector3 middleProxWorld = conversionSpace.TransformPoint(middleProxPose.position);
    Vector3 anatomicalFwd = middleProxWorld - wristWorld;
    if (anatomicalFwd.sqrMagnitude > 1e-6f)
        handForward = anatomicalFwd.normalized;
}
Vector3 originWorld = wristWorld + handForward * originForwardOffset;
```

Garantiza que "5 cm forward" cae siempre en el mismo punto anatómico (sobre los nudillos) en ambas manos, independiente de cómo el usuario tenga el brazo.

#### Smoothing opcional

Mantiene constantes `rotationSmoothK` y `positionSmoothK` (default 0 = sin lag adicional). Si se activan, se aplica `Slerp` sobre la dirección y `Lerp` sobre la posición. Documentado en tooltip: 0.3–0.5 reduce jitter pero añade 30–50 ms de latencia.

#### Caveat de jerarquía

`Left/RightHandRayAnchor` deben estar **fuera** del subtree del `ThumbMarker` (que era el parent histórico). Si quedan dentro, el `ThumbMarker` los arrastra cada frame antes de que `HandRayDriver` reescriba la pose absoluta — funcionalmente equivalente porque sobreescribimos world position/rotation, pero hace confuso el inspeccionar la jerarquía. Recomendación: reparentar a `XR_Hands_Debug > HandDebug` directamente.

---

### ViveEyeTrackingProvider — gate de XR session ready

Modificación a `Assets/EyeTracking/ViveEyeTrackingProvider.cs`:

Síntoma original: spam de `XR_ERROR_SESSION_LOST` en consola durante el bootstrap de OpenXR, antes de que `XRManagerSettings.StartSubsystems()` complete. Cada llamada a `XR_HTC_eye_tracker.Interop.GetEyeGazeData()` antes de la sesión válida tiraba excepción.

Fix:

```csharp
private static bool IsXrSessionReady()
{
    var settings = XRGeneralSettings.Instance;
    if (settings == null || settings.Manager == null) return false;
    if (!settings.Manager.isInitializationComplete) return false;
    if (settings.Manager.activeLoader == null) return false;

    s_displaySubsystems.Clear();
    SubsystemManager.GetSubsystems(s_displaySubsystems);
    for (int i = 0; i < s_displaySubsystems.Count; i++)
        if (s_displaySubsystems[i].running) return true;
    return false;
}
```

`Update()` ahora abandona temprano si `IsXrSessionReady()` retorna false. El criterio doble (loader inicializado **y** `XRDisplaySubsystem.running`) es necesario porque entre la inicialización del loader y la sesión OpenXR efectivamente activa hay una ventana en la que el eye tracker todavía no acepta queries.

Adicionalmente: cuando el HMD reporta `XrSystemEyeTrackingPropertiesHTC.supportsEyeTracking = 0` (caso observado en el Vive Focus Vision sin licencia activa de eye tracking en esa sesión), `XR_HTC_eye_tracker.Interop.GetEyeGazeData()` sigue tirando `NullReferenceException` aún con sesión válida. El `try/catch` ya estaba; el throttling del warning (1 cada 2 s vía `lastErrorLogTime`) reduce el ruido a un mensaje cada 2 segundos en lugar de uno por frame.

---

### AcquisitionMockServer — hardening

Modificaciones a `Assets/BiolabUDPSync/AcquisitionMockServer.cs`:

**1. Reset del estado lógico en cada arranque.**

Síntoma: tras detener y reiniciar Play Mode (o cambios de scripts con domain reload), el componente podía retener `acquisitionStarted = true` del run anterior. La siguiente sesión recibía `START` y respondía con `"ERROR Acquisition already started"`.

Fix: `StartServer()` ahora resetea `acquisitionStarted = false` antes de crear el thread del socket.

**2. Diferenciación de causas de shutdown del socket.**

Síntoma: `StopServer()` cerraba el `UdpClient`, y el thread loop arrojaba `SocketException` o `ObjectDisposedException` en `udpServer.Receive()`. Esto generaba un `Debug.LogError` ruidoso que parecía indicar un crash, cuando en realidad era cleanup normal.

Fix: el `catch` ahora distingue:

* `ObjectDisposedException` → silencio (es el shutdown ordenado).
* `SocketException` con `isRunning == false` → silencio (cierre durante Stop / recarga).
* `SocketException` con `isRunning == true` → `Debug.Log` (no Warning) con `SocketErrorCode`. Tipicamente shutdown del Editor.
* Cualquier otra excepción → `Debug.LogError` como antes.

Resultado: la consola queda limpia al salir de Play Mode.

---

### Estado actual

| Sistema | Estado |
|---|---|
| LAN discovery (host advertise + client listener) | ✅ Multi-NIC, dedupe + timeout, FIFO host pick |
| Auto-conexión NGO sin IP fija | ✅ Cliente toma IP del primer host visto; fallback a IP estática del Inspector |
| HUD de auditoría (BuildAuditHUD) | ✅ Rol, IsListening, clientes, spawned, hosts LAN. F4 toggle, ancho fijo |
| HUD de performance (PerformanceHUD) | ✅ FPS, p95/p99 frame, mono mem, alloc/frame, RTT NGO, estado HandRay, clock offset. F3 toggle |
| Raycast Meta-style | ✅ Shoulder virtual desde HMD; muñeca real como origen lógico |
| Palm placement de 3 ejes | ✅ Forward (anatómico vía MiddleProximal), lateral, vertical — simétricos entre manos |
| Smoothing configurable del ray | ✅ Latencia 0 por default, opcional Slerp/Lerp |
| Eye tracking sin spam de SESSION_LOST | ✅ Gate por `XRDisplaySubsystem.running` antes de leer gaze |
| Mock server resiliente a Play Mode repetido | ✅ Reset de estado + clasificación correcta de shutdown |

---

### Caveats / pendientes

* `XR_HTC_eye_tracker.Interop.GetEyeGazeData()` sigue tirando NRE cuando el HMD reporta `supportsEyeTracking = 0`. Solo es ruido throttled (cada 2 s). Si el deployment requiere eye tracking, hay que verificar licencia / calibración del visor antes de arrancar.
* `LanDiscoveryService.PickPreferredHost()` actualmente es FIFO. Si en el lab corren varias builds simultáneas, el cliente toma el primero que ve. Una versión futura podría priorizar por `sessionId` matcheando el del cliente, o exponer una mini UI de selección.
* Los offsets de palm placement deberían guardarse como defaults en el prefab (`HandRayDriver` está sobre un GameObject de escena hoy). En build de release conviene serializar valores afinados.
* Los anchors de mano (`Left/RightHandRayAnchor`) idealmente deberían colgar directamente de `XR_Hands_Debug > HandDebug`, no dentro de `ThumbMarker`. Funciona igual porque `HandRayDriver` sobreescribe world transform, pero es contraintuitivo al inspeccionar.

---

### Próximos pasos

* Persistir offsets de palm placement por participante (ergonomía variable según tamaño de mano).
* Exponer mini UI para listar hosts LAN descubiertos y permitir selección manual cuando hay más de uno.
* Broadcast del `SYNC_MARKER` a clientes vía NGO RPC (heredado del log anterior, sigue pendiente).
* Validar end-to-end con 2 o 3 PCs reales en LAN del lab (heredado).
* Auto-detección y reporte en HUD del `supportsEyeTracking` del HMD para que el operador sepa de antemano si el gaze va a estar disponible o no.

---

## 2026-05-18 — Integración multiplayer completa: roles, manos, Jenga networked y sincronización de relojes BioLab

### Workflow de branches

Se creó el branch `multiplayer-prototype-fixed` a partir de `biolab-udp-sync-simple` para aislar el trabajo de refactorización multiplayer sin afectar la línea estable de adquisición.

Commits relevantes en este branch:

```text
44358f3  Preliminary fix working multiplayer.
2b9df48  Working multiplayer.
1dfec65  Biolab multiplayer integration.
```

---

### Diagnóstico inicial: estado real del multiplayer

Antes de tocar código se hizo una auditoría empírica del stack networking existente:

* Framework confirmado: **Unity Netcode for GameObjects (NGO) 1.12.2**.
* Transport: **UnityTransport directo (UDP)**, sin Unity Relay → compatible con requerimiento de operación 100% LAN sin internet.
* `Address: 127.0.0.1`, `ServerListenAddress: 0.0.0.0`, `Port: 7777`, `ProtocolType: 0`.
* `TickRate: 30`, `RunInBackground: 1`, `ConnectionApproval: 0`.

Auditoría reveló matriz de sincronización pobre: solo se networkeaba head + body root del avatar (vía `OwnerNetworkTransform` + `AvatarFollowXROrigin`). Color, label, manos, rays, eventos de interacción y Jenga: **todos locales**.

---

### Fix arquitectónico: `NetworkLauncher` en escena

Se detectó que `NetworkLauncher.cs` (script que escucha teclas `H`/`C` para iniciar host/client) estaba como componente del `Avatar.prefab`, no en la escena. Esto generaba una contradicción imposible:

* el script solo se ejecuta cuando el avatar está spawneado;
* el avatar solo se spawnea cuando alguien hace `StartHost()`/`StartClient()`;
* pero las llamadas a `StartHost()`/`StartClient()` las dispara el propio script... que no existe todavía.

Verificación en git history: el GUID del script (`cbcdda6203ed8cc41971694ca9cce55b`) nunca apareció en `Room.unity` en ningún commit, en ningún branch.

Fix: se agregó el componente `NetworkLauncher` al GameObject `Network` de escena, junto al `NetworkManager`. Removido del Avatar.prefab para evitar listeners duplicados al spawnearse N avatares.

---

### Infraestructura de auditoría y testing single-machine

Se introdujo la carpeta `Assets/NetworkAudit/` con utilidades para correr Editor (Host) + build standalone (Client) en la misma máquina:

* `NetworkAuditLogger.cs` — Loggea eventos NGO a Console + CSV (`OnServerStarted`, `OnClientConnected`, `OnClientDisconnected`, snapshot de NetworkObjects con tecla `F2`).
* `BuildAuditHUD.cs` — Overlay en pantalla con rol actual, `IsListening`, `LocalClientId`, `ConnectedClients`, conteo de spawned, última tecla pulsada.
* `BuildFlyCamera.cs` — Cámara libre con WASD + mouse derecho para inspeccionar la build sin XR.
* `BuildXRDisabler.cs` — Detiene `XRGeneralSettings.Manager` y desactiva todos los `TrackedPoseDriver` de escena cuando es build (no Editor).
* `EditorXRBootstrap.cs` — Inicializa explícitamente XR en `Start()` del Editor.

Configuración de proyecto asociada: se desmarcó `Edit → Project Settings → XR Plug-in Management → Windows Standalone → Initialize XR on Startup`. De este modo XR no se autoinicia, lo que permite:

* Editor: `EditorXRBootstrap` lo enciende manualmente → headset funciona.
* Build standalone: nadie lo inicia → corre en ventana sin tocar el headset.

Resultado: se puede ejecutar Editor como Host con HMD + build como Client en ventana, simultáneamente en una sola PC.

---

### Asignación de roles por NGO con teleport a spawn

Se introdujo un sistema de asignación de roles networked desacoplado del `TriadSessionManager` (que se mantiene para mocks).

Archivos nuevos:

```text
Assets/Multiplayer/RoleConfig.cs           (ScriptableObject)
Assets/Multiplayer/RoleConfig.asset
Assets/Multiplayer/RoleAssignmentService.cs (server-only)
Assets/Multiplayer/NetworkedAvatarRole.cs   (NetworkBehaviour)
```

`RoleConfig` (ScriptableObject) — single source of truth para color + spawn pose por rol. Valores iniciales copiados del mock:

| Rol | Color | Spawn position |
|---|---|---|
| Host | rojo | `(0, 0, -0.766)` |
| Client | verde | `(-1.055, 0, 0)` |
| Helper | azul | `(1.071, 0, 0)` |

`RoleAssignmentService` (en GameObject `Network`):

* Suscribe a `OnServerStarted` y `OnClientConnectedCallback`.
* Mantiene array `assignmentOrder = [Host, Client, Helper]` y un `nextRoleIndex` server-only.
* En cada conexión nueva, asigna el siguiente rol al `clientId` correspondiente.
* Envía `TeleportClientRpc(position, euler)` al owner.

`NetworkedAvatarRole` (en Avatar.prefab):

* `NetworkVariable<PlayerRole> Role` (server-write, everyone-read).
* `OnNetworkSpawn` + `OnValueChanged` → aplica color en `bodyRenderers`, escribe texto en TMP `roleLabel`, billboard del `labelAnchor`.
* `TeleportClientRpc`: el owner mueve el `XROrigin` local (no el avatar directo, porque `AvatarFollowXROrigin` lo sobreescribiría al siguiente frame).

---

### Sincronización de manos y rays vía NetworkVariable

Archivos nuevos:

```text
Assets/Multiplayer/HandPoseState.cs        (struct INetworkSerializable + IEquatable)
Assets/Multiplayer/NetworkedAvatarHands.cs (NetworkBehaviour en Avatar.prefab)
```

`HandPoseState` campos:

```text
bool tracked
bool pinching
Vector3 thumbTipPos     (world space)
Vector3 indexTipPos     (world space)
Vector3 palmPos         (world space)
Quaternion palmRot      (world space)
bool rayActive
Vector3 rayStart        (world space)
Vector3 rayEnd          (world space)
```

`NetworkedAvatarHands`:

* Owner: lee `XRHandSubsystem.leftHand/rightHand`, convierte joint poses a world space usando el `CameraFloorOffsetObject` del `XROrigin` local, escribe a dos `NetworkVariable<HandPoseState>`.
* Owner adicional: lee el `LineRenderer` del raycast local del rig (`LeftGrabRay`/`RightGrabRay`) via auto-wire por nombre.
* All clients (incluyendo owner): aplican el estado a markers que son hijos del Avatar.prefab (`ThumbMarker`, `IndexMarker`, `PinchMarker`, `PinchLine`, `RayDisplay`).
* Threshold de pinch en world space configurable (`pinchThreshold = 0.025`).

Avatar.prefab nuevo sub-rig por mano:

```text
Avatar
├── LeftHand
│   ├── ThumbMarker      (sphere)
│   ├── IndexMarker      (sphere)
│   ├── PinchMarker      (sphere; se oculta vía Renderer.enabled, no SetActive)
│   ├── PinchLine        (LineRenderer; conecta thumb-index siempre que la mano se trackea)
│   └── RayDisplay       (LineRenderer; refleja el raycast del owner)
├── RightHand (idem)
└── LabelAnchor → RoleLabel (TMP)
```

Decisiones importantes:

* World space para todas las poses → cada cliente puede aplicar sin saber dónde está el `XROrigin` del owner.
* `Renderer.enabled = false` en lugar de `gameObject.SetActive(false)` cuando se oculta el `PinchMarker` (evita apagar la línea adyacente si quedó como hijo).
* Width de `LineRenderer` forzado vía script (`lineWidth = 0.003` configurable) en lugar de la curva de Inspector — esta última quedaba en 0 sin que sea evidente, generando líneas invisibles.
* Material fallback en runtime: si una `LineRenderer` queda sin material asignado al spawn, se le asigna `Sprites/Default` con warning.

`PinchDebugVisualizer` modificado: `Start` ahora delega en `TryAttachHandSubsystem` con retry en `Update`. Fix necesario porque XR ya no autoinicia → el subsistema aparece después.

---

### Sincronización del Jenga (server-authoritative con ownership transfer)

Archivos nuevos:

```text
Assets/Jenga/NetworkedJengaBlock.cs
```

Modificaciones a `JengaBlock.prefab`:

* Añadidos: `NetworkObject`, `OwnerNetworkTransform`, `NetworkRigidbody`, `NetworkedJengaBlock`.
* `NetworkRigidbody` maneja automáticamente `Rigidbody.isKinematic` según ownership; se eliminó la lógica manual del wrapper.
* Registro en `DefaultNetworkPrefabs.asset`.

Modificaciones a `JengaTowerGenerator.cs`:

* `Awake` registra singleton `Instance` (para que clientes accedan a `blockMaterials`).
* `Start` ya no construye la torre incondicionalmente. Suscribe a `NetworkManager.Singleton.OnServerStarted`.
* La torre se construye **solo en el server**.
* `BuildTower`: usa `NetworkObject.Spawn(true)` después de configurar el bloque. Mantiene lista privada `spawnedBlocks` para clear posterior.
* Material del bloque: índice determinístico `((level * 3) + (i + 1)) % blockMaterials.Length`. El server asigna el índice vía `NetworkVariable<int>` sobre `NetworkedJengaBlock`. Clientes leen el array local de `JengaTowerGenerator.Instance.GetMaterial(idx)` en `OnValueChanged`.
* Fallback standalone (sin NGO) sigue funcionando: aplica material localmente con `sharedMaterial`.

`NetworkedJengaBlock`:

* `RequestGrab(handTransform)` — guarda `pendingGrabHand` y envía `RequestGrabServerRpc()`.
* Server valida (bloque server-owned actualmente), llama `NetworkObject.ChangeOwnership(senderId)`.
* Cliente recibe `OnGainedOwnership` → ejecuta `grabbable.BeginGrab(pendingGrabHand)` localmente.
* `RequestRelease()` → `RequestReleaseServerRpc()` → server hace `RemoveOwnership()` → cliente recibe `OnLostOwnership` → `grabbable.EndGrab()`.
* `IsGrabbedAnywhere` propiedad pública para que los interactors descarten bloques tomados por otros.

`JengaGrabInteractor` y `JengaRayGrabInteractor` modificados:

* `TryGrab` delega al `NetworkedJengaBlock.RequestGrab(pinchPoint)` cuando existe, o cae al `JengaGrabbable.BeginGrab` local cuando no.
* `IsBlockTaken(g)` consulta el estado networked además del local.
* Eliminado `Debug.Log("Ray hit: ...")` del `Update` (era spam por cada frame que el ray toca algo).

Caveat operacional documentado: los `SphereCollider` que Unity agrega por default a los markers de mano del Avatar.prefab fueron eliminados — el raycast local del Jenga se autochocaba contra las propias esferas del avatar (que coinciden con la posición de los dedos). Las interacciones de poke/grab del rig no se ven afectadas porque referencian los markers de escena bajo `XR Origin > Camera Offset > XR_Hands_Debug`, no los del prefab.

---

### Eventos BioLab desde todos los nodos

Refactor de `AcquisitionEventManager.cs`:

* Eliminadas las puertas `if (!config.IsHost) return;` en `BeginExperimentalSession()` y `EndExperimentalSession()`.
* `LogAndMaybeForward` renombrado a `LogAndForward`, sin gate por rol.
* Cada nodo ejecuta su propia rutina de sesión: PING individual, SESSION_START / TASK_CONTEXT / heartbeat propios tagueados con su `node_id`.
* **Solo el host** mantiene la responsabilidad del comando `START` / `STOP` de adquisición a BioLab (evita conflictos en BioLab).
* En no-host: `acquisitionRunning = true` se asume tras PING OK + log `START_SKIPPED_NON_HOST`.

Hooks de interacción agregados:

* `EmitInteractionEvent(string label, string detailKey, string detailValue)` público en `AcquisitionEventManager`.
* `JengaGrabInteractor.TryGrab` / `Release` → `GRAB_BEGIN_PINCH` / `GRAB_END_PINCH` con `block={blockName}`.
* `JengaRayGrabInteractor.TryGrab` / `Release` → `GRAB_BEGIN_RAY` / `GRAB_END_RAY` con `block={blockName}`.

Resultado: cada participante (Host / Client / Helper) escribe su CSV local y reenvía sus propios eventos a BioLab tagueados con su `node_id`. BioLab recibe N timelines y puede agruparlas offline.

---

### Sincronización de relojes (Nivel 2.5 — handshake + sync markers)

Archivo nuevo:

```text
Assets/BiolabUDPSync/NetworkClockSync.cs (NetworkBehaviour)
```

Decisión de arquitectura: handshake explícito al conectar + sync markers periódicos, descartando la opción de NTP a nivel SO o de depender de `NetworkManager.ServerTime`.

Algoritmo de handshake (Cristian's):

* Cliente envía `T1` (su `Time.realtimeSinceStartup`) en `RequestSampleServerRpc`.
* Server registra `T2` al recibir y `T3` al responder con `RespondSampleClientRpc(T1, T2, T3)`.
* Cliente registra `T4` al recibir, calcula:
  * `offset = ((T2 − T1) + (T3 − T4)) / 2`
  * `rtt = (T4 − T1) − (T3 − T2)`
* Repite N veces (default `samples = 10`).
* Selecciona los `samples / 2` con menor RTT y toma la mediana del offset como estimación final.

API expuesta:

* `NetworkClockSync.Instance.IsSynced` → bool.
* `NetworkClockSync.Instance.OffsetToHost` → segundos (positivo si el host adelanta).
* `NetworkClockSync.Instance.HostTime` → `Time.realtimeSinceStartup + OffsetToHost`.
* `NetworkClockSync.Instance.SyncQualityRttMs` → telemetry.

Modificaciones en `AcquisitionEventManager`:

* `BuildMetadataPayload` ahora incluye `t_local=<localTime>` y `t_host=<HostTime>` además del flag `sync=synced` / `unsynced` / `failed`.
* Nuevo `LogAndForwardExternal` público para que `NetworkClockSync` pueda emitir markers a través del mismo pipeline (CSV + UDP a BioLab).
* En el `Start()` no-host: espera al handshake con timeout (`waitForClockSync = true`, `clockSyncWaitTimeoutSec = 5`). Si vence el timeout, continúa con `offset = 0` y deja registrado `CLOCK_SYNC` `TIMEOUT_PROCEED_WITHOUT_SYNC`.

Sync markers:

* Server-only coroutine en `NetworkClockSync.SyncMarkerLoop`.
* Emite cada `syncMarkerIntervalSec` (default 10s) un evento `SYNC_MARKER` con `seq`, `t_host_auth`, `t_host_est`, `t_local`, `offset_ms`.
* Se loggea localmente y se reenvía a BioLab vía `AcquisitionEventManager.LogAndForwardExternal`.

Validación local (Editor + build sobre loopback):

```text
host:    [NetworkClockSync] Server side ready (offset=0).
client:  [NetworkClockSync] Handshake OK.
         offset=-140785.688 ms, avgRTT=30.337 ms (best 5/10 samples).
```

El offset de ~141 segundos no es deriva de relojes — es la diferencia entre los orígenes de `Time.realtimeSinceStartup` de cada instancia (cada Unity arranca con su propio cero). Es exactamente lo que necesitamos capturar para alinear los timelines.

Items pendientes detectados en el log:

* El handshake se dispara dos veces seguidas en el cliente (mismo resultado las dos veces). Probablemente dos sitios de invocación (`OnNetworkSpawn` + algún callback). No rompe pero es redundante.
* Los `SYNC_MARKER` no se propagan a los clientes vía NGO RPC actualmente; solo el host los loggea. Falta el broadcast para que cada nodo escriba su propio par `(t_host_recibido, t_local_propio)` y se pueda validar drift offline.

---

### Cleanup de escena

* Eliminado el GameObject `HandTrackingManager` raíz (tenía solo `PinchDetector` con `Debug.Log` comentados → no-op funcional).
* El otro `HandTrackingManager` (hijo de XR Origin, con `PinchDebugVisualizer` y todas las refs a markers) se mantuvo y es el único activo.
* `TriadSessionManager` desactivado durante el audit inicial para evitar superposición visual entre mocks y avatares networked.

---

### Fix de compilación

`Assets/Jenga/AssignJengaAOIs.cs` se movió a `Assets/Jenga/Editor/AssignJengaAOIs.cs` (con su `.meta`). Usaba `[MenuItem]`, `Selection`, `Undo`, `EditorUtility` (todos del namespace `UnityEditor`) sin guard `#if UNITY_EDITOR`. Esto rompía cualquier build (Player.exe). Mover a una carpeta `Editor/` es la solución idiomática de Unity.

---

### Estado actual

| Sistema | Estado |
|---|---|
| Roles networked | ✅ Host / Client / Helper con color + spawn determinístico |
| Avatares (head/body) | ✅ NetworkTransform owner-authoritative |
| Manos (markers + pinch line + ray) | ✅ Sincronizado en world space |
| Labels TMP de rol | ✅ Sincronizado con billboard |
| Torre Jenga | ✅ Server-spawn con NetworkObject |
| Grab/release de bloques | ✅ Ownership transfer + ClientRpc local |
| Material de bloques | ✅ Index sincronizado via NetworkVariable |
| Eventos BioLab por nodo | ✅ Host / Client / Helper emiten independientes |
| Clock sync | ✅ Handshake al conectar + sync markers cada 10s |
| Timestamps en eventos | ✅ `t_local` + `t_host` en payloads |
| Sin internet, intranet only | ✅ Confirmado (UDP directo, no Relay) |
| Eventos eye tracking en CSV | ✅ Por nodo, local |
| Velocidad al soltar bloque | ⚠ No se transfiere en cambio de ownership (limitación conocida) |

---

### Próximos pasos

* Auto-vincular `AcquisitionNodeConfig.role` al `PlayerRole` asignado por `RoleAssignmentService` (hoy se configura manualmente por máquina).
* Resolver doble disparo del handshake en el cliente.
* Agregar broadcast del `SYNC_MARKER` vía NGO RPC para que cada nodo lo loggee con su par `(t_host_recibido, t_local_propio)` y permita validar drift offline.
* Probar end-to-end con 2 PCs reales en LAN (offset esperado ≠ 0, RTT esperado < 5 ms).
* Sincronizar velocidad/angularVelocity del rigidbody en el cambio de ownership para que el bloque mantenga inercia al soltarlo.
* Filtrar warnings de `XR_ERROR_SESSION_LOST` del eye tracker hasta que XR esté completamente inicializado.
* Documentar configuración por máquina (`nodeId`, `acquisitionIp`, role) en un README de deployment.

## 2026-05-08 — Refinamiento del flujo experimental y corrección de interacción XR basada en hand rays

### Simplificación del flujo experimental

Se decidió simplificar temporalmente el pipeline experimental eliminando la dependencia de interacción manual mediante botones VR. El objetivo fue estabilizar:

* adquisición BioLab,
* logging experimental,
* eye tracking,
* sincronización temporal,

antes de continuar depurando interacción XR compleja.

Como parte de esta decisión:

* los botones VR fueron desactivados operativamente,
* los prefabs y scripts se mantuvieron en escena para reutilización futura,
* el control experimental fue migrado a un flujo automático centralizado.

---

### Consolidación de la arquitectura de adquisición

Se identificó que el flujo activo del proyecto no utilizaba `BioLabSessionCoordinator`, sino `AcquisitionEventManager`.

Se consolidó la arquitectura actual:

```text
AcquisitionIntegration
├── AcquisitionNodeConfig
├── ExperimentEventLogger
├── AcquisitionMockServer
└── AcquisitionEventManager
```

A partir de esto, `AcquisitionEventManager` fue modificado para implementar un flujo experimental automático desacoplado de UI XR.

---

### Inicio automático de sesión experimental

Se incorporó soporte para:

* inicio automático de sesión,
* heartbeat periódico de metadata,
* logging explícito,
* compatibilidad transparente con acquisition mock.

Se añadieron parámetros configurables:

* `autoStartSession`
* `autoStartDelaySeconds`
* `sendTaskHeartbeat`
* `heartbeatIntervalSeconds`

El flujo actual ejecuta automáticamente:

1. `PING`
2. `START`
3. `SESSION_START`
4. `TASK_CONTEXT`
5. heartbeat periódico

sin requerir interacción manual.

---

### Integración consistente de metadata experimental

Se consolidó el uso de `ExperimentEventLogger` como fuente primaria de metadata experimental.

Los eventos enviados ahora incluyen consistentemente:

* `participant`
* `session`
* `task`
* `trial`
* `node`

Ejemplo:

```text
SESSION_START|participant=P001|session=S001|task=task_01|trial=trial_01|node=VR_HOST
```

Esto garantiza coherencia entre:

* CSV de eventos,
* CSV de eye tracking,
* BioLab UDP,
* acquisition mock log.

---

### Heartbeat automático de metadata experimental

Se implementó un mecanismo periódico (`TASK_CONTEXT_HEARTBEAT`) para mantener metadata contextual sincronizada durante sesiones largas.

Características:

* configurable por intervalo,
* reutiliza metadata experimental existente,
* prepara soporte para futuras condiciones dinámicas.

El heartbeat se detiene automáticamente al finalizar la sesión experimental.

---

### Logging explícito y depuración

Se añadieron mensajes detallados vía `Debug.Log` para facilitar depuración en tiempo real.

Se incorporó logging explícito para:

* inicio y fin de sesión,
* resultados de `PING`,
* resultados de `START` y `STOP`,
* forwarding UDP,
* heartbeat,
* sincronización local.

Esto facilita validar:

* conectividad,
* acquisition pipeline,
* consistencia de metadata,
* comportamiento del mock server.

---

### Validación del Acquisition Mock Server

Se confirmó el correcto funcionamiento del servidor mock (`AcquisitionMockServer`) para desarrollo local.

El sistema actualmente puede ejecutarse completamente sin BioLab real.

El mock responde correctamente a:

* `PING`
* `START`
* `STOP`
* eventos experimentales UDP.

---

### Corrección de payload duplicado

Se identificó un problema donde los payloads duplicaban el nombre del evento.

Ejemplo incorrecto:

```text
SESSION_START_SESSION_START|participant=...
```

La causa era:

```csharp
string encoded = $"{eventType}_{eventValue}";
```

cuando `eventValue` ya contenía el nombre del evento.

Se corrigió reemplazando por:

```csharp
string encoded = eventValue;
```

Tras la corrección, los payloads quedaron consistentes y limpios.

---

### Desactivación temporal de logging de pinch gestures

Se decidió desactivar temporalmente el logging de pinch y hand gestures debido a:

* contaminación excesiva de consola,
* generación masiva de eventos irrelevantes,
* dificultad para depuración experimental.

Se deshabilitaron temporalmente:

* `PinchDetector`
* `PinchDebugVisualizer`

Esto no afecta:

* eye tracking,
* raycast,
* grab interaction,
* acquisition pipeline.

---

### Corrección de generación inicial de torre Jenga

Se diagnosticó un problema donde la torre Jenga aparecía flotando sobre la mesa únicamente al iniciar la escena.

Se observó que:

* el reset manual reconstruía correctamente la torre,
* el flujo inicial ejecutaba `BuildTower()` demasiado temprano.

El problema estaba relacionado con:

* inicialización prematura de física,
* estabilización tardía del XR rig,
* sincronización de colliders y transforms.

Se corrigió reutilizando el mismo pipeline estable del reset manual durante el inicio de escena.

Se reemplazó:

```csharp
StartCoroutine(BuildTower());
```

por:

```csharp
yield return null;
yield return new WaitForFixedUpdate();
yield return new WaitForSeconds(0.25f);
yield return StartCoroutine(ResetCoroutine());
```

Resultados:

* torre correctamente apoyada sobre la mesa,
* eliminación de bloques flotando,
* comportamiento consistente entre inicio y reset.

---

### Diagnóstico del desplazamiento del raycast XR

Se investigó un problema donde los rayos utilizados para grab de bloques Jenga aparecían desplazados aproximadamente un metro hacia adelante respecto a las manos.

Inicialmente se sospechó:

* uso incorrecto de `forward`,
* problemas de world/local space,
* offsets introducidos por jerarquías XR.

Tras revisar:

* scripts,
* referencias de escena,
* configuración de `LineRenderer`,
* branch principal funcional,

se concluyó que el problema NO estaba en `JengaRayGrabInteractor.cs`.

---

### Identificación del problema real de ray origins

Se descubrió que:

* `RightRayOrigin`
* `LeftRayOrigin`

existían como GameObjects independientes bajo:

```text
Interaction
```

con posiciones absolutas no nulas.

Ejemplo observado:

```text
Position:
X 0.17
Y 0.99
Z 0.78
```

Esto provocaba que:

* el raycast siguiera correctamente la orientación de la mano,
* pero el origen espacial estuviera desacoplado del tracking XR.

---

### Corrección arquitectónica de ray origins

Se reparentaron los ray origins bajo marcadores de mano XR válidos.

Nueva jerarquía:

```text
RightThumbMarker
├── RightRayOrigin
└── RightHandRayAnchor

LeftThumbMarker
├── LeftRayOrigin
└── LeftHandRayAnchor
```

Los transforms locales fueron normalizados:

```text
Position = 0,0,0
Rotation = 0,0,0
Scale = 1,1,1
```

Esto eliminó el desplazamiento espacial excesivo del raycast.

---

### Introducción de arquitectura tipo Meta Horizon para hand rays

Se identificó un problema de usabilidad importante:

* cuando el raycast dependía directamente del índice,
* el gesto pinch alteraba la dirección del rayo,
* dificultando la selección de objetos.

Se decidió adoptar una arquitectura más cercana a Meta Horizon / Quest System UI:

* dirección del ray desacoplada del pinch,
* selección activada por pinch,
* orientación controlada desde palma/pulgar.

Para esto se introdujeron:

```text
RightHandRayAnchor
LeftHandRayAnchor
```

como anchors independientes para raycasting.

Configuración inicial recomendada:

### RightHandRayAnchor

```text
Local Position = 0,0,0
Local Rotation = 0,25,0
Local Scale = 1,1,1
```

### LeftHandRayAnchor

```text
Local Position = 0,0,0
Local Rotation = 0,-25,0
Local Scale = 1,1,1
```

Esto prepara una interacción más estable y ergonómica.

---

### Estado actual

* acquisition pipeline estable,
* acquisition mock funcional,
* metadata experimental consistente,
* heartbeat automático operativo,
* eye tracking funcional,
* torre Jenga correctamente alineada,
* ray origins corregidos,
* arquitectura tipo Meta Horizon parcialmente integrada,
* interacción raycast funcional pero aún en refinamiento ergonómico.

---

### Próximos pasos

* refinar orientación de `RightHandRayAnchor` y `LeftHandRayAnchor`,
* estabilizar ergonomía del raycast XR,
* desacoplar completamente raycast de movimientos finos del índice,
* limpiar arquitectura de helpers XR redundantes,
* reintegrar interacción XR progresivamente,
* sincronizar interacción XR con eventos experimentales reales.

## 2026-05-08 — Simplificación del flujo experimental, estabilización de logging y correcciones de interacción XR

### Simplificación del flujo experimental

Se decidió simplificar temporalmente el pipeline experimental eliminando la dependencia de interacción manual mediante botones VR. El objetivo fue estabilizar el flujo de adquisición y logging antes de continuar depurando problemas de interacción XR.

Como parte de esta decisión:

* se desactivó el uso operativo de botones VR físicos,
* se mantuvieron los prefabs y scripts en la escena para posible reutilización futura,
* se migró el control experimental hacia un flujo automático centralizado.

La motivación principal fue desacoplar:

* adquisición BioLab,
* logging experimental,
* eye tracking,
* sincronización temporal,

de los problemas de interacción física y hand tracking.

---

### Reestructuración del flujo de adquisición

Se identificó que el sistema activo de integración no utilizaba `BioLabSessionCoordinator`, sino `AcquisitionEventManager`.

Se revisó y consolidó la arquitectura actual:

``text
AcquisitionIntegration
├── AcquisitionNodeConfig
├── ExperimentEventLogger
├── AcquisitionMockServer
└── AcquisitionEventManager

## 2026-04-11 — Integración BioLab, corrección de marcadores de mano y sistema inicial de botones VR

Se implementó una primera capa de integración entre el prototipo XR y BioLab para permitir el registro sincronizado de eventos experimentales durante la ejecución. Se definió como decisión de diseño mantener las señales de alta frecuencia (por ejemplo, eye tracking) localmente en Unity, y enviar únicamente eventos semánticos de baja frecuencia hacia BioLab. Esto evita sobrecargar el sistema de adquisición y asegura la calidad de los datos fisiológicos.

La comunicación con BioLab se implementó mediante UDP, siguiendo un protocolo simple orientado a control experimental. El flujo actual considera:

* `PING` / `PONG` para verificación de conectividad
* `START` para iniciar adquisición
* `STOP` para finalizar adquisición
* `E:source,value,0` para eventos semánticos con timestamp

Se adoptó un modelo en el cual solo el nodo `host` envía eventos a BioLab, asumiendo una arquitectura de ejecución en intranet (sin internet). Esto evita duplicación de eventos y simplifica la sincronización en escenarios multiusuario.

Se definió el conjunto inicial de eventos experimentales:

* `SESSION_START`
* `TASK_START_<id>`
* `TASK_END_<id>`
* `SESSION_END`

En paralelo, se implementó un sistema de logging local (`ExperimentEventLogger`) como fuente primaria de verdad. Este sistema escribe archivos CSV estructurados con:

* `event_index`
* `timestamp_rel_s`
* `timestamp_utc_iso`
* `node_id`
* `event_type`
* `event_value`
* `notes`

Los archivos se almacenan en `Application.persistentDataPath`, alineados con la estructura ya utilizada para eye tracking, lo que permite posteriormente fusionar datos de interacción y gaze en análisis offline.

Para facilitar el desarrollo sin dependencia del sistema de adquisición, se implementó un servidor mock (`AcquisitionMockServer`) que simula BioLab. Este componente permite validar el flujo de mensajes UDP y registrar eventos en un log local (`acquisition_mock_log.txt`), facilitando la depuración.

---

### Sistema de botones VR para eventos experimentales

Se desarrolló una primera versión del sistema de botones físicos en VR para gatillar eventos experimentales mediante interacción con manos (hand tracking).

Se definió un conjunto inicial de botones:

* Start Session
* End Session
* Start Task
* End Task

La arquitectura de cada botón separa explícitamente:

* lógica experimental (`VrTaskButton`)
* detección física (`VrButtonTrigger`)
* representación visual (`Visual`)
* etiquetado (`TextMeshPro`)

Esto permite modificar animaciones, materiales y lógica sin acoplamientos innecesarios.

Se implementó un mecanismo de activación mediante un proxy dedicado del dedo índice (`ButtonTouchProxy`), en lugar de reutilizar directamente el marcador del índice. Este proxy:

* es hijo de `RightIndexMarker`
* contiene su propio `SphereCollider` y `Rigidbody`
* permite aislar la interacción con botones del resto del sistema (raycast, Jenga, pinch)

Se introdujeron capas específicas para interacción:

* `UIButtons`
* `ButtonActivator`

Y se configuró la matriz de colisiones para permitir únicamente interacción entre ambas, evitando interferencias con otros elementos de la escena (por ejemplo, bloques de Jenga).

Se implementó feedback visual básico en los botones:

* desplazamiento en eje Y (efecto de “hundimiento”)
* cambio de color durante la activación

Este comportamiento es funcional y será refinado en etapas posteriores.

---

### Corrección de alineación de marcadores de mano

Se diagnosticó y corrigió un problema crítico de alineación en los marcadores de mano (`RightIndexMarker`, `RightThumbMarker`, etc.), que provocaba:

* desfase lateral respecto a los dedos reales
* activación incorrecta de botones
* inconsistencia en la interacción por pinch y raycast

El problema se originaba en una combinación de:

* uso inconsistente de espacios de coordenadas (world vs local)
* jerarquía de transforms no alineada con el `XR Origin`
* posibles dobles transformaciones

Para resolverlo, se realizaron los siguientes ajustes:

* reorganización de la jerarquía de `XR_Hands_Debug` para alinearla con el sistema de referencia del XR rig
* normalización de transforms (`Position = 0,0,0`, `Rotation = 0,0,0`, `Scale = 1,1,1`) en contenedores intermedios
* validación del seguimiento de joints usando directamente poses consistentes con el espacio del rig
* eliminación de offsets implícitos introducidos por parents incorrectos

Tras la corrección:

* los marcadores siguen correctamente las puntas de los dedos
* desaparece el desfase lateral
* la interacción con botones se vuelve consistente

---

### Consideraciones sobre escala y colliders

Se identificó un aspecto relevante respecto al uso de escala en los marcadores:

* usar `scale != 1` en objetos que participan en interacción física puede afectar colisionadores hijos
* en particular, el `ButtonTouchProxy` puede quedar con un collider efectivo demasiado pequeño si hereda escala

Se establece como recomendación:

* mantener objetos lógicos (markers, proxies, colliders) en escala `(1,1,1)`
* aplicar escala solo a objetos visuales (meshes hijos)

Esto mejora la estabilidad de detección y la reproducibilidad de interacción.

---

### Estado actual

* Integración UDP con BioLab funcional
* Logging local de eventos experimentales funcional
* Servidor mock de adquisición operativo
* Marcadores de mano correctamente alineados
* Proxy de dedo funcional para interacción con botones
* Sistema de botones VR parcialmente operativo
* Feedback visual de botones funcional
* Interacción existente (Jenga, raycast, pinch) no afectada

---

### Próximos pasos

* estabilizar completamente el sistema de botones (evitar activaciones múltiples por frame)
* implementar debounce robusto en `VrButtonTrigger` / `VrTaskButton`
* validar comportamiento independiente de cada botón
* integrar eventos automáticos desde interacción (grab, pinch, finalización de tareas)
* mejorar feedback visual (hover, confirmación clara)
* evaluar incorporación de feedback háptico o pseudo-háptico
* sincronizar estado experimental entre host, client y helper
* preparar pipeline de fusión offline (eye tracking + eventos)
* documentar configuración final de jerarquía y componentes en Unity

## 2026-04-09 — BioLab Integration  

En el PC de adquisición, según el manual, debes hacer esto antes de probar nada:

Abrir BioLab.
Ir a la configuración de eventos de red y habilitar UDP Events. El manual ubica este switch en Network Events.
En la pantalla de adquisición, seleccionar la ruta/archivo de salida de la corrida. BioLab solo empieza a escuchar en el puerto 1776 después de que se selecciona un file path en la Acquisition screen.
Verificar que Trigger Mode = Off, porque START solo funciona en ese modo.
Dejar BioLab en la pantalla de adquisición, listo para recibir comandos.

## 2026-04-02 — Multiplayer Mock Prototype (Triad Setup)

Created a new branch `multiplayer-prototype` to isolate the development of multiplayer support and avoid interfering with the current stable setup.

Implemented an initial triad-based mock multiplayer system to enable development and testing with a single XR device. Defined three fixed roles (`Host`, `Client`, `Helper`) and introduced a `PresenceMode` per role (`Real`, `Mock`, `Disabled`), allowing selective simulation of remote users.

Added `TriadSessionManager` to manage player slots. The manager is responsible for spawning mock avatars, assigning role-based colors, and positioning them in the scene. Introduced a `tableCenter` reference to automatically orient all mock avatars towards the interaction area, removing the need for manual rotation tuning.

Created a `MockAvatar` prefab by duplicating the existing avatar and removing all networking and XR tracking components. This ensures visual consistency between real and simulated users while keeping mocks fully local.

Implemented `RoleAvatarPresenter` to handle visual configuration of avatars, including:

* applying role-based colors,
* displaying role labels,
* billboard behavior for labels.

Introduced a `LabelAnchor` transform to decouple label positioning from the avatar geometry. This simplifies alignment and ensures stable label orientation in VR.

Validated the setup in runtime:

* mock avatars spawn correctly,
* roles and colors are assigned as expected,
* avatars are oriented towards the shared workspace,
* labels remain readable from the user’s perspective.

This establishes a controlled triad environment and prepares the system for the next phase: extending multiplayer presence to hand proxies (thumb, index, pinch, ray) before integrating shared interaction with Jenga.



## 31 de marzo

### Git y estructura del proyecto

* Se creó el branch `eye-tracking-prototype` a partir de `main` para aislar el desarrollo del sistema de seguimiento ocular.
* Se consolidó la estrategia de branches:

  * `vive-focus-preview`: migración de hardware
  * `eye-tracking-prototype`: instrumentación de datos

### Integración de eye tracking (HTC Vive Focus Vision)

* Se reemplazó el enfoque OpenXR genérico por el uso directo del SDK de HTC (`XR_HTC_eye_tracker`).
* Se confirmó que:

  * el eye tracking funciona correctamente a nivel de sistema
  * requiere calibración previa en el visor
* Se observó que:

  * en modo streaming (PCVR) pueden perderse datos si la sesión no está completamente inicializada

### Implementación del proveedor de datos

* Se implementó `ViveEyeTrackingProvider` para capturar:

  * gaze por ojo (left/right)
  * gaze combinado
  * pupil diameter
  * pupil position
  * eye openness
* Se agregaron mecanismos de robustez:

  * manejo de excepciones (`XR_ERROR_SESSION_LOST`)
  * reducción de logs repetitivos

### Métricas geométricas adicionales

* Se incorporaron:

  * `vergenceAngleDeg`
  * `interocularDistance`
* Permiten estimar profundidad de atención sin depender de raycast

### Sistema de logging (CSV)

* Se implementó `EyeTrackingSessionLogger`
* Características:

  * escritura por frame
  * timestamps duales:

    * `timestamp_rel_s`
    * `timestamp_utc_iso`
  * estructura extensible para experimentos

### Almacenamiento de datos

* Generación automática de archivos incrementales

* Estructura:

  EyeTrackingLogs/<participant>/<session>/

* Ejemplo:

  task_01_trial_01_001_gaze.csv
  task_01_trial_01_002_gaze.csv

### Integración con AOIs (Jenga)

* Integración automática de `AOITag` en `JengaTowerGenerator`
* Cada bloque incluye:

  * identificador único
  * metadata (nivel y posición)
* Se implementó `GazeTargetRaycaster` para registrar intersecciones gaze–objeto

### Validación del sistema

* Se verificó:

  * generación correcta de CSV
  * datos de cabeza y gaze válidos
  * detección de AOIs correcta
* Problemas observados:

  * pupil diameter constante (posible limitación del SDK o iluminación)
  * errores intermitentes `XR_ERROR_SESSION_LOST` no bloqueantes

### Decisión metodológica

* Se definió registrar únicamente datos raw
* El procesamiento (fixations, saccades, etc.) se realizará offline

### Estado actual

* Captura gaze operativa
* AOI tracking operativo
* Logging CSV operativo
* Sistema listo para recolección de datos experimentales

### Próximos pasos

* Incorporar eventos de interacción (pinch, grab)
* Evaluar calidad de pupil data
* Resolver warnings de streaming (missing frames)
* Preparar pipeline de análisis offline

## 24 de marzo

### Migración a HTC Vive Focus Vision

#### Cambio estratégico

* Transición desde Meta Quest 3 hacia Vive Focus Vision
* Objetivos:

  * acceso a eye tracking
  * mayor control vía OpenXR

#### Git workflow

* Creación del branch:

  vive-focus-preview

#### Setup técnico

* Uso de:

  * VIVE Streaming (wired)
  * SteamVR
  * OpenXR en Unity

#### Problemas encontrados

* USB no reconocido
* Streaming no disponible
* Uso incorrecto de GPU (iGPU en lugar de RTX)

#### Soluciones

* Cambio de puerto USB
* Configuración correcta de streaming
* Forzar GPU dedicada

#### Resultado

* Sistema funcionando en Vive Focus Vision:

  * head tracking OK
  * hand tracking OK
  * streaming estable

## 17 de marzo

### Interacción por raycast

* Implementación de `JengaRayGrabInteractor`
* Visualización mediante `LineRenderer`

### Integración

* Activación del raycast mediante gesto pinch

### Problemas pendientes

* Dirección del rayo no natural (basado en cámara)
* Falta de orientación basada en la mano
* Raycast siempre activo
* Posible conflicto entre poke y raycast

## 12 de marzo

### Estabilización de torre Jenga

* Generación progresiva de bloques
* Implementación de `WaitUntilSettled`

### Ajustes

* Spacing
* Drop height
* Configuración de Rigidbody

### Nueva interacción

* Diseño de interacción tipo poke

### Próximo paso

* Implementar empuje con dedo índice

## 11 de marzo (Jenga)

### Implementación inicial

* Prefab `JengaBlock`
* Script `JengaTowerGenerator`

### Problema

* Inestabilidad física (explosión de bloques)

### Diagnóstico

* Collider mal dimensionado

### Solución

* Corrección del `BoxCollider`
* Separación entre física y visual

### Resultado

* Torre estable

## 11 de marzo

### Hand tracking (Quest 3)

* Implementación con XR Hands
* Detección de pinch funcional

### Sistema de depuración

* Script `PinchDebugVisualizer`
* Objeto `HandDebug`
* Marcadores:

  * Thumb
  * Index
  * Pinch

### Problema

* Desfase entre tracking y visualización

### Solución

* Corrección de espacio de coordenadas (XR Origin)

### Resultado

* Tracking consistente y preciso

## 8 de marzo

### Sincronización de avatares

* Problemas:

  * Host no veía correctamente al cliente
  * Cliente no veía su propio avatar moverse

### Intervención

* Revisión de ownership (`NetworkObject`)
* Ajuste de `AvatarFollowXROrigin`

### Cambios

* Solo el owner actualiza posición
* Sincronización vía red para otros clientes

### Resultado

* Sincronización completa entre usuarios

## 6 de marzo

### Estado inicial

* Sistema multijugador parcialmente funcional
* Avatares visibles en ambos clientes

### Problemas

* Movimiento incorrecto de avatares
* Inconsistencias entre host y cliente

### Solución

* Ajustes de red:

  * desactivación de firewall
  * habilitación de conexiones remotas

### Resultado

* Comunicación funcional entre equipos
