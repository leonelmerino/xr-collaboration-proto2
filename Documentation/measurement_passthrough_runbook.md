# Runbook — Sesión de registro con passthrough (Jenga físico)

**Branch:** `measurement/passthrough-physical-jenga`
**Para:** equipo de registro. Permite correr una sesión completa sin intervención del equipo de desarrollo.
**Fase del roadmap:** Fase 0 de [`roadmap_experimental.md`](roadmap_experimental.md).

## Qué hace esta versión

- Los participantes ven la **sala real** por passthrough y juegan con un **Jenga físico**.
- El Jenga virtual, el entorno virtual, la locomoción y las interacciones de poke, pinch y rayo están **desactivados, no borrados** (`Assets/Measurement/MeasurementMode.cs`).
- Siguen corriendo: mirada (`_gaze.csv`), pose de visor y manos (`_body.csv`), eventos hacia BioLab y sincronía de reloj.

| Se registra | No se registra |
| --- | --- |
| Dirección de la mirada y validez | AOI sobre objetos virtuales (ya no existen) |
| Posición y rotación del visor | Trackers de cintura y pies (Bug 1, ver abajo) |
| Posición de las manos (`hand_*_valid`) | |

## Antes de la sesión (una vez por PC)

1. **Conexión del visor: USB, no DisplayPort.** El modo DisplayPort de VIVE Streaming **no soporta passthrough**. Conectar el visor con el cable USB-C a un puerto USB del PC (no al conversor DisplayPort).
2. **VIVE Hub → Settings → VIVE Streaming → Graphics:**
   - *MR with passthrough*: **activado**.
   - *FPS*: **90 fps** (120 fps es beta y agrega riesgo de stutter).
   - *Streaming graphics preferences*: la opción de mayor calidad disponible.
3. **SteamVR** abierto y el visor en verde.
4. Revisar que exista `XR Collaboration Measurement.exe` en la carpeta del build.

## Correr la sesión

1. En **un** PC (Host), abrir `XR Collaboration Measurement.exe` y presionar **H**.
2. En los otros dos PC, abrir el mismo ejecutable y presionar **C** para conectarse.
3. Verificar en la **pantalla del PC** (no se ve en el visor):
   - abajo al centro: `[HOST]` o `[CONECTADO]`;
   - abajo a la izquierda: `MODO MEDICIÓN · passthrough: ON`.
4. Ponerse los visores: debe verse la sala real, sin objetos virtuales.
5. Jugar la tarea con el Jenga físico. Los logs se escriben solos desde que abre la aplicación.
6. Al terminar, cerrar la aplicación normalmente (Alt+F4) para que los archivos se cierren bien.

La ventana del PC se ve negra en modo medición: es esperado. El passthrough se compone en el visor, no en el PC.

## Dónde quedan los archivos

```
%USERPROFILE%\AppData\LocalLow\<Company>\<Product>\EyeTrackingLogs\{participantId}\{sessionId}\
    {taskId}_{trialId}_{NNN}_gaze.csv
    {taskId}_{trialId}_{NNN}_body.csv
```

La ruta exacta de cada PC aparece en la pantalla, en el recuadro `MODO MEDICIÓN`.

## Problemas conocidos y qué hacer

| Problema | Efecto | Qué hacer |
| --- | --- | --- |
| **Bug 2:** los tres PC escriben con el mismo `participantId` (`P001`) | Las carpetas no identifican al participante | Al copiar, renombrar la carpeta según el PC/participante |
| **Bug 1:** la calibración de trackers queda inválida | Columnas de cintura y pies no confiables | Ignorar esas columnas en esta fase; en modo medición la calibración queda desactivada |
| Passthrough `NO DISPONIBLE` | Se ve negro en el visor | Revisar que la conexión sea USB (no DisplayPort) y que *MR with passthrough* esté activado; reiniciar la aplicación |
| Manos que se pierden al manipular bloques | `hand_*_valid = 0` | Anotar; puede requerir ajustar iluminación o altura de mesa |

## Volver al modo VR

- Build: lanzar con el argumento `-vr` (acceso directo con `"XR Collaboration Measurement.exe" -vr`).
- Editor: menú **XR Collab → Modo medición (passthrough)** para desmarcarlo.
