# XR Collaboration Prototype – Development Log

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
