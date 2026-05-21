# XR Collaboration Prototype – Development Log

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
