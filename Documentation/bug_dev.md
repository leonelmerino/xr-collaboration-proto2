# Registro de bugs y correcciones pendientes

**Fecha del análisis:** 2026-08-27
**Branch analizado:** `fixes-ui-new` (commit `a914f21`)
**Alcance:** revisión del subsistema de trackers corporales (`TrackerBodyCalibration`, `BodyTrackingSessionLogger`, `TrackerSystemBootstrap`, `TrackerPoseDriver`) y su interacción con `NetworkLauncher` y `NetworkedAvatarPose`.

Origen: análisis realizado al redactar la guía operativa de trackers para el responsable de recolección de datos. Los bugs de prioridad P0 y P1 afectan la **integridad de los datos registrados**, no solo la experiencia de uso.

---

## Índice por prioridad

| # | Bug | Prioridad | Impacto |
|---|---|---|---|
| 1 | Colisión de tecla `C`: conectar y calibrar en el mismo frame | **P0** | Datos contaminados |
| 2 | `participantId` desincronizado entre logger de mirada y de cuerpo | **P0** | Archivos en carpetas distintas |
| 12 | Salto de ~20 cm al hacer pinch en Client/Helper | **P0** | Interacción inutilizable en 2 de 3 nodos |
| 3 | Presionar `C` durante discovery reinicia la conexión | **P1** | Conexión inestable |
| 4 | Falta columna `head_valid` en el CSV de cuerpo | **P1** | Dropouts indistinguibles |
| 11 | Rayo de selección remoto no visible para los demás | **P1** | Sin referente compartido de atención |
| 5 | `StartHost()` ignora su valor de retorno; el HUD reporta éxito sin verificar | **P2** | Diagnóstico engañoso |
| 6 | `Calibrate()` no valida la plausibilidad de la pose | **P2** | Calibración inválida silenciosa |
| 7 | Sin marca temporal de calibración en el CSV | **P2** | No se puede segmentar el archivo |
| 13 | El router no respeta la asignación de IP fija por MAC | **P2** | Conectividad intermitente entre nodos |
| 8 | `TrackerPoseDriver` hardcodea `Avatar_Host` | **P3** | Cosmético (Client/Helper) |
| 9 | Colisión de tecla `R` con `AvatarHandTunerHUD` | **P3** | Latente, hoy mitigado |
| 10 | `CalibrateHands()` escribe una NetworkVariable que nadie consume | **P3** | Código muerto |

> Los bugs 11, 12 y 13 se agregaron el 2026-08-27 tras la revisión del subsistema de interacción y de la configuración de red del laboratorio. Los números son identificadores estables, no orden de prioridad; las secciones siguen ordenadas por prioridad.

---

## P0 — Críticos

### Bug 1 — Colisión de tecla `C`: conectar y calibrar se disparan en el mismo frame

**Prioridad:** P0
**Archivos:** `Assets/NetworkLauncher.cs:53`, `Assets/TrackerBodyCalibration.cs:85-86`, `Assets/Multiplayer/NetworkedAvatarPose.cs:189,251`

**Descripción**

Tres MonoBehaviours distintos escuchan `KeyCode.C` en su `Update()`. Unity despacha `Update()` a todos los componentes activos, así que **una sola pulsación dispara los tres**:

| Script | Línea | Acción | ¿Protegido? |
|---|---|---|---|
| `NetworkLauncher` | 53 | `TryStartClient()` | Sí — `if (nm == null \|\| nm.IsListening) return;` (línea 88) |
| `TrackerBodyCalibration` | 85 | `Calibrate()` | **No — se ejecuta siempre** |
| `NetworkedAvatarPose` | 251 | `CalibrateHands()` | Solo owner; sin efecto visual (ver Bug 10) |

**Comportamiento por rol**

*Host* — correcto. `H` arranca el host y deja `IsListening = true`. Una `C` posterior encuentra el guard de la línea 88 y `TryStartClient()` sale sin efecto; solo corre `Calibrate()`.

*Client / Helper* — **defectuoso**. La primera `C` conecta **y** calibra simultáneamente, en el instante en que el operador está sentado frente al teclado y no de pie en la pose de calibración.

**Por qué contamina los datos**

`Calibrate()` (línea 92) solo aborta si encuentra un número de trackers distinto de 3 (línea 131). Si los tres están tracked —que es el estado esperado según el procedimiento operativo—, la calibración **tiene éxito** con una pose incorrecta:

```csharp
// TrackerBodyCalibration.cs:190
IsCalibrated = true;   // se alcanza aunque la pose de calibración sea inválida
```

`BodyTrackingSessionLogger` arranca en `Start()` con `autoStart = true` (línea 57), o sea antes de cualquier calibración. Desde ese primer `C` espurio y hasta que se recalibre, el CSV registra filas con `is_calibrated = 1` y posiciones de trackers físicamente incorrectas. **Son filas que parecen válidas y no lo son.**

Consecuencia para el análisis: en Client y Helper, `is_calibrated = 1` **no garantiza** una calibración válida. El control documentado ("verificar que `is_calibrated = 1`") es insuficiente para esos dos nodos.

**Mitigación operativa actual (sin cambios de código)**

Presionar `C` una segunda vez, ya de pie en posición. `Calibrate()` reasigna los tres dispositivos y los tres offsets desde cero, así que el sobreescritura es completa y no deja residuos —ni siquiera de un refinamiento previo con `R`. El tramo contaminado queda en el período de setup, anterior al inicio de la tarea, y se descarta al recortar por los eventos de BioLab (ver Bug 7).

**Corrección propuesta**

Mover la calibración de trackers a una tecla libre. Cambio de una línea en `TrackerBodyCalibration.cs:85`:

```csharp
// Actual
if (Input.GetKeyDown(KeyCode.C))
    Calibrate();

// Propuesto: campo serializado con tecla que no colisione
[SerializeField] private KeyCode calibrateKey = KeyCode.K;
...
if (Input.GetKeyDown(calibrateKey))
    Calibrate();
```

Teclas ya ocupadas a evitar: `H`, `C` (NetworkLauncher), `R` (refine + AvatarHandTunerHUD), `F4` (toggle HUD tuner), `F5` (toggle AvatarHandJointDebugViz), `Q/A/W/S/E/D`, `1/2`, `Y/U/X` (AvatarHandTunerHUD).

Conviene resolver también la colisión de `NetworkedAvatarPose:189` (`calibrationKey = KeyCode.C`) en el mismo cambio, aunque hoy sea inocua.

---

### Bug 2 — `participantId` desincronizado entre el logger de mirada y el de cuerpo

**Prioridad:** P0
**Archivos:** `Assets/BodyTrackingSessionLogger.cs:31-32,145`, `Assets/TrackerSystemBootstrap.cs:16-26`, `Assets/Scenes/Room.unity`

**Descripción**

Los dos loggers obtienen su metadata de sesión de fuentes distintas:

| Logger | Instanciación | ¿Configurable antes de Play? |
|---|---|---|
| `EyeTrackingSessionLogger` | Presente en `Room.unity` | **Sí** — campos editables en el Inspector |
| `BodyTrackingSessionLogger` | Creado en runtime por `TrackerSystemBootstrap` | **No** — usa siempre los defaults del código |

`TrackerSystemBootstrap` crea el GameObject `[TrackerSystem]` mediante `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` (línea 16) y le agrega los cuatro componentes por código (líneas 23-26). Como el objeto no existe hasta entrar en Play, sus campos serializados no se pueden editar previamente, y `StartLogging()` se ejecuta en `Start()` antes de que nadie pueda tocarlos.

**Impacto A — los dos archivos de una misma sesión caen en carpetas distintas**

Si el operador configura `participantId = "P007"` en el `EyeTrackingSessionLogger` de la escena:

```
EyeTrackingLogs/P007/S001/task_01_trial_01_001_gaze.csv   ← mirada
EyeTrackingLogs/P001/S001/task_01_trial_01_001_body.csv   ← cuerpo (default hardcodeado)
```

Esto rompe la premisa documentada de que ambos archivos comparten carpeta y convención de nombres. La sincronización por `timestamp_utc_iso` sigue siendo posible, pero hay que saber que los archivos están separados.

**Impacto B — colisión de nombres al agrupar los tres nodos**

Los tres laptops escriben en `P001/S001/` con nombres idénticos. Como cada máquina tiene su propio `persistentDataPath` no se pisan localmente, pero al centralizar los datos de Host, Client y Helper los archivos colisionan y no hay forma de saber cuál corresponde a qué participante.

**Corrección propuesta**

Opción preferida: agregar el `[TrackerSystem]` a `Room.unity` como GameObject de escena con los cuatro componentes wireados. `TrackerSystemBootstrap:19` ya contempla ese caso y sale temprano:

```csharp
if (Object.FindObjectOfType<TrackerBodyCalibration>() != null)
    return; // ya está en la escena (wired manualmente)
```

Esto hace configurables los campos de sesión sin tocar código.

Opción complementaria: que `BodyTrackingSessionLogger` lea la metadata desde el `EyeTrackingSessionLogger` de la escena en `Start()`, garantizando que ambos loggers no puedan divergir:

```csharp
var gaze = FindObjectOfType<EyeTrackingSessionLogger>();
if (gaze != null)
{
    participantId = gaze.participantId;
    sessionId     = gaze.sessionId;
    taskId        = gaze.taskId;
    trialId       = gaze.trialId;
    condition     = gaze.condition;
}
```

Debe ejecutarse antes de `StartLogging()` (línea 57), ya que el nombre del archivo se fija en ese momento.

Para el Impacto B, considerar además un identificador de nodo (`node_id` o el rol) en la ruta o en el nombre del archivo.

---

### Bug 12 — Salto de ~20 cm al hacer pinch en Client/Helper

**Prioridad:** P0
**Archivos:** `Assets/Jenga/NetworkedJengaBlock.cs:90-124`, `Assets/Jenga/JengaGrabbable.cs:26-35,71-85`, `Assets/OwnerNetworkTransform.cs`
**Estado:** abierto — primera hipótesis refutada experimentalmente

**Síntoma**

Al apuntar con el rayo a un bloque y hacer pinch desde Client o Helper, el bloque se desplaza de forma **instantánea unos 20 cm**, en dirección lateral o vertical. Al soltar el pinch, el bloque vuelve a su posición original.

Características observadas:

* La dirección es **determinista por bloque**: el mismo bloque salta siempre en la misma dirección.
* **No ocurre en el Host.** En el Host `IsOwner = true` desde el spawn, así que no hay `ChangeOwnership` al agarrar.
* El poke (Bug relacionado, ya resuelto en `1b8facb`) no presenta el problema.

**Hipótesis 1 — REFUTADA**

El commit `50919c1` (2026-08-22) atribuyó el salto a una corrección instantánea de colisión: al activar física local en el bloque agarrado, Unity detectaría overlap contra las poses interpoladas de los vecinos y expulsaría el bloque para resolverlo. La corrección consistió en mantener `isKinematic = true` cuando el owner no es el server:

```csharp
// NetworkedJengaBlock.cs:109 — estado actual
if (rb != null && IsServer)
{
    rb.velocity = Vector3.zero;
    rb.angularVelocity = Vector3.zero;
    rb.isKinematic = false;
}
```

**Verificado en VR: el salto persiste.** Con el bloque kinematic no hay simulación de física local que pueda generar la corrección de overlap, de modo que la causa está en otra parte. El comentario extenso en `NetworkedJengaBlock.cs:99-108` describe esta hipótesis ya descartada y **debe actualizarse** para no inducir a error en el futuro.

**Hipótesis restantes**

Ninguna verificada todavía. En orden de plausibilidad:

*H2 — Transferencia de autoridad de `OwnerNetworkTransform`.* Antes del grab, el transform del bloque en el cliente lo escribe `OwnerNetworkTransform` a partir de la pose del server, interpolada y con 1-2 frames de retardo. Al ejecutarse `ChangeOwnership(senderId)` (`NetworkedJengaBlock.cs:197`), el cliente pasa a ser autoridad y su pose local —la interpolada, no la real— se convierte en la verdad publicada. El salto sería la diferencia entre ambas.

*H3 — `initialGrabOffset` calculado sobre una pose obsoleta.* `JengaGrabbable.BeginGrab` (línea 34) calcula:

```csharp
initialGrabOffset = transform.position - grabPoint.position;
```

Si `transform.position` está desactualizado en el instante del `BeginGrab`, el offset queda desplazado por ese mismo error, y `desired = grabPoint.position + initialGrabOffset` (línea 71) coloca el bloque corrido durante todo el agarre. Encaja con que **el bloque vuelva al soltar**: al recuperar el server la autoridad, se reimpone la pose correcta.

*H4 — Divergencia `transform.position` / `rb.position` en cuerpo kinematic.* `MovePosition` sobre un rigidbody kinematic aplica el movimiento en el siguiente paso de física; si algo lee `transform.position` en el intermedio, ve un valor distinto al del rigidbody.

**Cómo discriminar: los logs de `a914f21`**

El commit `a914f21` instrumentó cinco puntos del flujo con el prefijo `[JengaGrab]`. **Aún no se ha ejecutado.** No corrige nada — solo recolecta evidencia — pero está diseñado exactamente para separar estas hipótesis:

| Evidencia en el log | Hipótesis que confirma |
|---|---|
| `OnGainedOwnership POST` con `tPosDelta` o `rbPosDelta` ≈ 0.20 | H2 — el salto ocurre en la transición de autoridad, antes del `BeginGrab` |
| Deltas ≈ 0, pero `blockPos` difiere entre `RequestGrab path=SERVER_RPC` y `BeginGrab` | H3 — la pose ya estaba desfasada al calcular el offset; la diferencia mide el desfase |
| `blockPos` y `rbPos` divergentes en `BeginGrab` | H4 — desincronización transform/rigidbody |
| `offset` en `BeginGrab` con magnitud anómala frente a la distancia real mano-bloque | H3 |

**Próximo paso**

Ejecutar una sesión con Client y Helper, reproducir el salto y capturar el log completo de los tres equipos. La tabla anterior debería resolver la causa en una sola corrida. Hasta entonces **no aplicar más correcciones a ciegas**: la hipótesis 1 ya consumió un ciclo de trabajo y dejó comentarios engañosos en el código.

**Impacto operativo mientras siga abierto**

El grab a distancia es inutilizable en dos de los tres nodos. El poke sí funciona en los tres, así que la tarea colaborativa es posible pero con interacción degradada para Client y Helper. Debe advertirse a los participantes en cualquier sesión de prueba.

---

## P1 — Altos

### Bug 3 — Presionar `C` durante el discovery reinicia el intento de conexión

**Prioridad:** P1
**Archivo:** `Assets/NetworkLauncher.cs:88-91`

**Descripción**

El guard de `TryStartClient()` es `nm.IsListening`, que permanece en `false` mientras el cliente todavía está buscando el host por LAN discovery. Una pulsación de `C` en esa ventana pasa el guard y ejecuta:

```csharp
// NetworkLauncher.cs:88-91
if (nm == null || nm.IsListening) return;
if (_connectCoroutine != null) StopCoroutine(_connectCoroutine);
_connectCoroutine = StartCoroutine(ConnectClientCoroutine());
```

— aborta la corrutina en curso y arranca una nueva.

**Impacto**

Combinado con el Bug 1, el operador que presiona `C` para calibrar mientras el cliente aún busca al host reinicia la búsqueda sin darse cuenta. Si el host todavía no está arriba y se repite la pulsación, el nodo puede quedar en un ciclo donde nunca completa la conexión. No es destructivo, pero es difícil de diagnosticar en el laboratorio.

**Corrección propuesta**

Guardar también contra una conexión ya en curso:

```csharp
if (nm == null || nm.IsListening) return;
if (_connectCoroutine != null) return;   // ya hay un intento en curso
_connectCoroutine = StartCoroutine(ConnectClientCoroutine());
```

Requiere limpiar `_connectCoroutine = null` al finalizar `ConnectClientCoroutine()`, tanto en la ruta de éxito como en la de fallo, para permitir reintentos legítimos. Resolver el Bug 1 elimina el disparador más probable, pero el guard sigue siendo correcto por sí mismo.

---

### Bug 4 — Falta la columna `head_valid` en el CSV de cuerpo

**Prioridad:** P1
**Archivo:** `Assets/BodyTrackingSessionLogger.cs:162`

**Descripción**

El logger escribe `head_x/y/z` y `head_qx/qy/qz/qw` sin ningún flag de validez, a diferencia del resto de los bloques del CSV, que sí tienen su `*_valid`. El HMD reporta una posición por defecto `(0, 0, −0.7)` con rotación identidad en dos situaciones:

1. **Inicialización** — hasta que el subsistema XR posiciona la cámara en espacio mundo. Observado en los primeros ~60 samples (≈2 s) de la sesión de prueba del 2026-08-01.
2. **Dropout de tracking** — se registró uno de 1,9 s (samples 149-322) en esa misma sesión.

Ambos períodos son indistinguibles de datos válidos en post-procesamiento.

**Mitigación actual**

Filtro heurístico `head_y > 0.3` en análisis, documentado en la guía operativa. Funciona pero deja la responsabilidad del lado del analista.

**Corrección propuesta**

Agregar la columna al header (línea 162) y al `WriteLine`, con un criterio explícito. La validez del HMD se puede consultar directamente al dispositivo en vez de inferirla por altura:

```csharp
// Criterio robusto: consultar isTracked del HMD via InputDevices
var hmdDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
bool headValid = hmdDevice.isValid
              && hmdDevice.TryGetFeatureValue(CommonUsages.isTracked, out bool ht)
              && ht;
```

Si se prefiere no agregar la dependencia, el criterio por altura (`_head.position.y > 0.3f`) replica el filtro documentado y es suficiente.

**Nota:** este bug ya estaba anotado como pendiente en `dev_log.md`, entrada del 2026-08-01. Se incluye aquí por su impacto sobre los datos.

---

### Bug 11 — El rayo de selección remoto no es visible para los demás participantes

**Prioridad:** P1
**Archivo:** `Assets/AvatarHumanoid.prefab`
**Estado:** confirmado — causa identificada, corrección de un solo paso

**Síntoma**

Cada participante ve únicamente el rayo que sale de su propia mano. Ninguno ve el rayo de los otros dos, de modo que no hay forma de saber a qué bloque está apuntando otro participante.

**Causa raíz**

Verificado en el prefab: el componente `NetworkedAvatarHands` está serializado con `m_Enabled: 0` en el root de `AvatarHumanoid.prefab`.

Toda la infraestructura de sincronización del rayo existe y es correcta:

* `Assets/Multiplayer/HandPoseState.cs` — campos `rayActive` / `rayStart` / `rayEnd`.
* `Assets/Multiplayer/NetworkedAvatarHands.cs` — publica el estado desde el owner y lo consume en los remotos; `EnsureRayDisplays()` crea automáticamente los `LineRenderer` (`AutoRayDisplay_Left` / `AutoRayDisplay_Right`, rojos, 3 mm) si no están asignados en el prefab; oculta el rayo remoto en el owner para evitar duplicados.

Nada de eso se ejecuta. NGO omite `OnNetworkSpawn` en behaviours deshabilitados, y `Update` tampoco corre, así que el owner nunca publica el estado del rayo.

El rayo propio sí se ve porque lo dibuja `JengaRayGrabInteractor.rayLine` en el rig del XR Origin, de forma completamente local: nunca pasa por la red.

**Corrección**

Un solo paso manual, sin cambios de código:

> Abrir `Assets/AvatarHumanoid.prefab` → seleccionar el GameObject root → en el Inspector, marcar la casilla **Enabled** del componente `NetworkedAvatarHands` → guardar.

Este paso ya estaba anotado como pendiente en la entrada del `dev_log.md` del 2026-08-18 ("Fix manual requerido en el prefab"), donde también se documenta por qué habilitar este componente legacy es seguro y no reintroduce las esferas de depuración del sistema de manos anterior: existe una doble protección —la constante `ShowVisualizers = false` en el propio archivo, y las referencias a los markers vacías (`{fileID: 0}`) en el prefab humanoide—.

**Verificación posterior a la corrección**

* Cada participante ve el rayo rojo de los otros dos cuando estos apuntan.
* Cada participante sigue viendo **un solo** rayo propio, no dos superpuestos.
* No aparecen esferas ni líneas de depuración sobre las manos de los avatares.

**Por qué importa más allá de lo cosmético**

El proyecto estudia colaboración en XR. El rayo es el principal referente compartido de atención durante la tarea: sin él, los participantes no pueden establecer referencia deíctica sobre los bloques ("ese", "el que estoy apuntando"). Cualquier sesión que se corra sin esta corrección mide colaboración bajo una condición de comunicación degradada, lo que constituye un confundido experimental y debe registrarse como tal en los metadatos de la sesión.

---

## P2 — Medios

### Bug 5 — `StartHost()` ignora su valor de retorno y el HUD reporta éxito sin verificar

**Prioridad:** P2
**Archivo:** `Assets/NetworkLauncher.cs:60-77`

**Descripción**

`nm.StartHost()` (línea 65) devuelve un `bool` que se descarta. `SetStatus(Status.Hosting, "Host activo...")` (líneas 72 y 76) se ejecuta incondicionalmente después.

**Impacto**

Si `StartHost()` falla —puerto 7777 ocupado, error de transporte, permisos—, el HUD muestra "Host activo — anunciando en LAN puerto 7777" mientras `IsListening` sigue en `false`. Dos consecuencias:

1. Los otros dos nodos nunca encuentran el host por discovery, sin explicación visible.
2. En ese estado el guard de `TryStartClient()` no bloquea, así que presionar `C` para calibrar **dispara un intento real de conexión como cliente** en una máquina que el operador cree que está hosteando.

**Corrección propuesta**

```csharp
if (!nm.StartHost())
{
    SetStatus(Status.Error, "StartHost() falló — revisar puerto 7777 y transporte");
    Debug.LogError("[NetworkLauncher] StartHost() devolvió false.");
    return;
}
```

Verificar que el enum `Status` tenga un valor de error o agregarlo.

---

### Bug 6 — `Calibrate()` no valida la plausibilidad de la pose

**Prioridad:** P2
**Archivo:** `Assets/TrackerBodyCalibration.cs:92-195`

**Descripción**

La única validación previa es que haya exactamente 3 trackers tracked (línea 131). No se comprueba que la geometría resultante sea anatómicamente plausible, de modo que cualquier pose —sentado, agachado, trackers sobre la mesa— produce una calibración "exitosa" con `IsCalibrated = true`.

Este es el mecanismo que convierte el Bug 1 en contaminación de datos en lugar de un fallo visible.

**Corrección propuesta**

Agregar comprobaciones de sanidad antes de la línea 190, con un warning explícito y `return` sin marcar `IsCalibrated`:

```csharp
// La cintura debe estar a una altura razonable respecto del HMD
float waistRatio = waistWorld.y / Mathf.Max(hmd.position.y, 0.1f);
if (waistRatio < 0.35f || waistRatio > 0.75f)
{
    Debug.LogWarning($"[Calibration] Pose implausible: cintura al {waistRatio:P0} " +
                     $"de la altura del HMD. ¿El usuario está de pie? Calibración descartada.");
    return;
}

// Los pies deben estar cerca del suelo
if (Mathf.Abs(footLWorld.y) > 0.35f || Mathf.Abs(footRWorld.y) > 0.35f)
{
    Debug.LogWarning("[Calibration] Pose implausible: pies lejos del suelo. Calibración descartada.");
    return;
}
```

Los umbrales son propuestas iniciales y conviene ajustarlos con datos reales. El comportamiento de salir sin tocar `IsCalibrated` es el correcto y ya está establecido en la ruta de la línea 131: preserva la última calibración válida en vez de invalidarla.

---

### Bug 7 — Sin marca temporal de calibración en el CSV

**Prioridad:** P2
**Archivo:** `Assets/BodyTrackingSessionLogger.cs:160-170`

**Descripción**

`is_calibrated` es un booleano por fila que indica si existe *alguna* calibración activa, pero no permite distinguir **cuál**. Si se calibra más de una vez en la misma corrida —el caso normal mientras el Bug 1 siga abierto, y también al recalibrar tras un problema de tracking—, todas las filas posteriores a la primera calibración se ven idénticas.

**Impacto**

No se puede segmentar el archivo por calibración desde el propio CSV. Para descartar el tramo contaminado hay que cruzar con el log de eventos de BioLab por `timestamp_utc_iso`, o con una anotación manual del operador.

**Corrección propuesta**

Agregar un contador de calibración que se incremente en cada `Calibrate()` exitoso, expuesto como propiedad pública y escrito como columna:

```csharp
// TrackerBodyCalibration.cs
public int CalibrationCount { get; private set; }
// ...en Calibrate(), junto a IsCalibrated = true:
CalibrationCount++;
```

Con eso, el análisis conserva únicamente las filas cuyo `calibration_index` coincida con el máximo del archivo. Alternativa más simple: una columna `calibration_timestamp_utc` con el instante de la calibración vigente.

---

### Bug 13 — El router no respeta la asignación de IP fija por MAC

**Prioridad:** P2
**Ámbito:** configuración de red del laboratorio (no es un defecto del código)
**Estado:** confirmado por observación; causa no diagnosticada

**Síntoma**

El router local tiene configuradas reservas de IP fija por dirección MAC para las tres estaciones. Aun así, **a veces** los equipos reciben una IP distinta de la reservada.

**Impacto**

* Cualquier configuración que asuma una IP concreta deja de ser válida sin aviso.
* El LAN discovery (UDP 7778) resuelve la dirección del host en runtime, así que en el caso normal el sistema tolera el cambio. Pero el campo `UnityTransport.ConnectionData.Address` de `Room.unity` actúa como fallback si el discovery falla, y ese valor sí es estático.
* Complica el diagnóstico: al depurar un problema de conectividad no se puede dar por sentado qué equipo es cuál.

**Mitigación operativa actual**

Verificar la IP de cada equipo con `ipconfig` al inicio de cada sesión y registrarla en la planilla, antes de encender los visores. Está incorporado como paso obligatorio en el protocolo de prueba.

**Pendiente de diagnóstico**

Para poder corregirlo hay que determinar primero:

* ¿Afecta siempre a la misma estación, o es aleatorio entre las tres?
* ¿La IP entregada queda fuera del rango reservado, o el router intercambia reservas entre equipos?
* ¿Ocurre solo tras reiniciar el router, tras reiniciar un equipo, o sin patrón aparente?
* ¿Los equipos tienen una segunda interfaz activa (WiFi de UC invitados) que pueda estar interfiriendo en la resolución? Las estaciones se conectan simultáneamente a WiFi para internet y por cable para la intranet, y una métrica de ruta mal priorizada podría explicar parte del comportamiento observado.

Registrar estos datos durante las próximas sesiones. Sin ellos cualquier corrección sería especulativa.

**Causas candidatas habituales**

Lease DHCP previo todavía vigente que el router entrega antes de aplicar la reserva; reserva registrada sobre la MAC de la interfaz WiFi en lugar de la Ethernet; o un segundo servidor DHCP en el segmento.

---

## P3 — Bajos

### Bug 8 — `TrackerPoseDriver` hardcodea `Avatar_Host`

**Prioridad:** P3
**Archivo:** `Assets/TrackerPoseDriver.cs:87`

```csharp
var go = GameObject.Find("Avatar_Host");
```

El driver que aplica las posiciones de los trackers a los huesos del avatar (Hips, LeftFoot, RightFoot) solo busca el sub-mesh del Host. En Client y Helper no encuentra su avatar y las piernas no se animan.

**No afecta el registro de datos**: `BodyTrackingSessionLogger` lee los trackers directamente vía `InputDevices` y no depende de `TrackerPoseDriver`. Es exclusivamente visual.

**Corrección propuesta**: resolver el sub-mesh según el rol asignado, del mismo modo que lo hace `AvatarRoleMeshSwitcher` a partir de `NetworkedAvatarRole`, en vez de buscar por nombre fijo.

---

### Bug 9 — Colisión de tecla `R` entre refinamiento y `AvatarHandTunerHUD`

**Prioridad:** P3
**Archivos:** `Assets/TrackerBodyCalibration.cs:44`, `Assets/Multiplayer/AvatarHandTunerHUD.cs:48,219`

Ambos componentes escuchan `KeyCode.R`: `TrackerBodyCalibration` como `refineKey` y `AvatarHandTunerHUD` como `resetKey`.

**Hoy es inocuo** porque el manejo de input del HUD está detrás de un early-return (`AvatarHandTunerHUD.cs:115`):

```csharp
if (!_visible) return;
```

y el HUD arranca oculto —`showOnGUI` dejó de ser `[SerializeField]` en la sesión del 2026-06-01, así que siempre parte en `false` y solo se muestra con `F4`.

**Riesgo latente**: si alguien abre el HUD con `F4` durante una sesión, cada `R` de refinamiento también resetea el offset Euler de la muñeca seleccionada. Conviene reasignar una de las dos teclas al resolver el Bug 1.

---

### Bug 10 — `CalibrateHands()` escribe una NetworkVariable que nadie consume

**Prioridad:** P3
**Archivos:** `Assets/Multiplayer/NetworkedAvatarPose.cs:251-254,326`, `Assets/Multiplayer/AvatarPoseDriver.cs`

`CalibrateHands()` escribe la NetworkVariable `WristCalibration`, pero `AvatarPoseDriver` no la referencia en ninguna parte —verificado por búsqueda sobre el archivo completo. Es coherente con la nota del `dev_log` del 2026-05-24: la estrategia geométrica de orientación de palma volvió innecesaria la calibración de muñeca, y los offsets quedaron en `Vector3.zero` por defecto.

**Consecuencia práctica**: la tercera acción disparada por la tecla `C` (Bug 1) no tiene efecto visible. Es la razón por la que la colisión no se manifestó antes como un problema de avatares.

**Corrección propuesta**: eliminar `CalibrateHands()`, el `calibrationKey` y la NetworkVariable `WristCalibration` si se confirma que ningún otro consumidor los usa. Se alinea con el `TODO(cleanup)` de `NetworkedAvatarHands` anotado en la entrada del 2026-08-18.

---

## Orden de trabajo sugerido

1. **Bug 11** — una casilla en el prefab, sin cambios de código. Es la corrección de mayor impacto por unidad de esfuerzo de toda la lista.
2. **Bug 12** — ejecutar una sesión con los logs de `a914f21` y resolver la causa antes de intentar cualquier corrección. Es el defecto que más limita la tarea colaborativa.
3. **Bug 1** — desbloquea la recolección confiable en Client y Helper. Cambio mínimo (una tecla).
4. **Bug 2** — sin esto, los datos de los tres nodos no se pueden agrupar sin renombrado manual.
5. **Bug 6** — convierte una calibración inválida en un fallo visible; refuerza la corrección del Bug 1.
6. **Bug 4** y **Bug 7** — eliminan filtros heurísticos del lado del análisis.
7. **Bug 3** y **Bug 5** — robustez de la conexión LAN.
8. **Bug 13** — recolectar los datos de diagnóstico durante las próximas sesiones antes de tocar la configuración del router.
9. **Bugs 8, 9, 10** — limpieza, agrupables con el `TODO(cleanup)` ya anotado.

Los bugs 1, 2, 4, 7 y 13 tienen mitigación operativa documentada en la guía de trackers y en el protocolo de prueba. Mientras sigan abiertos, esos documentos son un requisito para que los datos sean utilizables.

Los bugs 11 y 12 afectan la validez experimental, no la integridad de los datos: cualquier sesión que se corra con ellos abiertos mide colaboración bajo condiciones de interacción degradadas, y eso debe quedar registrado en los metadatos de la sesión.
