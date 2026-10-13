# Señales 3D en el visor (FeedbackCues)

El equipo de la sesión le muestra al participante un aviso en 3D sobre el passthrough: «¡PERFECTO!», «MAL COLOCADO»,
«DEMASIADO LENTO»… Se manda desde el panel de sesión (`tools\lab-dashboard`, teclas 1-9) por el canal de control de
la app (127.0.0.1:47811):

```
cue tono|ícono|TÍTULO|subtítulo|segundos      ej.  cue bad|cross|MAL COLOCADO|El bloque quedó torcido
cue clear
```
- **Tonos:** great (dorado) · good (verde) · bad (rojo) · slow (naranjo) · warn (amarillo) · info (azul).
- **Íconos:** auto (el del tono) · none · check · cross · clock · star · warning · bolt.
- **Segundos:** cuánto queda en pantalla (0,5–15; 2,4 por defecto).

Cada señal queda en el CSV de eventos (`FEEDBACK_CUE`) y como marca del video si se está grabando.

## Qué se ve
- **Ubicación:** fija en la sala, 1,4 m delante de donde mira la persona y 6° arriba de la vista, de frente a ella.
  No va pegada a la cabeza y no tapa la torre.
- **Título con volumen:** 12 capas de TextMeshPro con el shader propio `Shaders/CueText.shader` (atlas SDF de TMP,
  bisel e iluminación Standard), extruidas hacia atrás y un poco en diagonal. La cara tiene degradé.
  Los shaders de TMP no sirven en XR: suavizan el borde según el tamaño de la ventana del PC y no de la textura del
  ojo, y en el visor las letras se veían transparentes. `CueText` usa la derivada en pantalla (fwidth).
- **Oclusión:** las manos y los antebrazos reales tapan la señal (`Assets/Measurement/Occlusion`).
- **Ícono metálico biselado:** malla extruida hecha en código (`CueMeshes`) que refleja la sala.
- **Fondo:** sombra suave detrás (se lee sobre fondos claros) y brillo aditivo, que suma luz sin tapar la sala.
- **Animación:**
  - Entrada: letras en cascada, el ícono entra girando, onda expansiva, chispas, más confeti (great) o esquirlas (bad).
  - Luego un destello de luz cruza el título; el cuerpo flota y se balancea.
  - Extras por tono: sacudida (bad), agujas del reloj (slow), pulso (warn).
  - Salida: letras en cascada inversa.
- **Sonido:** sintetizado en código (`CueSynth`, sin archivos): whoosh de entrada más campanas, golpe grave,
  tic-tac o beep según el tono. Sale por el audio del visor y queda en la grabación.

## Luz de la sala (`PassthroughLighting`)
VIVE Streaming no da las cámaras del visor, pero el espejo del visor (scrcpy) trae la misma imagen.

1. **Muestreo:** cada 0,25 s se toman ~1.600 puntos del ojo izquierdo. Se pasan a direcciones del mundo con la
   orientación de la cabeza y se acumulan en un mapa de entorno de cubo (6×8×8 celdas), que aprende la sala a
   medida que la persona mira alrededor.
2. **Exposición:** el percentil 85 de luminancia, es decir las superficies claras como una pared o la mesa. No se
   usa el promedio, que un monitor negro arrastra.
3. **Luces que se arman:**
   - Ambiente con armónicos esféricos, como una sonda de luz.
   - Luz principal: zonas brillantes de la mitad de arriba (tubos del techo), con su color.
   - Relleno suave desde quien mira; la luz real suele venir de adelante-arriba y dejaría el texto a contraluz.
   - Cubemap de reflejos para el metal.

No se mide con una señal en pantalla, porque el espejo la incluye. Solo en modo medición; en VR se usa la luz de la
escena. Las luces de las señales solo iluminan su capa (29).

## Archivos
| Archivo | Qué hace |
|---|---|
| `FeedbackCueDirector.cs` | Recibe `cue`, ubica la señal, mide la luz, máscara de cámara. |
| `FeedbackCueVisual.cs` | Arma y anima una señal. |
| `FeedbackCueSpec.cs` | Formato de la orden y colores de cada tono. |
| `CueMeshes.cs` | Íconos extruidos. |
| `CueSynth.cs` | Sonidos. |
| `PassthroughLighting.cs` | Luz de la sala. |
| `CueAssets.cs` | Carga los materiales de `Resources/FeedbackCues` y genera las texturas de brillo. |
| `AppHealth.cs` | Salud en vivo para el panel (`"app"` en cada respuesta) y el JPG de `snapshot`. |
| `Shaders/CueFx.shader` | Brillos aditivos y partículas. |

- **Materiales:** los crea `Assets/Editor/FeedbackCueAssetsBuilder.cs`; MeasurementBuild lo llama antes de cada
  build, así el build incluye los shaders.
- **Vista previa sin visor:** `Assets/Editor/FeedbackCuePreview.cs`. Renderiza cada señal sobre una foto del espejo,
  compuesta como en el visor, y escribe PNGs:
  ```
  Unity.exe -batchmode -quit -projectPath . -executeMethod FeedbackCuePreview.Render -cue-bg sala.png -cue-frame espejo.png -cue-out carpeta
  ```
