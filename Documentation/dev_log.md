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

