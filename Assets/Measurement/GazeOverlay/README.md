# Mirada en vivo en la ventana del PC (GazeOverlay)

Mientras el eye tracking entrega mirada combinada válida, la ventana del PC muestra un **anillo verde** donde mira
el participante, con una **estela corta** (0,5 s) que se desvanece. Arriba a la izquierda, una línea chica:
`Mirada 87% válida (5 s) · mano I sí D no · [G ocultar · Shift+G visor off]`. Como se dibuja en la ventana, **queda
en el video** de SessionRecorder sin hacer nada más.

**En el visor está apagado por defecto** (verlo cambiaría hacia dónde mira el participante).

## Teclas y argumentos

| | Efecto |
|---|---|
| **G** | Muestra / oculta todo lo de la mirada en el PC (anillo, estela y línea de estado). |
| **Shift+G** | Enciende / apaga el punto en el **visor** (magenta). Solo para pruebas; queda en `Player.log` con hora UTC. |
| `-gaze-overlay on\|off` | Estado inicial en el PC (on). |
| `-gaze-hmd on\|off` | Estado inicial en el visor (off). |
| `-gaze-distance M` | Distancia del punto si el rayo no choca con nada (1.5 m). |
| `-gaze-overlay-delay-ms N` | Retrasa el anillo para que coincida con el espejo, que llega ~100–200 ms tarde (0). |
| `-gaze-mirror-scale S`, `-gaze-mirror-offset X,Y` | Ajuste fino del anillo sobre la imagen del espejo (1 y 0,0; en fracción de la imagen de un ojo). |

## Cómo funciona (y por qué así)

**De dónde sale la ventana del PC.** En modo medición la ventana **no** es el espejo XR de Unity: ese queda negro,
porque el passthrough se compone dentro del visor. Lo que se ve es `HeadsetMirror`: la pantalla del visor por
scrcpy (ambos ojos lado a lado o uno, F6) en un Canvas Screen Space - Overlay. Como esa imagen **es** la pantalla
del visor, cualquier cosa que renderice la cámara XR (aunque sea en una capa aparte) aparece en el espejo solo si
también aparece en el visor. Por eso no sirve una capa «solo escritorio» culleada de los ojos: el anillo se dibuja en
**2D con OnGUI** (encima del Canvas del espejo, nunca en el visor) en el lugar que corresponde de la imagen:

1. Rayo de mirada combinado de `ViveEyeTrackingProvider` (OpenXR `XR_HTC_eye_tracker`), pasado del espacio de
   tracking al mundo con el padre de la cámara (igual que las manos en `BodyTrackingSessionLogger`).
2. Raycast contra los colliders de la escena (desde 10 cm delante de los ojos, hasta 10 m). Si no choca, el punto
   queda a 1,5 m. En modo medición casi nunca choca: el entorno virtual está apagado y el passthrough no tiene
   geometría. Para el anillo 2D la distancia apenas importa (se proyecta por el mismo rayo; solo cambia el
   paralaje entre ojos).
3. El punto se proyecta con la matriz de vista y proyección **de cada ojo** de la cámara XR
   (`GetStereoViewMatrix` / `GetStereoProjectionMatrix`) y se dibuja en la mitad izquierda / derecha de la imagen
   del espejo (`HeadsetMirror.TryGetImageScreenRect`). Con la vista de un ojo, solo el izquierdo.
4. Sin espejo (modo VR `-vr`, `-nomirror` o Quest por Link sin espejo) la ventana es el espejo XR de Unity: se
   proyecta con el ojo izquierdo sobre toda la ventana (aproximado).

**Punto en el visor (Shift+G):** un Canvas World Space chico (0,8° de tamaño) en la capa **30**, en el punto de
mirada. La capa se agrega a la máscara de la cámara principal solo mientras está encendido (MeasurementMode la
deja en 0) y se saca al apagarlo. Como el espejo es la pantalla del visor, el punto magenta también se ve en el
espejo: **sirve para verificar** que el anillo verde cae en el mismo lugar.

**Mirada de los otros participantes:** no se muestra. La mirada no viaja por la red (cada PC solo la registra en
su `*_gaze.csv`); mostrarla requeriría sincronizarla por Netcode.

## Verificar (después de compilar)

1. Abrir el build en modo medición con el visor puesto y el espejo funcionando. Arriba a la izquierda debe aparecer
   la línea `Mirada …% válida`; con el visor bien puesto, ~60–90 %. Con el visor en la frente, ~0 % y sin anillo.
2. Mirar un objeto físico con referencia clara (una esquina de la mesa, el Jenga): el anillo verde debe caer sobre
   él en las dos mitades del espejo.
3. **Shift+G**: aparece el punto magenta en el visor y, ~0,1–0,2 s después, en el espejo. El anillo verde debe
   quedar encima del punto magenta (al mover los ojos, el verde llega antes: esa diferencia es la demora del
   espejo; se puede compensar con `-gaze-overlay-delay-ms`). Si queda corrido o escalado de forma constante,
   ajustar `-gaze-mirror-offset` / `-gaze-mirror-scale`. **Shift+G de nuevo para apagarlo antes de la sesión.**
4. **G** oculta y vuelve a mostrar todo.
5. Grabar 10 s (`Send-RecordingCommand.ps1 start`/`stop`): el `.mp4` muestra el anillo y la línea de estado.

## Código

`GazeOverlay.cs` (Assembly-CSharp; se crea solo al cargar la escena, solo en Windows). Usa
`ViveEyeTrackingProvider`, `XRHandSubsystem` (manos) y `HeadsetMirror` (dónde está la imagen). No escribe nada:
la telemetría sigue saliendo de los loggers de siempre.
