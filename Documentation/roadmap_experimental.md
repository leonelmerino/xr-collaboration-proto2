# Roadmap experimental — desacople de la medición respecto del desarrollo VR

**Proyecto:** ANID Exploración 13250116 — *Enhancing Collaboration in Extended Reality*
**Fecha:** 2026-09-14
**Semestre en curso:** 3 (de 8). Renovación de Semestre 2 aprobada hace ~2 meses.

---

## 1. Propósito y justificación

El desarrollo de la aplicación VR se ha convertido en la ruta crítica de todo el proyecto: no se pueden correr sesiones de registro hasta que la aplicación esté estable, y la aplicación tiene defectos abiertos que impiden la interacción en dos de los tres roles.

Este roadmap desacopla ambas cosas. Se abre una **vía de medición** que usa *passthrough* con una torre de Jenga física, y una **vía de desarrollo VR** que continúa en paralelo. Ambas convergen más adelante en un estudio de validación cruzada.

### La propuesta original ya autoriza esta estrategia

No se trata de un cambio de plan que haya que justificar ante ANID, sino de la aplicación de mecanismos que la propuesta ya contempla. Cuatro elementos textuales lo respaldan:

1. **El marco es XR, no VR.** El marco teórico define el alcance como *"XR includes Augmented Reality (AR), Virtual Reality (VR), and Mixed Reality (MR)... with mixed reality offering an in-between space where digital and physical elements coexist and interact dynamically"*. Passthrough con objetos físicos es MR, y está dentro de la definición del propio proyecto.

2. **La tarea comprometida no exige que el puzzle sea virtual.** La metodología de SO1 especifica *"solving puzzles of increasing complexity that require coordinated actions"*. Una torre de Jenga física satisface esa definición.

3. **El plan de mitigación del Hito 1 lo contempla explícitamente:** *"Implement contingency plans, such as remote testing or **simpler setups**, to address hardware delays"* y *"extend testing into Semester 2 while **focusing on refining individual components sequentially**"*.

4. **El desacople ya es una estrategia declarada en la metodología.** En SO3: *"We will prototype a self-adaptive system in a scalable manner by **decoupling** the multisensory integration layer from the navigation and wayfinding self-adaptive system."*

### El argumento central para el próximo informe

El indicador de cumplimiento del Hito 1 es **95% de confiabilidad del sistema de captura en tareas piloto** — del sistema de medición, no de la aplicación VR.

Actualmente los defectos de la aplicación confunden esa medición: si el agarre a distancia falla en Client y Helper, no es posible separar la falla del software de la falla del registro. **El passthrough no es un rodeo, es el camino más limpio para cerrar el indicador del Hito 1**, porque aísla el sistema de medición de los defectos de la aplicación.

---

## 2. Estado respecto de los hitos comprometidos

| Hito | Semestres | Estado |
|---|---|---|
| 1 — Framework de monitoreo psicofisiológico | 1–2 | **Incompleto.** El Informe de Avance 2025 reconoce que no se puede asegurar el 95% de confiabilidad y anuncia la extensión del pilotaje al año 2. |
| 2 — Sistemas de retroalimentación multisensorial | 3–4 | **No iniciado.** Vence al cierre del Semestre 4 (≈ junio 2027). |
| 3 — Prototipo autoadaptativo | 5–6 | Pendiente |
| 4 — Validación en escenarios reales | 7–8 | Pendiente |

**Ventana disponible:** el Semestre 3 corre hasta ~diciembre 2026 (quedan ~3,5 meses); el Semestre 4 de enero a junio 2027.

---

## 3. Fases

### Resumen

| Fase | Contenido | Duración | Ventana |
|---|---|---|---|
| **0** | Passthrough con los sensores que ya funcionan | 3–4 sem | Sep–Oct 2026 |
| **1** | Cierre de Bugs 1 y 2 → registro completo | 2–3 sem | Oct 2026 |
| **2** | Calibración espacial compartida y AOI | 4–5 sem | Nov–Dic 2026 |
| **3** | Feedback visual y manipulación emocional | 5–6 sem | Dic 2026–Feb 2027 |
| **4** | Estudio SO1 completo | 2–3 meses | Feb–Abr 2027 |
| **5** | Ampliación multisensorial → cierra Hito 2 | 2–3 meses | Abr–Jun 2027 |
| **B** | Desarrollo VR, en paralelo | continuo | — |
| **C** | Validación cruzada VR ↔ passthrough | 1–2 meses | Sem 5 |

---

### Fase 0 — Passthrough con los sensores que ya funcionan

**Sin resolver ningún bug.** Esta fase existe porque una parte sustancial del sistema de registro es independiente de los defectos conocidos.

#### Qué funciona hoy, sin tocar código

| Flujo | Estado | Por qué |
|---|---|---|
| ECG / HRV, EDA, EEG, respiración | ✅ Íntegro | Los registra el PC de adquisición con BioLab; no pasan por Unity |
| Cámara externa + estimación de pose 3D | ✅ Íntegro | Independiente del motor |
| Eye tracking — mirada cruda y pose de cabeza | ✅ Íntegro | `EyeTrackingSessionLogger` está en la escena y es configurable; el Bug 2 afecta solo al logger de cuerpo, y el Bug 1 solo a los trackers |
| Pose de cabeza y de manos en `_body.csv` | ✅ Íntegro | `head_*` viene de `Camera.main` y `hand_*` de `XRHandSubsystem`; ninguno depende de `IsCalibrated` |
| Eventos de tarea → BioLab, sincronía de reloj | ✅ Íntegro | `AcquisitionEventManager` y `NetworkClockSync` son independientes |
| Trackers de cintura y pies | ❌ No usable | Bug 1: la calibración espuria deja `is_calibrated=1` con offsets inválidos |
| Nombre de archivo por participante | ⚠️ Con workaround | Bug 2: renombrar la carpeta por nodo al copiar |

**Objetivo.** Validar la cadena completa de adquisición sincronizada durante una tarea colaborativa real, sin depender de la aplicación VR ni de ninguna corrección de código.

**Trabajo técnico.** Habilitar passthrough en la escena (build de configuración, sin torre virtual ni avatares visibles). Es el único desarrollo de la fase.

**Datos que se recolectan.** Fisiología completa de los tres participantes; mirada cruda (vectores y validez) y pose de cabeza; pose de manos; video externo con pose corporal; marcas de eventos de tarea; offsets de reloj entre nodos.

**Análisis.**
* Confiabilidad por flujo: porcentaje de muestras válidas sobre el total, por participante y por sesión. Es la medida directa del indicador del Hito 1.
* Alineamiento temporal: dispersión de los offsets de reloj entre los tres nodos a lo largo de la sesión.
* Calidad de señal fisiológica: detección de picos R, artefactos de movimiento en EDA, impedancia de electrodos.
* Cobertura de hand tracking: proporción de tiempo con `hand_*_valid = 1`, que en las pruebas previas fue problemática.

**Implicancia de los resultados.**
* Si la confiabilidad es alta en estos flujos, **el Hito 1 queda demostrado parcialmente**.
* Si es baja, se sabe con certeza que el problema **no** está en la aplicación VR, lo que redirige el esfuerzo al laboratorio o al hardware en vez de al software.
* La cobertura de hand tracking determina si la tarea física necesita rediseño: si las manos se pierden al manipular bloques reales, hay que ajustar la altura de la mesa o la iluminación antes de invertir en las fases siguientes.

---

### Fase 1 — Cierre de Bugs 1 y 2

**Objetivo.** Recuperar el registro postural y la identificación inequívoca de participante. Son los únicos dos defectos que están en la ruta crítica de **ambas** vías: hay que resolverlos para VR y para passthrough por igual.

**Trabajo técnico.** Bug 1: mover la calibración de trackers a una tecla libre (cambio de una línea). Bug 2: llevar el `[TrackerSystem]` a la escena para que sus campos de sesión sean configurables, o leerlos del `EyeTrackingSessionLogger`. Conviene incluir también el Bug 6 (validación de plausibilidad de la pose de calibración), que convierte una calibración inválida en un fallo visible.

**Datos adicionales.** Posición de cintura y ambos pies, calibrada y válida.

**Análisis.**
* Confiabilidad del tracking postural, cerrando el quinto flujo del indicador del Hito 1.
* **Sincronía postural entre participantes** — una variable explícitamente comprometida en la propuesta (*"postural synchronization within the team"*). Correlación cruzada de series de posición de cintura entre pares, con desfase.

**Implicancia.** Con los cinco flujos medidos y confiables, **el Hito 1 queda cumplido**. Es el entregable que desbloquea el reporte de cumplimiento atrasado.

---

### Fase 2 — Calibración espacial compartida y AOI

**Objetivo.** Dotar al sistema de referentes espaciales en passthrough, de modo que la mirada pueda atribuirse a objetos y personas reales.

**Trabajo técnico.**

1. **Registro de origen común.** Rutina de tres puntos con el control sobre marcas físicas de la mesa; cálculo de la transformación rígida; persistencia por estación.
2. **Proxy de mesa y torre.** Colliders alineados a la geometría física, con `AOITag` por zona y por nivel de altura.
3. **AOI social.** Collider invisible con `AOITag` en el hueso de la cabeza de cada avatar remoto.
4. **Verificación.** Una vista de diagnóstico que superponga los proxies sobre el passthrough para confirmar visualmente la alineación antes de cada sesión.

**Datos adicionales.** Columnas `hit_aoi` pobladas con categorías: `tower`, `tower_lvl_NN`, `table`, `partner_<rol>`, `elsewhere`.

**Análisis.**
* **Atención conjunta:** proporción de tiempo en que dos o más participantes miran la misma zona simultáneamente.
* **Mirada social:** tiempo dirigido a cada compañero; detección de *mutual gaze* (A mira a B mientras B mira a A).
* **Seguimiento de mirada:** latencia con que un participante dirige la mirada a la zona que otro acaba de mirar. Es un índice directo de coordinación atencional.
* **Alternancia entre tarea y persona:** frecuencia de transiciones torre ↔ compañero, que en la literatura se asocia a carga y a búsqueda de acuerdo.

**Implicancia.** Esta fase habilita el análisis de coordinación visual, que es el núcleo conductual de SO1. Sin ella solo hay vectores de mirada en crudo, difíciles de interpretar. Los resultados alimentan directamente la selección de marcadores conductuales para SO3: si el *mutual gaze* o la latencia de seguimiento resultan sensibles al estado del equipo, son candidatos a señal de entrada del sistema autoadaptativo.

**Riesgo.** La precisión del registro de tres puntos determina todo lo demás. Se recomienda medir el error de reproyección al calibrar y abortar la sesión si supera un umbral (propuesta inicial: 2 cm), en vez de descubrirlo en el análisis.

---

### Fase 3 — Feedback visual y manipulación emocional

**Objetivo.** Introducir la primera variable independiente del proyecto: retroalimentación visual valorada tras cada turno, usada para inducir estados emocionales y observar su efecto sobre la colaboración.

#### Decisión de diseño: el feedback debe ser predeterminado, no contingente

Para que la retroalimentación funcione como variable independiente hay que **controlar la dosis**. Si la marca depende del desempeño real, la proporción de feedback negativo la determina el participante y deja de ser manipulable.

La recomendación es un **esquema predeterminado** de valencia por turno, fijado por condición experimental (por ejemplo: 20% negativo en la condición de baja fricción, 60% en la de alta). Esto tiene dos ventajas adicionales:

* Elimina la necesidad de evaluar automáticamente si la jugada fue correcta.
* Iguala la exposición entre participantes, lo que hace comparables las respuestas fisiológicas.

Solo se necesita detectar **los límites de cada turno**, no su resultado. Dos opciones, compatibles entre sí: marcado manual por el operador, o detección automática de la mano entrando en el volumen de la torre, que queda disponible tras la Fase 2.

> **Consideración ética — a validar.** Entregar retroalimentación disociada del desempeño real introduce un componente de engaño experimental. **Hay que consultar al comité ético si esto requiere actualizar la evaluación ya aprobada**, y definir si corresponde incorporar un procedimiento de *debriefing* al cierre de cada sesión. Conviene hacer esa consulta al comienzo de la Fase 2: si la respuesta es que sí corresponde actualizar, el plazo de tramitación entra al calendario, y es preferible conocerlo con anticipación.

**Trabajo técnico.** Overlay de feedback en el visor del participante que acaba de jugar; secuenciador de turnos con esquema de valencia configurable; registro de eventos de turno y de feedback hacia BioLab con marca temporal precisa.

**Datos adicionales.** Eventos de inicio y fin de turno por participante; eventos de feedback con valencia y timestamp; cuestionarios NASA-TLX, SPQ, SVNTS y PANAS al cierre.

**Análisis.**
* **Respuesta evocada al feedback:** respuesta fásica de EDA y desaceleración cardíaca en la ventana de 0–6 s posterior a la marca, comparando valencia positiva y negativa.
* **Deriva de estado:** evolución de HRV (RMSSD, razón LF/HF) a lo largo de la sesión en función de la dosis acumulada de feedback negativo.
* **Efecto sobre la mirada social:** cambio en el tiempo dirigido a los compañeros y en el *mutual gaze* tras feedback negativo. Es la medida de fricción colaborativa.
* **Efecto sobre la conducta:** cambio en duración del turno, en la distancia de aproximación a la torre, y en la sincronía postural.
* **Convergencia subjetivo–objetivo:** correlación entre PANAS y los índices fisiológicos, que valida la manipulación.

**Implicancia.** Sirve a tres objetivos simultáneamente:

* **SO1** — demuestra que el sistema detecta estados emocionales inducidos.
* **SO2** — la retroalimentación visual valorada **es** una configuración de retroalimentación multisensorial.
* **SO3** — si la respuesta fisiológica al feedback negativo es detectable en tiempo real, se vuelve la señal de entrada natural para el sistema autoadaptativo: un sistema que reduce la frecuencia de feedback negativo cuando detecta fricción.

Si la manipulación **no** produce efecto medible, el hallazgo también es informativo: obliga a revisar la intensidad de la manipulación antes de invertir en el estudio completo de la Fase 4.

---

### Fase 4 — Estudio SO1 completo

**Objetivo.** Ejecutar el estudio comprometido de SO1 con potencia estadística suficiente.

**Diseño.** 72 participantes en 24 tríadas, equilibrados por género, edad 18–45. El análisis de potencia original establece un mínimo de 61 para un tamaño de efecto f=0,35 con potencia 0,8.

Manipulación de complejidad de tarea en tres niveles, conforme a *"puzzles of increasing complexity"*: Jenga libre, cronometrado, y con turnos restringidos o roles asimétricos.

**Datos.** Todos los flujos de las fases anteriores, con condición y orden contrabalanceados.

**Análisis.** Los comprometidos en la sección 3 de la propuesta:
* **Fusión multimodal:** random forests y redes neuronales sobre HRV, EDA, mirada y postura para predecir calidad de colaboración.
* **Modelos jerárquicos lineales:** para la estructura anidada de medidas repetidas dentro de tríadas, separando varianza intra e intergrupo.
* **Validación cruzada k-fold** para robustez de los modelos predictivos.

**Implicancia.** Responde RQ1 y constituye la sustancia de SO1. Es el insumo de la primera publicación del proyecto y la base empírica sobre la que se diseñan las adaptaciones de SO3.

---

### Fase 5 — Ampliación multisensorial

**Objetivo.** Completar las tres configuraciones de retroalimentación comprometidas en el Hito 2, dentro de su ventana.

| Configuración | Modalidad | Entregable sobre passthrough |
|---|---|---|
| 1 | Visual — marca de valencia por turno | Ya construida en la Fase 3 |
| 2 | Auditiva — señales de temporización y urgencia | Sí, sin dependencia de la app VR |
| 3 | Interoceptiva — compartir HRV o EDA entre participantes | Sí; requiere retorno de BioLab en tiempo real |

La tercera es la que la propuesta menciona explícitamente: *"EDA or Heart Rate data can signal emotional changes to other group members allowing the recalibration of task distribution among them."* Requiere cerrar el lazo BioLab → aplicación, que hoy solo corre en un sentido.

**Análisis.** Comparación entre configuraciones en tiempo de completitud, tasa de error, eficiencia comunicativa y satisfacción (≥80% es el indicador comprometido). Análisis multivariado y modelado temporal.

**Implicancia.** **Cierra el Hito 2** al final del Semestre 4. Si el calendario se aprieta, la propuesta contempla la mitigación: *"Reduce the number of feedback configurations under evaluation to ensure timely delivery"* — se priorizan las dos que muestren efecto más claro.

---

### Vía B — Desarrollo VR, en paralelo

Corre sin bloquear ninguna de las fases anteriores. El plan detallado —estrategia de testeo del modo multijugador e integración de los guantes hápticos— está en [`plan_via_b.md`](plan_via_b.md); lo que sigue es el resumen.

**B.1 — Defectos bloqueantes.** Bug 12 (salto de 20 cm al pinch): ejecutar una sesión con los logs de `[JengaGrab]` ya instrumentados y resolver la causa antes de intentar otra corrección. Bug 11 (rayo remoto invisible): marcar la casilla `Enabled` de `NetworkedAvatarHands` en el prefab.

**B.2 — Quality gate.** Criterio de entrada definido por adelantado, no negociable en el momento: las dos formas de interacción operativas en los tres roles, rayo remoto visible, y una sesión de 20 minutos con tres participantes sin incidentes.

**B.3 — Paridad de features.** Llevar a VR lo construido en las Fases 2 y 3: AOI por bloque (que en VR sí es viable, porque la geometría es conocida) y el sistema de feedback por turno.

---

### Fase C — Validación cruzada VR ↔ passthrough

**Objetivo.** Ejecutar la misma tarea con la misma instrumentación en ambas modalidades.

Cumple dos funciones. La primera es metodológica: demuestra que los datos recolectados en la vía passthrough son transferibles al entorno VR, lo que consolida retroactivamente todo lo hecho en las Fases 0 a 5.

La segunda es que **es un producto científico por derecho propio.** ¿Difiere la colaboración en MR-passthrough de la VR inmersiva, medida con psicofisiología sincronizada en tríadas? Ataca directamente el continuo AR/VR/MR que la propuesta plantea en su marco teórico, y hasta donde alcanza la literatura revisada en la propuesta, no ha sido medido así.

**Análisis.** Comparación entre modalidades de todos los índices establecidos en las fases previas, con modelos de equivalencia además de las pruebas de diferencia — porque la pregunta relevante es si los resultados son *comparables*, no solo si difieren.

**Implicancia.** Habilita el Hito 3 con dos entornos validados en vez de uno, lo que amplía las opciones de despliegue para el Hito 4 y, en particular, para el trabajo comprometido con la Corporación de Rehabilitación Club de Leones Cruz del Sur, donde un montaje de passthrough con objetos físicos puede ser más apropiado que VR inmersiva.

---

## 4. Dependencias críticas

| Fase | Depende de | Bloquea a |
|---|---|---|
| 0 | Passthrough habilitado; laboratorio operativo | — |
| 1 | — | Fase 2 (los datos de postura alimentan la sincronía) |
| 2 | Fase 1; marcas físicas en la mesa | Fase 3 (detección automática de turno) |
| 3 | Fase 2; **validación de si corresponde actualizar la evaluación ética** | Fase 4 |
| 4 | Fase 3; reclutamiento de 72 participantes | Fase 5 |
| 5 | Fase 4; lazo de retorno BioLab en tiempo real | Hito 2 |
| B | — | Fase C |
| C | Fase 5 y B.2 | Hito 3 |

---

## 5. Referencias

* [`architecture.md`](architecture.md) — mapa de subsistemas, flujos de datos y acoplamientos del sistema
* [`plan_via_b.md`](plan_via_b.md) — plan detallado de la Vía B: estrategia de testeo del modo multijugador e integración de guantes hápticos
* [`bug_dev.md`](bug_dev.md) — registro de defectos con causa raíz y prioridad
* [`test_protocol.md`](test_protocol.md) — protocolo de sesión de validación
* [`body_tracking_guide.md`](body_tracking_guide.md) — operación y formato del registro de movimiento
* [`eye_tracking_data_format.md`](eye_tracking_data_format.md) — especificación del CSV de mirada
* [`network_setup.md`](network_setup.md) — configuración de red del laboratorio
* [`dev_log.md`](dev_log.md) — historial técnico de desarrollo
