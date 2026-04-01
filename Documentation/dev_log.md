\# 🧪 XR Collaboration Prototype – Development Log



\## 📅 6 de marzo



\### Estado inicial

\- Estado inicial de la red multijugador y sincronización de avatares.

\- La conexión de red estaba casi funcionando.

\- Los avatares aparecían en la escena para ambos usuarios.



\### Problemas detectados

\- En el host:

&#x20; - Se veían dos avatares, pero no se movían correctamente.

&#x20; - Solo se observaba movimiento vertical (eje Y) del propio usuario.

\- En el cliente:

&#x20; - El avatar del host se movía correctamente.

&#x20; - No se observaban correctamente algunos movimientos del host.



\### Solución

\- Se identificó un problema de configuración de red.

\- Se corrigió mediante:

&#x20; - Desactivación temporal de Windows Defender Firewall

&#x20; - Activación de `Allow Remote Connections` en Unity



\### Resultado

\- Comunicación host-cliente funcionando correctamente.



\---



\## 📅 8 de marzo



\### Corrección de sincronización de avatares

\- Problemas:

&#x20; - Host no veía correctamente al cliente

&#x20; - Cliente no veía su propio avatar moverse



\### Intervención

\- Revisión de:

&#x20; - Ownership (`NetworkObject`)

&#x20; - Script `AvatarFollowXROrigin`



\### Cambios

\- Solo el owner actualiza su posición

\- Otros clientes reciben sincronización por red



\### Resultado

\- Sincronización completa:

&#x20; - Host ↔ Cliente correctos

&#x20; - Auto-avatar sincronizado



\---



\## 📅 11 de marzo



\### Hand Tracking (Quest 3)

\- Implementación con XR Hands

\- Detección de pinch funcional



\### Sistema de depuración

\- Script: `PinchDebugVisualizer`

\- Objeto: `HandDebug`

\- Marcadores:

&#x20; - Thumb

&#x20; - Index

&#x20; - Pinch



\### Problema

\- Desfase entre tracking y visualización



\### Solución

\- Corrección de espacio de coordenadas (XR Origin)



\### Resultado

\- Tracking preciso y visualización correcta



\---



\## 📅 11 de marzo (Jenga)



\### Implementación inicial

\- Prefab `JengaBlock`

\- Script `JengaTowerGenerator`



\### Problema crítico

\- Explosión física de bloques



\### Diagnóstico

\- Collider mal dimensionado



\### Solución

\- Ajuste correcto del `BoxCollider`

\- Separación física/visual del prefab



\### Resultado

\- Torre estable



\---



\## 📅 12 de marzo



\### Mejora de estabilidad

\- Generación progresiva de bloques

\- Implementación de `WaitUntilSettled`



\### Ajustes

\- Spacing

\- Drop height

\- Rigidbody config



\### Nueva interacción

\- Diseño de interacción tipo \*\*poke\*\*



\### Próximo paso

\- Implementar empuje con dedo índice



\---



\## 📅 17 de marzo



\### Interacción por raycast

\- Sistema `JengaRayGrabInteractor`

\- Visualización con `LineRenderer`



\### Integración

\- Pinch → activación de raycast



\### Problemas pendientes

\- Dirección del rayo poco natural

\- Ray basado en cámara (no mano)

\- Ray siempre activo

\- Conflicto poke vs raycast



\---



\## 📅 24 de marzo



\### Migración a HTC Vive Focus Vision



\#### Cambio estratégico

\- Se inicia transición desde \*\*Meta Quest 3 → Vive Focus Vision\*\*

\- Objetivo:

&#x20; - Mejor soporte de eye tracking

&#x20; - Pipeline OpenXR más controlado



\#### Git workflow

\- Creación de branch:



```bash

vive-focus-preview

### Actualización (31 de marzo – sesión eye tracking)

#### Git / estructura del proyecto
- Se creó el branch `eye-tracking-prototype` a partir de `main` para aislar el desarrollo del sistema de seguimiento ocular.
- Se mantuvo la estrategia de branches separadas:
  - `vive-focus-preview` → migración de hardware
  - `eye-tracking-prototype` → instrumentación de datos

---

#### Integración de Eye Tracking (HTC Vive Focus Vision)
- Se reemplazó el enfoque inicial basado en OpenXR genérico por el uso directo del SDK de HTC (`XR_HTC_eye_tracker`).
- Se confirmó que:
  - el eye tracking funciona correctamente a nivel de sistema (menús del visor)
  - requiere calibración previa en el headset
- Se detectó que:
  - en modo streaming (PCVR) el tracking puede perder datos si la sesión no está completamente inicializada

---

#### Implementación del proveedor de datos
- Se implementó `ViveEyeTrackingProvider` para capturar:
  - gaze por ojo (left/right)
  - gaze combinado
  - pupil diameter
  - pupil position
  - eye openness
- Se agregaron mecanismos de robustez:
  - manejo de excepciones (`XR_ERROR_SESSION_LOST`)
  - throttling de logs para evitar saturación de consola

---

#### Métricas geométricas adicionales
- Se incorporaron nuevas métricas derivadas directamente de datos raw:
  - `vergenceAngleDeg`
  - `interocularDistance`
- Estas métricas permiten estimar profundidad de atención sin depender de raycast

---

#### Sistema de logging (CSV)
- Se implementó `EyeTrackingSessionLogger`
- Características:
  - escritura continua por frame
  - timestamps duales:
    - relativo (`timestamp_rel_s`)
    - absoluto (`timestamp_utc_iso`)
  - estructura extensible para experimentos

---

#### Mejora en almacenamiento de datos
- Se implementó generación de archivos incrementales:
  - evita sobreescritura
  - permite múltiples trials por sesión
- Estructura final:

```

EyeTrackingLogs/<participant>/<session>/

```

- Ejemplo:

```

task_01_trial_01_001_gaze.csv
task_01_trial_01_002_gaze.csv

```

---

#### Integración con AOIs (Jenga)
- Se integró automáticamente `AOITag` en `JengaTowerGenerator`
- Cada bloque ahora contiene:
  - identificador único estructurado
  - metadata (nivel, posición)
- Se implementó `GazeTargetRaycaster` para:
  - detectar intersecciones gaze → objeto
  - registrar AOI hit en el dataset

---

#### Validación del sistema
- Se verificó que:
  - los archivos CSV se generan correctamente
  - los datos de cabeza y gaze combinado son válidos
  - los AOIs se registran correctamente
- Problemas observados:
  - pupil diameter constante (posible limitación del SDK o condiciones de iluminación)
  - errores intermitentes `XR_ERROR_SESSION_LOST` no bloqueantes

---

#### Decisión metodológica clave
- Se decidió explícitamente:
  - registrar únicamente **datos raw**
  - no calcular métricas como fixations o saccades en Unity
- Todo el procesamiento se realizará **offline (Python / análisis posterior)**

---

#### Estado del sistema
- Pipeline completo operativo:
  - captura gaze ✔
  - captura pupil ✔ (con limitaciones)
  - AOI tracking ✔
  - logging CSV ✔
- Sistema listo para:
  - recolección de datos experimentales
  - integración con eventos de interacción (siguiente paso)

---

#### Próximos pasos
- Incorporar logging de eventos:
  - pinch
  - grab
- Evaluar calidad real de pupil data
- Resolver warnings de streaming (missing frames)
- Preparar pipeline de análisis offline


