# XR Collaboration Prototype – Development Log

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
