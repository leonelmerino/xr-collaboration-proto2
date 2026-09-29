# Espejo del visor (HeadsetMirror)

Muestra en la ventana del PC lo que ve el participante en el visor: la sala de las cámaras a color más la
capa de la app. Así quien observa desde el laptop ve lo mismo que el participante.

## Por qué hace falta

En modo medición la ventana del PC se ve negra. El passthrough se compone **dentro del visor**: la app
entrega píxeles transparentes y el visor pone la cámara detrás. La imagen de las cámaras nunca llega al PC
por VIVE Streaming. La única imagen completa es la pantalla física del visor, y esa se puede capturar
por adb con el encoder H.264 del propio visor (lo mismo que hace scrcpy).

## Flujo

```
Visor (Android 12)                        PC (Windows)
scrcpy-server 4.1                         ScrcpyFrameSource (hilo propio)
  pantalla → encoder H.264 (hardware)       adb push / forward (adb de VIVE Hub)
  socket abstracto scrcpy_<scid> ──USB──►   tcp://127.0.0.1:<puerto libre>
                                              └─ ffmpeg: H.264 → RGBA WxH (vflip) por stdout
                                                   └─ FrameTripleBuffer ──► hilo principal
                                                        └─ HeadsetMirrorView: Texture2D en Canvas Overlay
```

- Solo video: sin audio ni control. El USB lo comparte con VIVE Streaming (~6 Mbit/s contra ~100 Mbit/s).
- No pasa por VIVE Streaming, SteamVR ni OpenXR, y **no toca los loggers**.
- El Canvas Overlay solo se ve en la ventana del PC, nunca en el visor. Unity avisa una vez en el log
  (`...will not be visible while in VR`): es lo buscado.

## Estructura

| Archivo | Responsabilidad |
|---|---|
| `HeadsetMirror.cs` | Punto de entrada (MonoBehaviour): arma fuente y vista, teclas, estado, estadísticas, ciclo de vida. |
| `HeadsetMirrorSettings.cs` | Configuración y argumentos de línea de comandos. |
| `HeadsetMirrorView.cs` | Canvas Overlay con la imagen (con su proporción) y la línea de estado. |
| `Capture/IHeadsetFrameSource.cs` | Contrato de una fuente de cuadros + estados. Permite otras fuentes sin tocar la vista. |
| `Capture/ScrcpyFrameSource.cs` | Sesión adb + scrcpy + ffmpeg en su hilo, watchdog de imagen detenida, reconexión con espera creciente. |
| `Capture/FrameTripleBuffer.cs` | Intercambio de cuadros entre hilos sin copias ni allocations por cuadro. |
| `Capture/ScrcpyProtocol.cs` | Todo lo que depende de la versión de scrcpy-server, los argumentos de ffmpeg y el tamaño del cuadro. |
| `Capture/FrameAnalysis.cs` | Detecta cuadros casi negros (grilla de 512 muestras por cuadro). |
| `Diagnostics/MirrorDiagnosis.cs` | Explica una pantalla negra: estado + imagen + estado XR de la app → aviso. |
| `Platform/AdbClient.cs` | Comandos de adb y parseo de su salida. |
| `Platform/ExternalTools.cs` | Ubica adb (siempre el de VIVE Hub) y ffmpeg. |
| `Platform/ChildProcess.cs` | Procesos sin ventana con salida redirigida. |
| `Platform/KillOnCloseJob.cs` | Job object de Windows: los hijos mueren con la app aunque se cierre mal. |
| `Tests/Editor/` | Tests de EditMode de todo lo que no necesita visor. |

Assembly `XRCollab.Measurement.Mirroring`, namespace igual. `MeasurementMode` lo crea al cargar la escena,
solo en Windows.

## Requisitos (en cada PC)

- **VIVE Hub**: su adb (1.0.41). Nunca otro adb: dos versiones se matan el servidor y cortan el enlace USB
  de VIVE Hub. Por eso `ExternalTools` no busca adb en el PATH.
- **ffmpeg** en el PATH o en WinGet: `winget install Gyan.FFmpeg`. En los PCs del laboratorio lo instala
  `tools\lab-remote\Install-LabDevTools.ps1`.
- Visor por **USB** con la depuración USB autorizada (`adb devices` lo muestra como `device`).
- `Assets/StreamingAssets/HeadsetMirror/scrcpy-server` (va dentro del build).

## Uso

Se enciende solo en modo medición. Teclas: **F5** muestra u oculta la imagen, **F6** alterna ambos ojos y
un ojo. Abajo a la derecha se ve el estado: serial del visor y fps, o qué falta.

| Argumento | Efecto |
|---|---|
| `-nomirror` | Desactiva el espejo. |
| `-mirror-hidden` | Arranca con la imagen oculta (F5 la muestra). |
| `-mirror-layout both\|left` | Vista inicial. |
| `-mirror-serial S` | Visor a usar si hay varios por adb (si no, el primero). |
| `-mirror-size N` | Lado mayor del video (por defecto 1280 → 1280x640). |
| `-mirror-fps N` | Tope de fps del encoder del visor (por defecto 30). |
| `-mirror-bitrate N\|6M\|6000k` | Bitrate H.264 (por defecto 6M). |
| `-mirror-adb RUTA`, `-mirror-ffmpeg RUTA` | Rutas explícitas. |

## Por qué se ve negro (aviso en pantalla)

Cuando no hay imagen útil, el centro de la ventana dice en blanco **qué pasa** y **qué hacer**, y la imagen que
quede detrás se oscurece. Lo decide `Diagnostics/MirrorDiagnosis.cs` (función pura, con tests) a partir de
cuatro fuentes:

1. **El estado del espejo y su causa** (`MirrorIssue`): faltan herramientas, no hay visor, no autorizado, en
   reposo, se cortó la imagen.
2. **La imagen misma**: si dejaron de llegar cuadros (≥ 2 s) o si llegan casi negros (≥ 2,5 s, ver
   `FrameAnalysis`; al ponerse el visor, VIVE hace un fundido desde negro de ~2 s que no debe avisar).
3. **El sensor de proximidad del visor** (`HeadsetWear`): solo se consulta por adb cuando la imagen se ve
   mal, cada 2 s como mucho. Es la señal más confiable de «no está puesto»: con el visor en la frente, VIVE
   Streaming muestra su pantalla de espera pero la sesión XR de la app puede seguir en FOCUSED a 90 fps.
4. **El lado XR de la app** (opcional, lo entrega `MeasurementMode`): el estado de la sesión OpenXR
   (`SYNCHRONIZED` = la app corre pero el visor no la muestra) y las fallas reales del passthrough.

| Aviso | Causa | Qué hacer |
|---|---|---|
| Falta el adb de VIVE Hub / Falta ffmpeg / El build está incompleto | Herramienta ausente en el PC | Instalar VIVE Hub / `winget install Gyan.FFmpeg` / recompilar |
| El visor no está conectado por USB | adb no lo ve (cable, o solo Wi-Fi) | Conectar el USB y elegir «VIVE Streaming». No usar puertos Thunderbolt/DP |
| El visor no autorizó la depuración USB | Diálogo de depuración sin aceptar | Aceptarlo dentro del visor |
| El visor está en reposo | Nadie lo tiene puesto; pantalla apagada | Ponérselo; vuelve solo |
| **El visor no está puesto** | El sensor de proximidad dice que no está sobre la cara (en la frente o en la mesa): VIVE Streaming muestra su pantalla de espera | Ponérselo bien sobre los ojos |
| **El visor pausó la imagen** | Visor en la frente o sacado, o menú del visor en primer plano: VIVE Streaming pausa y la app baja a ~20 fps | Ponérselo bien sobre los ojos |
| El visor dejó de enviar imagen | Sin cuadros ≥ 2 s con la app visible | Espera; si se durmió o se desconectó, se recupera solo |
| El passthrough no está funcionando | Falla real de `PassthroughUnderlayFeature` | USB (no DP), «MR with passthrough» activado |
| El visor muestra negro | Cuadros negros sin otra causa conocida (si el sensor dice «puesto», el problema es del streaming) | USB (no DP), «MR with passthrough», visor bien puesto |
| Se cortó la imagen / Conectando… | Reconexión en curso | Nada: se recupera solo |
| Espejo oculto | Se apretó F5 | F5 |

Con la imagen dejando de llegar, el watchdog consulta a los 2 s si el visor se durmió y corta la sesión de
inmediato (sin esperar el timeout de 10 s), así el aviso pasa rápido a «en reposo».

## Estados y problemas

| Estado en la línea de abajo a la derecha | Qué hacer |
|---|---|
| `no está el adb de VIVE Hub` | Instalar VIVE Hub. |
| `no está ffmpeg` | `winget install Gyan.FFmpeg` y reiniciar la app. |
| `conectar el visor por USB` | Conectar el cable. Por Wi-Fi no hay adb. |
| `aceptar la depuración USB en el visor` | Aceptar el diálogo dentro del visor. |
| `visor en reposo (Asleep): ponérselo para ver la imagen` | Normal si nadie lo lleva puesto: la pantalla del visor está apagada. Aparece la imagen ~2 s después de ponérselo. |
| `se cortó la imagen` / `el visor no envió imagen` | Se reintenta solo. Si se repite, revisar el log. |

- **Recuperación:** si la imagen se detiene más de 10 s o ffmpeg/el servidor mueren, la sesión se desarma y
  se vuelve a armar sola (~2 s con el visor despierto). Si la app muere de golpe, el job object mata a ffmpeg
  y al `adb shell`; el forward de adb que queda se limpia al arrancar la próxima vez. Probado el 2026-09-29.
- El log de Unity (`Player.log`) tiene todo con el prefijo `[HeadsetMirror]`: cambios de estado, las primeras
  líneas de `server:` (scrcpy) y `ffmpeg:` de cada sesión, y cada 60 s los cuadros recibidos y los fps.
- Latencia aproximada de 100–200 ms: sirve para observar, no para medir tiempos. La imagen **no se graba**.
- Usa el encoder por hardware del visor. En las pruebas del 2026-09-29, passthrough y telemetría siguieron
  a 90 Hz con el espejo encendido.
- **No agregar `-fflags nobuffer` ni un `-probesize` chico a ffmpeg**: decodifica un solo cuadro y se queda
  esperando. Hay un test que lo impide.

## Actualizar scrcpy-server

1. Reemplazar `Assets/StreamingAssets/HeadsetMirror/scrcpy-server` por el `scrcpy-server` de la nueva versión
   (viene en el zip de Windows de scrcpy) y su `LICENSE`.
2. Cambiar `ScrcpyProtocol.ServerVersion`. El servidor rechaza un cliente de otra versión, y el error aparece
   en el log como `server: ...`.
3. Revisar en el changelog de scrcpy que las opciones de `ScrcpyProtocol.ServerCommand` sigan existiendo.
4. Correr los tests y probar con un visor.

## Tests

Unity → Window → General → Test Runner → EditMode → `XRCollab.Measurement.Mirroring.Tests`. Por línea de
comandos (con el Editor cerrado):

```
Unity.exe -batchmode -projectPath . -runTests -testPlatform EditMode -testResults editmode.xml
```
