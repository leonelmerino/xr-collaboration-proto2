# Oclusión (lo real tapa lo virtual)

Formas en la capa 28 que borran lo virtual donde está la mano real, así en ese lugar se ve el passthrough: una señal
3D detrás de la mano queda tapada por la mano.

## Qué hay hoy (2026-10-13)
- **`Occluders`:** se crea solo al cargar la escena. Dos modos:
  - **suave** (por defecto, `Shaders/HandCut.shader`): se dibuja al final (Transparent+200) y multiplica color y alpha
    por (1 − a), con a = 1 dentro de la mano y bajando a 0 en el borde de la silueta (N·V). El borde queda difuminado
    y la malla se infla unos mm por sus normales. Es la técnica de «alpha cut» que documenta Meta para passthrough sin
    profundidad, con bordes suaves para que el desalce no se vea como un corte duro.
  - **duro** (`Shaders/DepthOnly.shader`): solo profundidad, antes de lo virtual (lo de antes).
- **`HandOccluderSource`:**
  - **Mano:** la malla con piel del ejemplo «Hand Visualizer» de XR Hands 1.4.3 (`Resources/Occlusion/Hands`). Cada
    hueso va a la pose de su articulación (las 26 de XR_EXT_hand_tracking), así la silueta tiene la forma de la mano.
    Sin los modelos, vuelve a cápsulas por hueso.
  - **Antebrazo:** dos cápsulas de la muñeca al codo estimado (hombro desde la cabeza + brazo de dos huesos).
  - **Tiempo:** las poses se toman en el callback `updatedHands` de XR Hands en BeforeRender (lo más reciente posible,
    como `XRHandSkeletonDriver`) y se guarda ~0,5 s de historia. `offset` corre la pose: + predice con la velocidad
    de cada articulación (la mano del PC suele llegar más vieja que el passthrough, que se compone en el visor),
    − usa poses de antes. Además se infla un poco más mientras la mano se mueve rápido.
- **Ajuste en vivo** (canal de control, 127.0.0.1:47811):
  `occlusion show|hide` (ver las formas en color en un snapshot), `soft|hard`, `offset=MS`, `inflate=MM`,
  `feather=0..1`, `on|off`. Al arrancar: `-occlusion-offset-ms N`, `-occlusion-inflate-mm N`, `-occlusion-hard`.
  El estado va en `"occlusionInfo"` de cada respuesta.
- **Cómo calibrar el desfase:** con `occlusion show`, mover la mano de lado a lado a velocidad constante y tomar
  snapshots. Si la forma de color va detrás de la mano real, subir `offset`; si va adelante, bajarlo.

## Agregar algo que tape (mesa, torre, otra persona…)
```csharp
public sealed class TableOccluder : IOccluderSource
{
    public void DrawOccluders(Occluders occ, Camera cam) =>
        occ.DrawBox(tableCenterWorld, tableRotationWorld, new Vector3(1.2f, 0.75f, 0.8f));
}
// al arrancar:  Occluders.Register(new TableOccluder());
```
- **Primitivas:** `DrawCapsule(a, b, radio)`, `DrawBox(centro, rotación, tamaño)`, `DrawMesh(malla, matriz)`, todas
  en coordenadas del mundo.
- **Espacio de tracking:** para pasar a coordenadas del mundo, usar `cam.transform.parent.TransformPoint(...)`.
- **Contenido nuevo:** para que una forma lo tape, alcanza con que use ZTest normal (LEqual), como hacen todos los
  shaders de Unity y los de las señales.

## Por qué no hay profundidad de la sala (como el Depth API del Quest 3)
En PC, VIVE Streaming no entrega profundidad ni malla de la sala. Las extensiones OpenXR que ofrece el runtime (ver
`Player.log`, «Available Runtime Extensions») tienen manos, ojos, cara, cuerpo, trackers y passthrough. No tienen
`XR_META_environment_depth`, ni scene understanding, ni planos. Opciones, de menor a mayor esfuerzo:
1. **Objetos calibrados:** mesa, torre, paredes. Se marcan con la punta del dedo (hand tracking) y se guardan como
   cajas en el espacio de tracking: una `IOccluderSource` como la de arriba.
2. **Profundidad estéreo desde el espejo:** el espejo del visor trae la imagen de los dos ojos. Con matching estéreo
   en GPU se podría estimar la profundidad. Llega ~0,1–0,2 s tarde (scrcpy), así que al mover la cabeza la
   oclusión quedaría corrida.
3. **Build del visor (Android):** VIVE tiene Scene Perception, con planos y malla de la sala. En el Quest 3 standalone
   existe el Depth API.
