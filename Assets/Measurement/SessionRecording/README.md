# Grabación de la ventana del PC (SessionRecorder)

Respaldo en video de cada sesión: graba la ventana del PC **tal como se ve**. Incluye el espejo del visor, los HUD,
los avisos en blanco y una barra arriba con **la hora UTC y local con milisegundos** y el número de cuadro de
Unity. Graba también con el visor sacado o desconectado: la ventana se graba igual.

**La telemetría no depende de esto.** Mirada, cabeza, manos, eventos, auditoría de red y sincronía de reloj se
registran siempre mientras la app está abierta, se grabe video o no. El video es solo un respaldo.

## Control desde afuera (iniciar / detener)

La app abre **en espera** (barra gris `REC en espera`) y no tiene lógica de laboratorio, red ni SSH. Escucha
órdenes de una línea **solo en `127.0.0.1:47811`** (`RecordingControlServer`) y responde una línea JSON:

```
status
start [label=S01] [at=2026-10-01T18:00:05.000Z]
stop  [at=2026-10-01T18:40:00.000Z]
mark  texto libre
```

- `at` es una hora **UTC** absoluta: varios PCs con el reloj sincronizado (NTP) empiezan en el mismo instante
  aunque la orden les llegue con distinta demora. Sin `at`, corre apenas llega. Mientras espera la hora, la barra
  dice `REC empieza en 2.3 s` en amarillo.
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

## Dónde queda

`%USERPROFILE%\AppData\LocalLow\DefaultCompany\xr-collaboration-proto2\Recordings\` (junto a la telemetría):

```
20261001_150005_STIMULUS1_S01/
  segments/seg_00000.ts ...        respaldo, un archivo cada 60 s
  session.json                     PC, label, horas, tamaño, fps, encoder, closedCleanly, cuadros
  frames.csv                       hora UTC de cada cuadro del video
  marks.csv                        marcas (si hubo)
  20261001_150005_STIMULUS1_S01.mp4
```

## Argumentos

`-norecord`, `-rec-auto`, `-rec-control-port N` (47811), `-rec-nocontrol`, `-rec-fps N` (30), `-rec-width N`
(1920), `-rec-segment S` (60), `-rec-encoder auto|nvenc|qsv|x264`, `-rec-dir RUTA`, `-rec-ffmpeg RUTA`.

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
| `RecordingFfmpeg.cs` | Argumentos de ffmpeg (grabar, probar encoders, unir) — puros, con tests |
| `RecordingLayout.cs` | Carpetas, nombres, filas de frames.csv / marks.csv, grabaciones pendientes de armar |
| `Tests/Editor/` | Tests de EditMode |
