# Grabación de la ventana del PC (SessionRecorder)

Respaldo en video de cada sesión: graba la ventana del PC **tal como se ve**. Incluye el espejo del visor, los HUD,
los avisos en blanco y una barra arriba con **la hora UTC y local con milisegundos** y el número de cuadro de
Unity. Graba también con el visor sacado o desconectado: la ventana se graba igual.

**La telemetría no depende de esto.** Mirada, cabeza, manos, eventos, auditoría de red y sincronía de reloj se
registran siempre mientras la app está abierta, se grabe video o no. El video es solo un respaldo.

## Control desde afuera (iniciar / detener)

La app abre **en espera** (barra gris `VIDEO sin grabar`: no se graba video; la telemetría sí) y no tiene lógica de laboratorio, red ni SSH. Escucha
órdenes de una línea **solo en `127.0.0.1:47811`** (`RecordingControlServer`) y responde una línea JSON:

```
status
start [label=S01] [at=2026-10-01T18:00:05.000Z]
stop  [at=2026-10-01T18:40:00.000Z]
mark  texto libre
```

- `at` es una hora **UTC** absoluta: varios PCs con el reloj sincronizado (NTP) empiezan en el mismo instante
  aunque la orden les llegue con distinta demora. Sin `at`, corre apenas llega. Mientras espera la hora, la barra
  dice `VIDEO empieza en 2.3 s` en amarillo; grabando, `REC 00:00:01` en rojo.
- Cada `start` crea su carpeta. `stop` cierra el último segmento **sin frenar la app** (lo hace un hilo aparte) y
  arma el `.mp4`. Se puede volver a hacer `start` después.
- Herramientas del laboratorio (fuera de la app): `tools\lab-remote\Send-RecordingCommand.ps1` (una orden a la
  app de este PC), `Lab-Recording.ps1` (los 3 PCs a la vez, por SSH, con hora común) y `Lab-RecordingConsole.ps1`
  (consola de una tecla en WezTerm; acceso directo «XR Grabación (sesión)» en el escritorio).
- `-rec-auto` graba desde que abre la app (comportamiento anterior). `-rec-nocontrol` apaga el puerto.

## Flujo

```
fin de cada cuadro (30 fps)                       hilo de la grabación                disco
ScreenCapture → RenderTexture → escalado (GPU)     cuadros RGBA → ffmpeg (stdin)       segments/seg_00000.ts (60 s)
  → AsyncGPUReadback (no bloquea)  ─cola (8)─►        H.264 NVENC / QuickSync / x264     frames.csv (UTC por cuadro)
```

- **Encoder:** se elige una vez al abrir la app (en segundo plano), probando `h264_nvenc` (GPU NVIDIA),
  `h264_qsv` (GPU Intel) y `libx264` (CPU). STIMULUS2 cae a QuickSync (driver NVIDIA viejo).
- **Segmentos MPEG-TS de 60 s:** si la app se cierra de golpe o se cuelga, se pierde como mucho el final del
  segmento en curso. El `.mp4` de una grabación cortada se arma al próximo arranque.
- Si el disco o el encoder no dan abasto, se descartan cuadros (cuenta en `session.json`): nunca se frena el loop
  de VR.

## Sincronización con la telemetría y entre PCs

- `frames.csv`: **una fila por cuadro del video, en orden** (`video_frame` = número de cuadro del .mp4, desde 0)
  con la hora UTC de captura con microsegundos (`GetSystemTimePreciseAsFileTime`, la hora que corrige NTP),
  `unity_frame` y `unity_realtime_s`. Para poner la mirada sobre el video: para cada cuadro, buscar en
  `*_gaze.csv` la muestra con `timestamp_utc_iso` más cercana a `capture_utc_iso`.
- `session.json`: `requestedUtc` (llegó la orden), `startAtUtc` (hora programada, igual en los 3 PCs),
  `firstFrameUtc` (primer cuadro real), `lastFrameUtc`, `stopRequestedUtc`, cuadros escritos y descartados.
- `marks.csv`: marcas (`mark ...`) con hora UTC exacta.
- Ojo: el espejo del visor llega con ~0,1 s de retraso respecto de lo que se ve en el visor (scrcpy + decodificar).

## Audio (app + micrófono del visor)

Mientras graba video, graba también dos pistas de audio en WAV aparte (PCM 16 bits), y al detener las mete en el
`.mp4` como pistas AAC **«app»** y **«mic»**, alineadas al primer cuadro del video:

| Pista | Qué es | Cómo se captura |
|---|---|---|
| `app` (`audio_app.wav`) | Lo que oye el participante: la mezcla final de la app que sale al visor | `AppAudioTap` (OnAudioFilterRead) en el `AudioListener`, hilo de audio, copia sin modificar |
| `mic` (`audio_mic.wav`) | Micrófono del visor (VIVE Focus Vision por VIVE Streaming = un micrófono de Windows; Quest por Link = «Headset Microphone (Oculus Virtual Audio Device)») | `UnityEngine.Microphone`, leído en el hilo principal en bloques de 20 ms |

- **Elección del micrófono:** el primero cuyo nombre contenga `VIVE`, `Focus`, `Oculus`, `Meta` o `Headset` (en ese
  orden); si ninguno, el predeterminado de Windows. `-rec-mic TEXTO` elige otro (varios separados por coma).
- **Sincronía:** cada callback de audio (app) y cada lectura del micrófono anota «la muestra N pasó a la hora UTC T»
  (`PreciseClock`, la misma hora de `frames.csv`). Un ajuste lineal sobre toda la grabación (`AudioClockFit`) da la
  hora de la primera muestra (`startUtc`, sin el ruido de ±1 buffer de un solo callback) y la frecuencia real
  (`effectiveRate`: los relojes de audio derivan decenas de ppm, decenas de ms en 40 min). Una fila por segundo queda
  en `audio_app_sync.csv` / `audio_mic_sync.csv` (`audio_frame,utc_iso,unix_ms`) para alinear por fuera.
- **Armado:** después de unir los segmentos (proceso aparte, sigue aunque se cierre la app), ffmpeg copia el video
  y agrega cada WAV con `adelay` (si empezó después del primer cuadro) o `atrim` (si empezó antes), más `atempo` si
  la deriva medida es de 10 ppm a 0,2 % en grabaciones de 30 s o más. Escribe `*.mp4.av.partial` y **solo si termina
  bien** lo pone en lugar del `.mp4` solo video (`audio_mux.done`). Si falla, quedan el `.mp4` solo video, los WAV,
  `audio_mux.failed` y el error en `audio_mux.log`. Nunca se pierde el video.
- Si la app se corta, `session.json` ya tiene las horas del audio (se actualiza cada ~20 s) y el próximo arranque
  arma el `.mp4` con audio.
- **Por qué no ffmpeg/dshow para el micrófono:** sería un segundo input del mismo ffmpeg del video: si el micrófono
  desaparece (visor desconectado, VIVE Streaming reiniciado), ffmpeg se cae y se corta el video. Así el audio
  nunca toca el video.
- **Latencias que no se corrigen** (constantes, se pueden restar por fuera): la pista `app` tiene la hora en que
  Unity mezcla el buffer, ~2 buffers DSP (~40–90 ms) antes de que salga por Windows, más lo que tarde VIVE Streaming
  hasta el visor; la pista `mic` llega al PC con la demora del streaming del visor.
- Si la salida de audio de Windows cambia de frecuencia o de canales a mitad de la grabación (visor reconectado),
  la pista `app` se corta ahí y se avisa (`error` en `session.json` y en el status).
- `status` agrega `audioApp`, `audioMic` (dispositivo · Hz, `off` o `error: ...`) y `audioMux` (`""` sin audio,
  `pendiente`, `ok`, `error: ...`) para `lastVideo`. `session.json` agrega `audioApp` y `audioMic` (archivo,
  dispositivo, `startUtc`, `effectiveRate`, muestras, segundos, error).
- Barra de arriba: `REC ... · audio app+mic` (lo que falte, en naranjo: `sin mic`).

### Micrófono en vivo del visor (app nativa, 2026-10-27)
`HeadsetMicStream.cs`: en el visor, 127.0.0.1:47812. Con un oyente (el panel de sesión, botón de audífonos) prende el
micrófono y manda «PCM16 16000 1» y audio s16le mono 16 kHz en bloques de ~40 ms; sin oyente el micrófono queda
apagado. Si el PC se atrasa, se pierde audio viejo (≤ 0,5 s en cola) en vez de acumular latencia. RECORD_AUDIO lo da
`adb install -g` (Deploy-LabHeadsetApp.ps1). El panel guarda lo que escucha en `sessionsudio-live`.

## Dónde queda

`%USERPROFILE%\AppData\LocalLow\DefaultCompany\xr-collaboration-proto2\Recordings\` (junto a la telemetría):

```
20261001_150005_STIMULUS1_S01/
  segments/seg_00000.ts ...        respaldo, un archivo cada 60 s
  session.json                     PC, label, horas, tamaño, fps, encoder, closedCleanly, cuadros
  frames.csv                       hora UTC de cada cuadro del video
  marks.csv                        marcas (si hubo)
  audio_app.wav, audio_mic.wav     audio de la app y del micrófono (+ *_sync.csv: muestra ↔ UTC)
  audio_mux.log / .done / .failed  resultado de meter el audio en el .mp4
  20261001_150005_STIMULUS1_S01.mp4 video + pistas de audio «app» y «mic»
```

## Argumentos

`-norecord`, `-rec-auto`, `-rec-control-port N` (47811), `-rec-nocontrol`, `-rec-fps N` (30), `-rec-width N`
(1920), `-rec-segment S` (60), `-rec-encoder auto|nvenc|qsv|x264`, `-rec-dir RUTA`, `-rec-ffmpeg RUTA`,
`-rec-audio on|off` (on), `-rec-mic TEXTO|off` (automático: VIVE, Focus, Oculus, Meta, Headset).

Requisito: ffmpeg (el mismo del espejo del visor). Solo en el PC (Windows): el build para el visor no graba video.

## Código

| Archivo | Responsabilidad |
|---|---|
| `SessionRecorder.cs` | Estado (en espera / armada / grabando), órdenes, captura en la GPU, barra con el reloj, cierre |
| `RecordingRun.cs` | Una grabación: carpeta, cola, hilo, ffmpeg (con reinicio), frames.csv, session.json, .mp4 final |
| `RecordingControlServer.cs` | TCP en 127.0.0.1; las órdenes se ejecutan en el hilo principal |
| `RecordingControlProtocol.cs` | Órdenes y respuesta JSON — puras, con tests |
| `PreciseClock.cs` | Hora UTC con µs |
| `SessionRecorderSettings.cs` | Configuración y argumentos |
| `RecordingFfmpeg.cs` | Argumentos de ffmpeg (grabar, probar encoders, unir, meter audio) — puros, con tests |
| `RecordingLayout.cs` | Carpetas, nombres, filas de frames.csv / marks.csv, grabaciones pendientes de armar |
| `SessionAudioCapture.cs` | Audio de una grabación: AudioListener + micrófono (elección por nombre), inicio/fin en el hilo principal |
| `AppAudioTap.cs` | OnAudioFilterRead en el AudioListener (hilo de audio) |
| `AudioTrackRecorder.cs` | Una pista: WAV + reloj + *_sync.csv + su parte de session.json |
| `AudioWavWriter.cs` | WAV PCM 16 bits en su hilo (nunca bloquea al que escribe) |
| `AudioClockFit.cs` | Ajuste muestra ↔ hora UTC: inicio y frecuencia real — puro, con tests |
| `RecordingAudioMux.cs` | Arma el paso de ffmpeg que mete el audio (desde session.json), estado para el status |
| `Tests/Editor/` | Tests de EditMode |
