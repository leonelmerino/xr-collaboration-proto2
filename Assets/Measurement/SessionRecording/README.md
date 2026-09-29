# Grabación de la ventana del PC (SessionRecorder)

Respaldo en video de cada sesión: graba la ventana del PC **tal como se ve**. Incluye el espejo del visor, los HUD,
los avisos en blanco y una barra arriba con **la hora UTC y local con milisegundos** y el número de cuadro de
Unity, para sincronizar con la telemetría (`timestamp_utc_iso` de los CSV). Graba también con el visor sacado o
desconectado: la ventana se graba igual.

## Flujo

```
fin de cada cuadro (30 fps)                       hilo del encoder                    disco
ScreenCapture → RenderTexture → escalado (GPU)     cuadros RGBA → ffmpeg (stdin)       segments/seg_00000.ts (60 s)
  → AsyncGPUReadback (no bloquea)  ─cola (8)─►        H.264 NVENC / QuickSync / x264     segments/seg_00001.ts ...
```

- **Encoder:** prueba en orden `h264_nvenc` (GPU NVIDIA), `h264_qsv` (GPU Intel) y `libx264` (CPU), y usa el
  primero que funcione en ese PC. El 2026-09-29, STIMULUS1 y STIMULUS3 usaban NVENC; STIMULUS2 tiene un driver
  NVIDIA más viejo que el que pide este ffmpeg (≥ 610) y cae a QuickSync.
- **Segmentos MPEG-TS de 60 s:** si el visor se desconecta, la app se cierra de golpe o se cuelga, se pierde como
  mucho el final del segmento en curso. Los segmentos se conservan siempre.
- **Video final:** al cerrar la app normal (X, Alt+F4, `Start-LabSession -Stop`) se unen los segmentos en un
  `.mp4` sin recodificar (segundos), en un proceso aparte que sigue aunque la app ya se cerró. Si la app murió de
  golpe, el `.mp4` se arma **al próximo arranque**.
- Cada cuadro se marca con la hora real de llegada: si la app baja de fps (visor en la frente, ~20 fps), el video
  conserva el tiempo real.
- Si el disco o el encoder no dan abasto, se descartan cuadros (cuenta en `session.json`): nunca se frena el loop
  de VR.

## Dónde queda

`%USERPROFILE%\AppData\LocalLow\DefaultCompany\xr-collaboration-proto2\Recordings\` (junto a la telemetría):

```
20261001_102345_STIMULUS1/
  segments/seg_00000.ts ...        respaldo, un archivo cada 60 s
  session.json                     PC, inicio UTC y local, tamaño, fps, encoder, closedCleanly, cuadros
  20261001_102345_STIMULUS1.mp4    video completo
```

~1920x1200 a 30 fps, calidad constante: una ventana quieta pesa poco y una en movimiento, algunos Mbit/s.

## Uso

Se enciende solo en modo medición. Argumentos: `-norecord`, `-rec-fps N` (30), `-rec-width N` (1920),
`-rec-segment S` (60), `-rec-encoder auto|nvenc|qsv|x264`, `-rec-dir RUTA`, `-rec-ffmpeg RUTA`.
En pantalla, arriba al centro: `REC 00:12:34 · 360 MB · h264_nvenc | PC · hora UTC · hora local · cuadro`. Si
algo falla dice `REC detenido: <motivo>` en naranja, y la telemetría sigue igual.

Requisito: ffmpeg (el mismo del espejo del visor).

## Código

| Archivo | Responsabilidad |
|---|---|
| `SessionRecorder.cs` | Captura, hilo del encoder, reinicio de ffmpeg, reloj en pantalla, cierre y video final |
| `SessionRecorderSettings.cs` | Configuración y argumentos |
| `RecordingFfmpeg.cs` | Argumentos de ffmpeg (grabar, probar encoders, unir) — puros, con tests |
| `RecordingLayout.cs` | Carpetas, nombres, sesiones pendientes de armar |
| `Tests/Editor/` | Tests de EditMode |
