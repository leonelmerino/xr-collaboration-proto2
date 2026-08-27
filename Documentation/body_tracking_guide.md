# Registro de movimiento corporal con VIVE Ultimate Trackers

Guía operativa — versión Play Mode desde Unity

**Proyecto:** XR Collaboration Prototype 2 · ANID Proyectos de Exploración 13250116

---

## 0. Qué hace el sistema

Cada laptop registra, cuadro a cuadro y en un archivo local propio, la posición de:

| Fuente | Qué se registra |
| --- | --- |
| 3 VIVE Ultimate Trackers | Cintura, pie izquierdo, pie derecho (posición XYZ) |
| Hand tracking del visor | Ambas manos, palma (posición XYZ + rotación) |
| Visor (HMD) | Cabeza (posición XYZ + rotación) |

Se genera un CSV `_body.csv` con **44 columnas**, en la misma carpeta y con la misma convención de nombres que el archivo de mirada (`_gaze.csv`). Ambos se sincronizan por la columna `timestamp_utc_iso`.

---

## 1. Los trackers

### Colocación

* Tracker 1 → cintura (centrado en la espalda baja, sobre el cinturón)
* Tracker 2 → pie izquierdo (empeine o tobillo exterior)
* Tracker 3 → pie derecho

No hace falta marcarlos ni asignarles un rol fijo: el sistema **identifica solo** cuál es cuál al calibrar (el más alto = cintura; de los dos restantes, el que quede a la derecha del visor = pie derecho).

### El mapa del espacio

Los Ultimate Tracker son **inside-out**: tienen cámaras propias y construyen un mapa visual de la sala para ubicarse. Consecuencias prácticas:

* El mapa se crea una vez al configurarlos, desde VIVE Hub → sección de trackers → configuración del espacio.
* Si la sala cambia, el mapa se degrada: mover mesas o sillas, cambiar la iluminación, abrir o cerrar cortinas, o correr las sesiones de día y de noche.
* Síntoma típico de mapa desactualizado: un tracker «se queda pegado» en una posición o salta bruscamente.
* Si eso pasa: rehacer el mapa del espacio desde VIVE Hub antes de seguir.
* **Regla operativa: una vez configurado el mapa, no mover el mobiliario del laboratorio.**

### Encendido y luces

Mantener presionado el botón de encendido hasta que se encienda el LED. Los tres deben estar encendidos y con tracking activo antes de entrar en Play.

| LED | Significado |
| --- | --- |
| Verde fijo | Tracking correcto — es el estado que se necesita para grabar |
| Verde parpadeante | Conectando / tracking inestable — esperar o revisar |
| Azul parpadeante | Modo emparejamiento (no debería aparecer en uso normal) |
| Rojo | Batería baja o error — cargar antes de la sesión |

---

## 2. Calibración dentro de la aplicación

La calibración se hace **cada sesión, con el usuario ya equipado**. Son dos teclas, en el foco de la ventana de Game de Unity.

### Paso a paso

1. Entrar en Play en Unity.
2. Presionar H (Host) o C (Client) según el rol de esa máquina, como siempre.
3. El usuario se para derecho, mirando al frente, con los tres trackers puestos.
4. Presionar C → calibrar.
5. Verificar visualmente en la escena las tres esferas de diagnóstico:

| Color de esfera | Corresponde a |
| --- | --- |
| Naranja | Cintura |
| Cian | Pie izquierdo |
| Magenta | Pie derecho |

Deben coincidir con la posición física real. Si están cruzadas o desplazadas, seguir al refinamiento.

### Refinamiento (tecla R)

La calibración con C deja típicamente un error residual de unos 15 cm por tracker. Para corregirlo:

1. El usuario toma un tracker en la mano (sin sacárselo del cuerpo si es incómodo — basta con acercar la mano al tracker).
2. Presionar R.
3. El sistema detecta cuál tracker está más cerca de la mano y corrige su offset. La Console muestra el error anterior en centímetros.
4. Repetir con los otros dos trackers.

Se puede presionar R varias veces sobre el mismo tracker para más precisión. El radio de detección es de 30 cm — si la mano está más lejos, no refina y avisa en el log.

---

## 3. Dónde quedan los datos

### Ruta

```
C:\Users\<usuario>\AppData\LocalLow\DefaultCompany\xr-collaboration-proto2\EyeTrackingLogs\<participant_id>\<session_id>\
```

En Unity es `Application.persistentDataPath` — la misma ruta en Play Mode que en build.

### Nombre del archivo

```
<task_id>_<trial_id>_<índice>_body.csv
```

Ejemplo: `task_01_trial_01_001_body.csv`

El índice se incrementa automáticamente en cada corrida. **Los archivos nunca se sobrescriben.** El archivo de mirada de la misma corrida es el `..._001_gaze.csv` en esa misma carpeta.

### Estructura del CSV (44 columnas)

| Bloque | Columnas |
| --- | --- |
| Identificación | `sample_index`, `timestamp_rel_s`, `timestamp_utc_iso` |
| Metadata | `participant_id`, `session_id`, `task_id`, `trial_id`, `condition` |
| Cabeza | `head_x/y/z`, `head_qx/qy/qz/qw` |
| Calibración | `is_calibrated` |
| Cintura | `waist_valid`, `waist_x/y/z` |
| Pie izquierdo | `foot_l_valid`, `foot_l_x/y/z` |
| Pie derecho | `foot_r_valid`, `foot_r_x/y/z` |
| Mano izquierda | `hand_l_valid`, `hand_l_x/y/z`, `hand_l_qx/qy/qz/qw` |
| Mano derecha | `hand_r_valid`, `hand_r_x/y/z`, `hand_r_qx/qy/qz/qw` |

**Convenciones (idénticas al archivo de mirada):**

* Posiciones en metros, espacio mundo de Unity.
* Rotaciones en cuaterniones.
* Los campos numéricos quedan vacíos (no en cero) cuando el flag `*_valid` correspondiente es 0.
* Verificar siempre el flag antes de usar el valor.

---

## 4. Advertencias para la recolección

### El nombre del participante es igual en las tres máquinas

Por defecto los tres laptops escriben en `P001/S001/` con los mismos nombres de archivo. Como cada laptop tiene su propia carpeta local no se pisan en la máquina, pero al juntar los datos de los tres nodos **los nombres colisionan**.

> **ACCIÓN** — Al copiar los archivos de cada laptop, renombrar la carpeta agregando el identificador del nodo (por ejemplo `P001_host/`, `P001_client/`, `P001_helper/`), o mantenerlos en carpetas separadas por máquina.

### Si no se presiona C, no se graba ningún tracker

El registro arranca solo al entrar en Play, antes de la calibración. Si nadie presiona C, el archivo se genera igual pero con `is_calibrated = 0` y todas las columnas de trackers vacías.

> **CONTROL** — Primer control de calidad al recibir un archivo: verificar que `is_calibrated = 1`.

### Descartar los primeros segundos

El visor reporta una posición por defecto `(0, 0, −0.7)` hasta que el subsistema XR lo inicializa — típicamente los primeros 60 cuadros (unos 2 segundos). No hay columna `head_valid` para distinguirlo.

> **FILTRO** — Descartar filas con `head_y < 0.3`. El mismo criterio detecta las caídas de tracking del visor durante la sesión.

### Las manos se actualizan más lento que los cuadros

El hand tracking corre a 30–60 Hz y Unity a unos 90 Hz, así que hay cuadros consecutivos con valores de mano idénticos.

> **FILTRO** — Antes de calcular velocidades o aceleraciones, filtrar las muestras repetidas (`diff() != 0` sobre las columnas de posición).

Además, una mano fuera del campo visual del visor simplemente no se registra (`hand_*_valid = 0`). Conviene indicarle al participante que mantenga las manos visibles al inicio.

---

## 5. Checklist de sesión

### Antes

- [ ] Los 3 trackers cargados y en verde fijo
- [ ] El mobiliario del laboratorio en la misma posición que cuando se creó el mapa
- [ ] Dongle en puerto USB 2.0
- [ ] SteamVR + VIVE Hub corriendo en los 3 laptops

### Durante

- [ ] Play → H / C según rol
- [ ] Usuario parado derecho → presionar C
- [ ] Console muestra `[Calibration] ✓`
- [ ] Las 3 esferas (naranja / cian / magenta) coinciden con el cuerpo
- [ ] Refinar con R tracker por tracker
- [ ] Participante muestra ambas manos al visor antes de empezar la tarea

### Después

- [ ] Copiar la carpeta `EyeTrackingLogs\` de cada uno de los 3 laptops
- [ ] Renombrar por nodo para evitar colisión
- [ ] Verificar `is_calibrated = 1` en cada `_body.csv`
