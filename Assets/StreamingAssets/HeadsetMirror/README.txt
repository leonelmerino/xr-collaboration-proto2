scrcpy-server 4.1 (Genymobile/scrcpy, Apache-2.0: ver scrcpy-LICENSE.txt), sin modificar.
Lo usa el espejo del visor (Assets/Measurement/HeadsetMirror). Se sube al visor por adb en cada sesión.
Para actualizarlo: reemplazar el archivo por el scrcpy-server de la nueva versión y cambiar
ScrcpyProtocol.ServerVersion para que coincida (el servidor rechaza clientes de otra versión).
