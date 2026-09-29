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

1. **Conexión del visor: USB, no DisplayPort.** El modo DisplayPort de VIVE Streaming **no soporta passthrough**. Conectar el visor con el cable USB-C a un puerto USB del PC (no al conversor DisplayPort). Ojo: un puerto USB-C Thunderbolt (⚡) o DisplayPort del laptop también hace entrar al visor en DP; usar un USB-C sin video o un USB-A.
2. **VIVE Hub → Settings → VIVE Streaming → Graphics:**
   - *MR with passthrough*: **activado**.
   - *FPS*: **90 fps** (120 fps es beta y agrega riesgo de stutter).
   - *Streaming graphics preferences*: la opción de mayor calidad disponible.
3. **SteamVR** abierto y el visor en verde.
4. Revisar que exista `XR Collaboration Measurement.exe` en la carpeta del build.

## Correr la sesión

1. En **un** PC (Host), abrir `XR Collaboration Measurement.exe` y presionar **H**.
2. En los otros dos PC, abrir el mismo ejecutable y presionar **C** para conectarse.
   - En vez de las teclas: lanzar con `-role host` o `-role client` (no depende de que la ventana tenga el foco). `Start-LabSession.ps1 <pc> -Role host|client` ya lo hace.
3. Verificar en la **pantalla del PC** (no se ve en el visor):
   - abajo al centro: `[HOST]` o `[CONECTADO]`;
   - abajo a la izquierda: `MODO MEDICIÓN · passthrough: ON`;
   - abajo a la derecha: `Espejo del visor · <serial> · 30 fps`, y detrás, lo que ve el participante.
4. Ponerse los visores: debe verse la sala real, sin objetos virtuales.
5. Jugar la tarea con el Jenga físico. Los logs se escriben solos desde que abre la aplicación.
6. Al terminar, cerrar la aplicación normalmente (Alt+F4) para que los archivos se cierren bien.

**Espejo del visor:** la ventana del PC muestra lo que ve el participante (sala a color + la app), capturado del visor por USB. **F5** lo oculta o muestra, **F6** alterna ambos ojos y un ojo; `-nomirror` lo desactiva. Necesita VIVE Hub y ffmpeg. Detalle: `Assets/Measurement/HeadsetMirror/README.md`.

## Dónde quedan los archivos

Todo queda bajo `Application.persistentDataPath` del build:
`%USERPROFILE%\AppData\LocalLow\DefaultCompany\xr-collaboration-proto2\`

```
EyeTrackingLogs\{participantId}\{sessionId}\          (hoy P001\S001 en todos los PC, ver Bug 2)
    {taskId}_{trialId}_{NNN}_gaze.csv                 mirada + pose de cabeza, ~90 Hz
    {taskId}_{trialId}_{NNN}_body.csv                 cabeza, manos (y trackers), ~90 Hz
    {taskId}_{trialId}_{nodeId}_{NNN}_events.csv      eventos del experimento y sync de reloj (nodeId p. ej. VR_HOST)
NetworkAudit\network_audit_{yyyyMMdd_HHmmss}.csv      host/cliente, conexiones, fallos de transporte
Player.log / Player-prev.log                          log de Unity (sesión actual / anterior)
acquisition_mock_log.txt                              solo si corre el mock de adquisición
```

- `NNN` sube en cada ejecución (001, 002, …); nunca se sobrescribe.
- La ruta exacta de cada PC aparece en la pantalla, en el recuadro `MODO MEDICIÓN`.
- **Cerrar con Alt+F4** (o `Start-LabSession.ps1 <pc> -Stop`): los CSV se cierran en `OnApplicationQuit`. Matar el proceso puede dejar la última parte sin escribir.
- Verificación de solo lectura (¿se creó?, ¿tiene datos?, ¿se ve razonable?): `dictuc\tools\lab-remote\Check-LabTelemetry.ps1`.

**Valores de referencia (STIMULUS1, 2026-09-24, USB, passthrough visible):** gaze y body a **89,9 Hz**, tiempos siempre crecientes, cuaterniones y direcciones de mirada unitarios, manos válidas **91–94 %**, `is_calibrated = 0` (esperado). La mirada solo es válida con el visor bien puesto: en ventanas de 10 s con el visor puesto, 64–82 % válida; con el visor en la frente, 0 %.

## Passthrough en PC: cómo funciona (y por qué no con las features de VIVE)

El passthrough lo crea y lo envía **`Assets/Measurement/PassthroughUnderlayFeature.cs`** (feature OpenXR propia "XR Collab: Passthrough underlay (PC)"): llama a `xrCreatePassthroughHTC` y en cada `xrEndFrame` agrega una capa `XrCompositionLayerPassthroughHTC` **debajo** de la proyección de Unity (con `BLEND_TEXTURE_SOURCE_ALPHA`), igual que `dictuc\tools\xr-passthrough-test`. Las dos features de passthrough de VIVE (com.htc.upm.vive.openxr 2.5.1) **no sirven en PC con backend Mono**:

| Feature VIVE | Qué pasa en PC | Síntoma |
| --- | --- | --- |
| VIVE XR Passthrough | Su hook de `xrWaitFrame` (ViveInterceptors) usa otro tipo de delegate que el del VIVE XR Eye Tracker; `Marshal.GetDelegateForFunctionPointer` lanza `InvalidCastException` dentro del callback nativo | La app se congela en el primer frame, **no se crea ningún CSV** |
| VIVE XR Composition Layer (Passthrough) | Crea el passthrough, pero `XR_HTC_passthrough_impls` no implementa `SubmitLayers` en Standalone | La capa nunca llega al visor: **negro** |

Ambas quedan apagadas y la nuestra encendida. **Build siempre con el menú `XR Collab → Build medición (Win64)`** (o `-executeMethod MeasurementBuild.BuildWin64`), que fuerza esa configuración.

**Lo que se ve en la ventana del PC viene del espejo, no del render.** La imagen de las cámaras nunca llega al PC por el streaming; el visor compone la sala con la capa de la app. Por eso `HeadsetMirror` captura la pantalla del visor por adb (scrcpy-server + ffmpeg) y la dibuja en la ventana del PC. Con `-nomirror` la ventana vuelve a verse negra, y eso es normal.

## Problemas conocidos y qué hacer

| Problema | Efecto | Qué hacer |
| --- | --- | --- |
| **Bug 2:** los tres PC escriben con el mismo `participantId` (`P001`) | Las carpetas no identifican al participante | Al copiar, renombrar la carpeta según el PC/participante |
| **Bug 1:** la calibración de trackers queda inválida | Columnas de cintura y pies no confiables | Ignorar esas columnas en esta fase; en modo medición la calibración queda desactivada |
| Passthrough `NO DISPONIBLE` | Se ve negro en el visor | Revisar que la conexión sea USB (no DisplayPort) y que *MR with passthrough* esté activado; reiniciar la aplicación |
| Manos que se pierden al manipular bloques | `hand_*_valid = 0` | Anotar; puede requerir ajustar iluminación o altura de mesa |
| Espejo del visor sin imagen | Ventana del PC negra; abajo a la derecha dice qué falta (`visor en reposo`, `conectar el visor por USB`, `no está ffmpeg`...) | Ponerse el visor o reconectar el USB: vuelve solo en ~2 s. Detalle en `Assets/Measurement/HeadsetMirror/README.md`. No afecta los logs |

## Volver al modo VR

- Build: lanzar con el argumento `-vr` (acceso directo con `"XR Collaboration Measurement.exe" -vr`).
- Editor: menú **XR Collab → Modo medición (passthrough)** para desmarcarlo.
