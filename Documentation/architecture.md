# Arquitectura del sistema

**Proyecto:** XR Collaboration Prototype 2 — ANID Exploración 13250116
**Fecha:** 2026-09-14
**Audiencia:** ingeniero que se incorpora al equipo y ya vio el sistema funcionando en el laboratorio.

Este documento explica **cómo está construido** el sistema: qué subsistemas existen, qué hace cada uno, cómo fluyen los datos y dónde están los acoplamientos que no son evidentes leyendo el código.

Para operar el sistema, ver [`test_protocol.md`](test_protocol.md) y [`body_tracking_guide.md`](body_tracking_guide.md). Para el historial de decisiones, [`dev_log.md`](dev_log.md). Para lo que está roto hoy, [`bug_dev.md`](bug_dev.md).

---

## 1. Qué es el sistema

Una aplicación Unity que sostiene sesiones experimentales de colaboración en XR. Tres participantes, cada uno con su propio PC y visor, comparten una sala virtual donde manipulan una torre de Jenga con las manos. Mientras juegan, el sistema registra su mirada, el movimiento de su cuerpo y marca los eventos de la tarea contra un sistema externo de adquisición psicofisiológica.

El software no es el objeto de investigación: es el instrumento. Esa distinción explica varias decisiones de diseño que de otro modo parecerían extrañas — por ejemplo, que se registren datos crudos en CSV y se deje todo el procesamiento para análisis offline.

### Restricciones que condicionan todo el diseño

| Restricción | Consecuencia |
|---|---|
| Sin servicios cloud | Todo corre en la LAN del laboratorio; descubrimiento por broadcast UDP |
| Tres nodos simétricos en hardware | Cada PC corre la aplicación completa; los roles se asignan en runtime |
| Latencia importa | Tasa de actualización alta, payloads acotados, sincronía de relojes entre nodos |
| Los datos son el producto | El registro no puede degradarse aunque falle la experiencia |
| Built-in Render Pipeline | Heredado; condiciona materiales e iluminación |

### Stack

Unity **2022.3.62f3** · C# · OpenXR · Netcode for GameObjects 1.12.2 · XR Hands 1.4.3 · XR Interaction Toolkit 2.6.5 · VIVE OpenXR 2.5.1 (OpenUPM) · Animation Rigging 1.2.1

Hardware: HTC Vive Focus Vision por PCVR · VIVE Ultimate Trackers (3 por participante) · polígrafos MindWare con BioLab · router local.

---

## 2. Mapa de subsistemas

Siete subsistemas, 72 scripts de producción. La tabla es el índice del resto del documento.

| Subsistema | Carpeta | Archivos | Responsabilidad |
|---|---|---|---|
| Red y descubrimiento | `Assets/Multiplayer/Discovery/`, raíz | 4 | Levantar host/cliente, encontrarse en la LAN, sincronizar transforms |
| Roles y sesión | `Assets/Multiplayer/` | 6 | Asignar Host/Client/Helper, ubicar a cada quien en su asiento |
| Avatares y pose | `Assets/Multiplayer/` | 6 | Capturar la pose del usuario, sincronizarla, animar el avatar remoto |
| Interacción Jenga | `Assets/Jenga/` | 8 | Torre, física, poke, grab, propiedad de los bloques |
| Registro de mirada | `Assets/EyeTracking/` | 4 | Eye tracking, AOI, CSV de mirada |
| Registro corporal | raíz | 5 | Trackers, calibración, CSV de cuerpo |
| Sincronía con BioLab | `Assets/BiolabUDPSync/` | 10 | Relojes, eventos de tarea, adquisición externa |
| *(Herramientas)* | `Assets/NetworkAudit/`, `Assets/Editor/` | 14 | Diagnóstico, HUDs, validadores, generadores |

---

## 3. Los dos flujos de datos

Casi todo el sistema se entiende siguiendo dos caminos que arrancan en el mismo lugar y no se cruzan.

### 3.1 Camino de la experiencia — lo que los participantes ven

```
XR Hands / HMD  (local, cada nodo)
        │
        ▼
NetworkedAvatarPose        ← el owner escribe NetworkVariables
        │
        │  NGO, ~30 Hz
        ▼
AvatarPoseDriver           ← todos los nodos leen y aplican
        │
        ▼
Huesos del avatar + IK de brazos + 15 huesos de dedos por mano
```

La pose viaja en **espacio local del root del avatar**, no en mundo, para ser independiente de dónde esté ubicado físicamente cada avatar. Se sincronizan dos NetworkVariables separadas —`Pose` (~80 bytes) y `Fingers` (~460 bytes)— precisamente para que un movimiento de cabeza no obligue a retransmitir el blob de dedos. Total ~16 KB/s por avatar.

### 3.2 Camino del registro — lo que queda para el análisis

```
XR Hands / HMD / Trackers / Eye tracker   (local, cada nodo)
        │
        ├──► EyeTrackingSessionLogger  ──►  *_gaze.csv
        │
        ├──► BodyTrackingSessionLogger ──►  *_body.csv
        │
        └──► AcquisitionEventManager   ──►  UDP  ──►  BioLab (PC de adquisición)
```

**Este camino no pasa por la red del juego.** Cada nodo escribe sus propios archivos localmente y manda sus eventos directo al PC de adquisición. La consecuencia práctica es importante: **el registro sigue funcionando aunque la capa multijugador falle**, y es la base de la estrategia de desacople descrita en [`roadmap_experimental.md`](roadmap_experimental.md).

Lo único que une temporalmente los tres nodos es `NetworkClockSync`, que establece el reloj del Host como referencia mediante un handshake tipo NTP.

---

## 4. Subsistema por subsistema

### 4.1 Red y descubrimiento

| Archivo | Rol |
|---|---|
| `NetworkLauncher.cs` | Punto de entrada. Teclas `H` y `C`. HUD de estado |
| `Discovery/LanDiscoveryService.cs` | Broadcast UDP del host; escucha del cliente |
| `Discovery/DiscoveryRecord.cs` | Estructura del anuncio |
| `OwnerNetworkTransform.cs` | `NetworkTransform` con autoridad del dueño, no del servidor |

**Cómo arranca una sesión.** El operador presiona `H` en Stimulus 1: `NetworkLauncher.StartHost()` levanta NGO y arranca el anuncio por UDP 7778. En los otros dos, `C` lanza una corrutina que escucha ese broadcast, extrae la IP y el puerto de juego, y conecta por UDP 7777. **No hay IP configurada a mano en ninguna parte** — el campo de `UnityTransport` en la escena es solo un respaldo.

El protocolo del anuncio lleva un magic `XRCOLLAB|` y versión; cualquier paquete que no lo traiga se descarta.

`OwnerNetworkTransform` sobreescribe `OnIsServerAuthoritative()` a `false`. **Esta línea explica buena parte del comportamiento del Jenga** y se detalla en §4.4.

### 4.2 Roles y sesión

| Archivo | Rol |
|---|---|
| `RoleAssignmentService.cs` | Solo en el servidor. Asigna roles por orden de conexión y teleporta al asiento |
| `RoleConfig.cs` | Posiciones de spawn y centro de la mesa |
| `NetworkedAvatarRole.cs` | El rol vive en una NetworkVariable; al cambiar, repinta y reubica |
| `AvatarRoleMeshSwitcher.cs` | Activa solo el sub-mesh del rol asignado |
| `RoleAvatarPresenter.cs`, `TriadSessionManager.cs` | Presentación y configuración de slots |

Los roles se asignan **por orden de llegada**: Host → Client → Helper. El servidor escribe el rol en una NetworkVariable y envía un ClientRpc al dueño para teleportar su XR Origin al asiento correspondiente.

`AvatarRoleMeshSwitcher` activa únicamente el sub-mesh del rol. Los otros dos quedan desactivados, y como los componentes en GameObjects inactivos no procesan `Update`, se evita el costo de tener tres Animators humanoides evaluando a la vez.

### 4.3 Avatares y pose

| Archivo | Rol |
|---|---|
| `NetworkedAvatarPose.cs` | El owner captura y publica; define `AvatarPoseState` y `HandFingerPose` |
| `AvatarPoseDriver.cs` | Cada nodo aplica la pose recibida al rig humanoide |
| `AvatarFollowXROrigin.cs` | Mueve el root del avatar siguiendo al HMD; oculta el cuerpo propio |
| `HandRayDriver.cs` | Ancla y orienta el rayo de selección |
| `NetworkedAvatarHands.cs` | **Legacy.** Solo sobrevive el rayo remoto — hoy deshabilitado (Bug 11) |
| `AvatarHandTunerHUD.cs` | HUD de ajuste manual de muñeca. Residual |

Los avatares son modelos **Microsoft Rocketbox** (CC0), uno por rol.

**La decisión de diseño más importante de este subsistema** es cómo se orientan las manos. Las primeras dos estrategias —calibración manual con una tecla, y un HUD para ajustar offsets Euler— fracasaron porque eran impracticables. La tercera funciona y es la que está en producción: **derivar la orientación de la palma geométricamente a partir de tres joints** (muñeca, metacarpo medio, metacarpo del pulgar), construyendo una base ortonormal. No depende de la convención de ejes que reporte el SDK, no necesita calibración, y funcionaría con cualquier rig humanoide.

Por eso `AvatarPoseState` sincroniza *posiciones de tres joints por mano* en vez de la rotación de la muñeca.

Los dedos se manejan aparte: 15 huesos por mano, cada uno rotado con `Quaternion.FromToRotation` para apuntar en la dirección del segmento equivalente del usuario.

**Visibilidad del dueño.** El owner no ve su propia cabeza — el hueso se escala a `0.0001` en vez de ocultar renderers, para que siga viendo su cuerpo y sus manos. No se usa `Vector3.zero` porque una escala exactamente cero produce matrices singulares en el skinning.

### 4.4 Interacción con el Jenga

| Archivo | Rol |
|---|---|
| `JengaTowerGenerator.cs` | Construye la torre de forma progresiva, esperando que cada bloque se estabilice |
| `NetworkedJengaBlock.cs` | Propiedad del bloque, `RequestGrab`, `RequestPush`, gestión de `isKinematic` |
| `JengaGrabbable.cs` | Mueve el bloque siguiendo la mano mientras está agarrado |
| `JengaPokeInteractor.cs` | Empuje con el índice |
| `JengaRayGrabInteractor.cs` | Rayo + pinch para agarrar a distancia |
| `JengaGrabInteractor.cs` | Agarre directo |
| `JengaBlockTag.cs`, `Editor/AssignJengaAOIs.cs` | Identidad del bloque y asignación de AOI |

**Este es el subsistema donde vive el defecto P0 abierto, y conviene entender por qué.**

Los bloques son `NetworkObject` con `OwnerNetworkTransform`, es decir **autoridad del dueño, no del servidor**. Por defecto el dueño es el servidor, que simula la física de los bloques libres y propaga las poses.

La consecuencia es que **cualquier interacción física que un no-dueño quiera ejercer tiene que rutearse por RPC al dueño**. Un `AddForce` local en el Client es un no-op efectivo: al frame siguiente `NetworkTransform` sobreescribe la posición con la del servidor. Ese fue exactamente el bug del poke, ya resuelto ruteando por `RequestPush` → `ApplyPushServerRpc`.

El grab funciona distinto: el cliente pide la propiedad (`RequestGrabServerRpc` → `ChangeOwnership`), y al recibirla arranca el agarre local. Esa transferencia de propiedad es donde vive el **Bug 12** — el salto de ~20 cm al hacer pinch en Client y Helper. Hay una hipótesis ya refutada y tres candidatas abiertas, documentadas en [`bug_dev.md`](bug_dev.md).

> **Patrón a recordar:** en NGO con transforms autoritativos del dueño, la sintomatología típica de este error es *"funciona en el Host, no funciona en los clientes"*. Si ves eso, sospecha de una operación física ejecutada localmente por un no-dueño.

### 4.5 Registro de mirada

```
ViveEyeTrackingProvider  ──►  GazeTargetRaycaster  ──►  EyeTrackingSessionLogger  ──►  *_gaze.csv
   (SDK de HTC)              (raycast contra AOITag)         (52 columnas)
```

`ViveEyeTrackingProvider` usa el SDK de HTC directamente (`XR_HTC_eye_tracker`), no la abstracción genérica de OpenXR, porque la genérica no entregaba los datos necesarios. Es el componente más frágil del sistema: lanza excepciones si se lo consulta antes de que la sesión XR esté lista, y también cuando el visor reporta que no soporta eye tracking. Ambos casos están contenidos con guardas y throttling de warnings.

`GazeTargetRaycaster` lanza el rayo de mirada y reporta contra qué `AOITag` chocó. En el Jenga, cada bloque lleva un tag con formato `jenga_l<nivel>_<lado>_<orientación>`.

El formato completo del CSV está en [`eye_tracking_data_format.md`](eye_tracking_data_format.md).

### 4.6 Registro corporal

| Archivo | Rol |
|---|---|
| `TrackerBodyCalibration.cs` | Identifica los 3 trackers, calcula offsets. Teclas `C` y `R` |
| `BodyTrackingSessionLogger.cs` | CSV de 44 columnas con trackers, manos y cabeza |
| `TrackerPoseDriver.cs` | Aplica las posiciones a los huesos del avatar |
| `TrackerVisualizer.cs` | Esferas de diagnóstico |
| `TrackerSystemBootstrap.cs` | Crea el GameObject `[TrackerSystem]` al entrar en Play |

**Dos particularidades que sorprenden a quien llega nuevo.**

La primera: los VIVE Ultimate Tracker reportan en un sistema donde **+X es la izquierda física y −Z es adelante**. `TrackingToWorld()` niega ambos ejes antes de transformar a mundo. Esa negación aplica **solo** a los trackers vía `InputDevices` — el HMD y los joints de XR Hands usan OpenXR estándar y jamás deben negarse.

La segunda: el sistema **no está en la escena**. `TrackerSystemBootstrap` lo crea por código al entrar en Play, mediante `[RuntimeInitializeOnLoadMethod]`. Eso hace que sus campos no sean configurables antes de correr, que es la causa del Bug 2.

La identificación de los trackers es puramente geométrica: el más alto es la cintura, y entre los dos restantes gana el pie derecho por producto punto contra `hmd.right`. **El sistema ignora los roles asignados en SteamVR.**

La operación está documentada en [`body_tracking_guide.md`](body_tracking_guide.md).

### 4.7 Sincronía con BioLab

| Archivo | Rol |
|---|---|
| `NetworkClockSync.cs` | Handshake tipo NTP contra el Host; establece el offset de cada nodo |
| `AcquisitionEventManager.cs` | Orquesta sesión y tarea; fallback automático a mock |
| `BioLabSessionCoordinator.cs` | `BeginSession`, `EndSession`, `ReportInteractionEvent` |
| `BioLabUdpClient.cs` | Cliente UDP hacia el PC de adquisición |
| `AcquisitionMockServer.cs` | Servidor simulado para desarrollo sin laboratorio |
| `ExperimentEventLogger.cs` | Espejo local de los eventos |
| `VrTaskButton.cs`, `LocalSyncMarkerReceiver.cs` | Botones en la escena, marcas locales |

El Host es la referencia temporal (offset cero). Cada cliente ejecuta un handshake al conectarse y conserva su offset, lo que permite alinear los eventos de los tres nodos en una misma línea de tiempo.

El mock es **fallback automático**: se hace ping a la IP configurada y, si no responde, se levanta un servidor simulado en loopback. Por eso el sistema corre completo en una máquina sin laboratorio.

> **Detalle operativo:** BioLab solo escucha en su puerto después de que alguien selecciona un archivo de salida en su pantalla de adquisición. Si los eventos no llegan, ese es el primer lugar donde mirar.

### 4.8 Herramientas de diagnóstico

`Assets/NetworkAudit/` permite correr la aplicación **sin XR** (`BuildXRDisabler`), con cámara libre (`BuildFlyCamera`) y HUDs de red y rendimiento. Es la base sobre la que el [`plan_via_b.md`](plan_via_b.md) propone construir el cliente espectador.

`Assets/Editor/` tiene validadores y generadores: `AvatarSetupValidator` —que ya detectó un constraint mal configurado y es el precedente del lint de assets—, `AvatarFootAlignTool`, `RocketboxMaterialSetup`, `RoomLighting`, `RoomPopulator`.

---

## 5. Qué corre dónde

Tabla de referencia rápida. Confundir estas categorías es la fuente de error más común en este código.

| Dato | Ámbito | Autoridad |
|---|---|---|
| Pose del HMD y de las manos | Local, luego sincronizado | Dueño |
| Root del avatar | Sincronizado vía `OwnerNetworkTransform` | Dueño |
| Rol del participante | NetworkVariable | Servidor |
| Pose de los bloques | Sincronizado vía `OwnerNetworkTransform` | Dueño (por defecto el servidor) |
| Propiedad de un bloque | `ChangeOwnership` por RPC | Servidor |
| Rayo de selección propio | **Solo local** | — |
| Rayo de selección remoto | Sincronizado (hoy deshabilitado) | Dueño |
| Trackers corporales | **Solo local** | — |
| Mirada y AOI | **Solo local** | — |
| Archivos CSV | **Solo local**, uno por nodo | — |
| Eventos de tarea | Local → UDP al PC de adquisición | — |

---

## 6. Acoplamientos no evidentes

Estos son los que han causado regresiones. Conviene conocerlos antes de tocar código.

**Los markers de `PinchDebugVisualizer` son datos de física, no decoración.** Los Transform `rightIndex`, `rightPinch`, `leftIndex` y `leftPinch` están referenciados directamente por `JengaPokeInteractor` como `pokePoint` y por `JengaRayGrabInteractor` como `pinchPoint`. Agrupar su actualización de posición dentro de un `if (ShowVisualizers)` congela la interacción con el Jenga sin ningún error en consola. Ya ocurrió. **La posición de un Transform y la visibilidad de su renderer son responsabilidades independientes.**

**La API de Unity no es segura fuera del main thread.** `LanDiscoveryService` parsea los broadcast en un thread de fondo, y una lectura de `Time.realtimeSinceStartup` allí lanza `UnityException` — que un `catch` vacío se tragaba en silencio. El patrón correcto es copiar el valor en el main thread a un campo `volatile`.

**Los flags de debug con `[SerializeField]` se filtran a producción.** Si alguien activa la visualización en runtime y guarda la escena, el valor persiste. Los toggles que solo sirven para diagnóstico **no deben ser serializados**.

**Agregar un campo serializado a un componente ya instanciado en prefabs puede romper la build**, con un error de *script class layout incompatible*. La salida pragmática es declararlo sin `[SerializeField]`.

**Los `TwoBoneIKConstraint` no avisan cuando su Target es nulo**: simplemente no hacen nada y el brazo queda en pose de bind. `AvatarSetupValidator` existe por esto.

---

## 7. Código legacy

Un tercio del subsistema de manos está apagado pero presente. Saberlo evita perder días entendiendo código que no corre.

| Archivo | Estado | Por qué sigue ahí |
|---|---|---|
| `NetworkedAvatarSkeleton.cs` | Apagado | Esferas del esqueleto remoto, reemplazadas por el avatar humanoide |
| `HandSkeletonRenderer.cs` | Apagado | Equivalente local |
| `AvatarHandJointDebugViz.cs` | Apagado (toggle `F5`) | Validación geométrica de la estrategia de palma |
| `NetworkedAvatarHands.cs` | Deshabilitado en el prefab | Solo sobrevive el subsistema del rayo remoto |
| `PinchDebugVisualizer.cs` | **Parcial** | Los visuales están apagados; **la detección de pinch y las posiciones siguen vivas** |
| `AvatarHandTunerHUD.cs` | Residual (toggle `F4`) | Ajuste manual de muñeca, innecesario con la estrategia geométrica |

Todos están protegidos por una constante `ShowVisualizers = false` y, además, por referencias vacías en el prefab humanoide.

`PinchDebugVisualizer` es la excepción peligrosa: **no se puede borrar sin más**, porque su lógica de detección de pinch y sus Transform alimentan la interacción con el Jenga (§6).

El `plan_via_b.md` propone eliminar los cinco primeros antes del refactor de entrada, porque reduce la superficie de diez archivos a cinco.

---

## 8. Configuración por máquina

| Qué | Dónde | Notas |
|---|---|---|
| Rol | Orden de conexión (`H` primero) | No está configurado por archivo |
| IP | DHCP del router; discovery lo resuelve en runtime | Ver [`network_setup.md`](network_setup.md) |
| Puertos | UDP 7777 juego, UDP 7778 discovery | Reglas de firewall por máquina |
| IP del PC de adquisición | Inspector de `AcquisitionConfig` | Con fallback a mock |
| Metadata de sesión | Inspector del `EyeTrackingSessionLogger` | El logger de cuerpo **no** es configurable — Bug 2 |
| Ruta de salida | `Application.persistentDataPath/EyeTrackingLogs/` | Local a cada nodo |

---

## 9. Por dónde empezar

Orden sugerido para las primeras dos semanas.

**Leer en este orden.** `NetworkLauncher.cs` (cómo arranca todo, 200 líneas) → `NetworkedAvatarPose.cs` y `AvatarPoseDriver.cs` (el camino de la experiencia) → `NetworkedJengaBlock.cs` (el modelo de propiedad, y donde está el bug abierto) → `BodyTrackingSessionLogger.cs` (el camino del registro) → la entrada del `dev_log` del 2026-05-24, que documenta las tres iteraciones de orientación de manos y explica por qué el código es como es.

**Correr sin laboratorio.** El mock de BioLab levanta solo; `BuildXRDisabler` permite correr sin visor. Se puede tener Host en el Editor y dos clientes como builds en la misma máquina.

**Dónde tocar según la tarea:**

| Si hay que… | Ir a |
|---|---|
| Cambiar cómo se ve o se anima un avatar | `AvatarPoseDriver.cs`, `AvatarHumanoid.prefab` |
| Cambiar la interacción con los bloques | `Jenga/`, y entender §4.4 antes |
| Agregar una columna a un CSV | El logger correspondiente **y** su documento de formato |
| Agregar un evento a BioLab | `BioLabSessionCoordinator.ReportInteractionEvent` |
| Cambiar el descubrimiento de red | `Discovery/LanDiscoveryService.cs` |
| Agregar una tecla | Revisar antes qué teclas están tomadas — hay colisiones conocidas (Bugs 1 y 9) |

---

## 10. Estado actual

Esta arquitectura describe lo que existe, no lo que debería existir. Tres cosas que conviene tener presentes:

**Hay defectos abiertos que afectan la operación.** El más grave hace inutilizable el agarre a distancia en dos de los tres roles. Están en [`bug_dev.md`](bug_dev.md), priorizados.

**No hay ningún test automatizado, ni assembly definitions.** Todo el código vive en `Assembly-CSharp`. El plan para resolverlo está en [`plan_via_b.md`](plan_via_b.md).

**El proyecto se está reorganizando en dos vías paralelas** — medición y desarrollo VR — descritas en [`roadmap_experimental.md`](roadmap_experimental.md). Vale la pena leerlo para entender qué se espera del desarrollo en los próximos meses.

---

## 11. Documentos relacionados

| Documento | Para qué |
|---|---|
| [`roadmap_experimental.md`](roadmap_experimental.md) | Plan del proyecto y estrategia de las dos vías |
| [`plan_via_b.md`](plan_via_b.md) | Infraestructura de testeo, refactor de entrada, guantes hápticos |
| [`bug_dev.md`](bug_dev.md) | Defectos abiertos con causa raíz y corrección propuesta |
| [`dev_log.md`](dev_log.md) | Historial cronológico de decisiones técnicas |
| [`test_protocol.md`](test_protocol.md) | Protocolo de sesión de validación |
| [`body_tracking_guide.md`](body_tracking_guide.md) | Operación de trackers y formato del CSV de cuerpo |
| [`eye_tracking_data_format.md`](eye_tracking_data_format.md) | Formato del CSV de mirada |
| [`network_setup.md`](network_setup.md) | Configuración de red del laboratorio |
