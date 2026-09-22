# Protocolo de prueba — sesión de validación multi-usuario

**Proyecto:** XR Collaboration Prototype 2 · ANID Proyectos de Exploración 13250116
**Fecha de redacción:** 2026-08-27
**Branch a ejecutar:** `main`, después de integrar `fixes-ui-new @ dc9c506`
**Actualización de la base de trabajo:** 2026-09-22
**Duración estimada:** 90 minutos (45 de preparación, 45 de pruebas)

---

## 0. Propósito

Validar el funcionamiento del sistema completo con tres participantes simultáneos y registrar de forma estructurada los defectos que aparezcan. No es una sesión experimental: no se recolectan datos de investigación, se recolectan reportes de fallas.

Varios de los problemas que van a observar **ya son conocidos y están documentados** en [`bug_dev.md`](bug_dev.md). Se listan explícitamente en cada prueba para que no pierdan tiempo reportándolos de nuevo, y para que puedan distinguir lo esperado de lo nuevo.

### Advertencia a los participantes

En esta versión, dos defectos conocidos degradan la interacción:

* **El grab a distancia (pinch + rayo) es inutilizable en Client y Helper.** El bloque salta unos 20 cm al tomarlo. El poke con el dedo sí funciona en los tres roles.
* **Nadie ve el rayo de los otros.** No hay referente compartido de atención, así que no se puede señalar un bloque apuntándolo: hay que describirlo verbalmente.

La tarea colaborativa se puede completar, pero coordinarse va a costar más de lo que costará cuando esos dos bugs estén cerrados. Conviene decirlo antes de empezar: baja la frustración y evita que se reporte como falla nueva.

---

## 1. Asignación de estaciones

Los roles están fijos por máquina. No intercambiar, porque la configuración de permisos de red y firewall ya está hecha por equipo:

| Equipo | Rol | Tecla de inicio |
| --- | --- | --- |
| Stimulus 1 | **Host** | `H` |
| Stimulus 2 | Client | `C` |
| Stimulus 3 | Helper | `C` |

El Host debe arrancar primero. Los otros dos no encuentran nada por LAN discovery hasta que el Host esté anunciando.

### Verificación de versión

La referencia histórica de esta sesión era `fixes-ui-new`. Después de su integración, usar `main` como base común. Antes de cambiar de rama, comprobar `git status --short` y conservar cualquier trabajo local; no forzar el cambio ni descartar archivos. Con el árbol limpio, sincronizar y verificar en cada equipo:

```bash
git fetch origin
git switch main
git pull --ff-only origin main
git merge-base --is-ancestor dc9c506 HEAD
git branch --show-current
git log -1 --oneline
```

La comprobación de ascendencia debe terminar con código 0 y los tres equipos deben coincidir en commit. Si alguno difiere, detener la preparación y sincronizar antes de continuar: varios de los defectos conocidos tienen correcciones parciales en commits distintos, y un reporte sin la versión exacta no es accionable. Crear las nuevas ramas de registro e infraestructura desde este `main` actualizado. La integración conserva los bugs conocidos descritos abajo; no implica que hayan sido corregidos.

---

## 2. Verificación de conectividad

### 2.1 Internet (WiFi)

Necesario para `git pull` y para la autenticación de Unity si la solicita al abrir el proyecto.

Conectar cada equipo a la red **UC invitados** con su cuenta:

| Equipo | Cuenta |
| --- | --- |
| Stimulus 1 | `stimulusuno` |
| Stimulus 2 | `stimulusdos` |
| Stimulus 3 | `stimulustres` |

Verificar con la carga de cualquier página. Si Unity pide login y no hay internet, el proyecto no abre.

### 2.2 Intranet (cable / router local)

Los tres equipos se conectan por cable al router local. El tráfico del experimento va por ahí, **no por WiFi**.

En cada equipo, anotar la IP asignada:

```bash
ipconfig
```

Registrar las tres en la planilla de sesión. Desde cada equipo, hacer ping a los otros dos:

```bash
ping <ip-de-cada-uno-de-los-otros-dos-equipos>
```

Los tres pings deben responder. Si alguno falla, revisar el cable y las reglas de firewall antes de seguir.

> **Problema conocido — Bug 13.** El router tiene asignación de IP fija por dirección MAC, pero a veces los equipos reciben una IP distinta de la reservada. Por eso hay que verificar la IP en cada sesión en vez de asumirla.
>
> Si la IP no coincide con la reservada, **anotar estos datos**, que son los que faltan para diagnosticar la causa:
>
> * ¿Qué equipo recibió una IP distinta?
> * ¿La IP entregada está dentro o fuera del rango reservado?
> * ¿Coincide con la reserva de otro de los equipos?
> * ¿Había reiniciado algo antes (router, equipo, interfaz de red)?

### 2.3 Puertos

El sistema usa UDP **7777** (tráfico de juego) y UDP **7778** (LAN discovery). Las reglas de firewall ya están creadas en los tres equipos. Solo hay que revisarlas si el discovery falla en el paso 4.

---

## 3. Preparación del headset y calibración de sala

Por cada estación, con el visor puesto:

1. Encender el visor y verificar que VIVE Hub y SteamVR estén corriendo.
2. Tomar el control y abrir el menú de configuración de espacio.
3. Gatillar la función de calibración de sala.
4. **Apoyar el control en el suelo** cuando lo pida, para que fije el nivel del piso.
5. Elegir la modalidad **estacionaria**. La sala es pequeña y la tarea es sentada; no se espera navegación.
6. **Orientar la dirección hacia la silla** donde se va a sentar ese participante.

El paso 6 es el que evita choques: cada participante queda orientado hacia su propia silla y, por lo tanto, separado de los otros dos en el espacio virtual compartido.

---

## 4. Inicio de la aplicación

En cada equipo, en este orden:

1. **Stimulus 1** — abrir Unity, Play, presionar `H`. Verificar en el HUD: *"Host activo — anunciando en LAN puerto 7777"*.
2. **Stimulus 2 y 3** — Play, presionar `C`. **Esperar a que el HUD confirme la conexión** antes de hacer cualquier otra cosa.

> **Importante — Bug 1.** En Client y Helper esa primera `C` también dispara una calibración de trackers, en el momento equivocado (con el operador sentado frente al teclado). Hay que recalibrar después, ya de pie. Ver el paso 5.

> **Importante — Bug 3.** No presionar `C` de nuevo mientras el cliente todavía busca al host: eso aborta y reinicia el intento de conexión. Esperar la confirmación del HUD.

---

## 5. Calibración de trackers

Seguir [`body_tracking_guide.md`](body_tracking_guide.md), sección 2.

Resumen del procedimiento: usuario de pie y derecho → `C` → verificar `[Calibration] ✓` en la Console → verificar las tres esferas (naranja = cintura, cian = pie izquierdo, magenta = pie derecho) → refinar con `R` tracker por tracker.

En Client y Helper, la calibración válida es **la segunda**, con el usuario ya de pie y con la conexión confirmada.

---

## 6. Batería de pruebas

Cada prueba indica el resultado esperado. Reportar **solo las desviaciones** respecto de lo esperado, más cualquier cosa que llame la atención aunque no esté en la lista.

---

### Bloque A — Interacción individual

Cada participante ejecuta estas pruebas por separado, sin coordinarse con los otros.

---

#### A1 · Poke con el dedo índice

Empujar un bloque de la torre con la punta del índice.

**Esperado:** el bloque se mueve al contacto en los tres roles.

**Ya conocido:** en Client y Helper el poke pasa por un RPC al servidor, así que responde con algo más de latencia y es menos preciso que en el Host. Es esperado; no reportarlo.

**Reportar:** si el bloque **no** reacciona en algún rol, o si el dedo lo atraviesa sin efecto.

---

#### A2 · Pinch + rayo (grab a distancia) — prueba prioritaria

**Esta es la prueba de mayor valor de la sesión.**

El salto de ~20 cm al hacer pinch en Client y Helper es un bug confirmado y abierto (Bug 12). Ya se intentó una corrección que **no funcionó**. La instrumentación de diagnóstico está en el código pero **nunca se ha ejecutado**.

> **No están probando si se arregló. Están recolectando la evidencia que falta para diagnosticarlo.**

**Procedimiento**

Apuntar a un bloque con el rayo, hacer pinch, mover el bloque, soltar.

Reproducir el salto **al menos tres veces en Client y tres en Helper, sobre bloques distintos**. En el Host, hacer la misma secuencia tres veces como control (ahí no debería ocurrir).

Anotar por cada intento:

* Qué bloque (nivel y posición en la torre).
* Dirección del salto: lateral o vertical.
* Magnitud aproximada.
* Si el bloque vuelve a su posición al soltar.
* Si la dirección se repite para el mismo bloque en intentos sucesivos.

**Imprescindible: guardar el log completo de la Console de los tres equipos al terminar esta prueba.**

Las líneas con prefijo `[JengaGrab]` contienen la instrumentación que discrimina entre las tres hipótesis abiertas sobre la causa. Sin ese log la sesión no aporta nada al diagnóstico, **aunque el salto se reproduzca perfectamente**. Guardar el log aun si el salto no aparece: esa también es información.

---

#### A3 · Estabilidad de la torre

Extraer un bloque intermedio con cuidado y observar la torre.

**Reportar:** si la torre colapsa sin contacto aparente, si algún bloque atraviesa a otro, o si algún bloque queda flotando.

---

### Bloque B — Percepción cruzada

Estas pruebas requieren coordinación verbal. Conviene que uno actúe y los otros dos observen y reporten.

---

#### B1 · ¿Se ven entre ustedes?

Cada participante mira a los otros dos.

**Reportar:** si algún avatar no aparece, aparece en una posición incoherente respecto de dónde está físicamente esa persona, o aparece de espaldas cuando debería estar de frente.

---

#### B2 · Movimiento de cabeza y brazos del otro

Un participante mueve la cabeza y los brazos de forma marcada. Los otros dos describen lo que ven.

**Ya conocido:** cuando el hand tracking pierde las manos —manos fuera del campo de visión del visor, o manos abajo— los targets de IK dejan de actualizarse y quedan congelados. El resultado es que los brazos del avatar quedan desincronizados del cuerpo y a veces **parecen salir desde la cabeza**. Es esperado en esta versión.

**Reportar:** si eso ocurre **con las manos visibles y bien tracked**, que sería un caso distinto.

---

#### B3 · Rayo de selección de los otros

Un participante apunta a un bloque con su rayo. Los otros dos indican si lo ven.

**Ya conocido — Bug 11:** el rayo solo lo ve quien lo emite. El componente que lo sincroniza está desactivado en el prefab del avatar. **Lo esperado es que nadie vea el rayo de los otros.**

**Reportar solo:** si alguien **sí** ve el rayo de otro, o si ve dos rayos superpuestos saliendo de su propia mano.

---

#### B4 · Consistencia de los bloques

Un participante toma un bloque y lo mueve lentamente. Los otros dos describen el movimiento que ven.

**Esperado:** los tres ven el bloque moverse hacia el mismo lugar, con retardo pero de forma coherente.

**Reportar:**

* Si algún observador ve el bloque en otra posición.
* Si lo ve volver atrás mientras quien lo manipula lo ve quieto.
* Si el bloque se duplica visualmente.
* Cuando aparezca el salto de A2: **si los observadores también lo ven, o si solo lo ve quien manipula.** Este dato es relevante para el diagnóstico del Bug 12 — distingue si el salto ocurre en la simulación autoritativa o solo en la vista local del que agarra.

---

### Bloque C — Tarea colaborativa

#### C1 · Partida corta

Jugar una partida de Jenga de unos 5 minutos entre los tres, con turnos, usando principalmente poke.

**Reportar en forma libre:** qué se sintió confuso, qué impidió coordinarse, en qué momento alguien no entendió lo que estaba haciendo otro. Este bloque busca problemas de usabilidad y de presencia social que las pruebas puntuales no capturan.

Dado que no hay rayo compartido, prestar atención especialmente a **cómo resolvieron la referencia a los bloques**: qué estrategias verbales aparecieron y cuánto costaron. Es información útil para dimensionar el impacto real del Bug 11.

---

## 7. Formato de reporte

Una fila por observación:

| Prueba | Equipo / rol | Qué se esperaba | Qué pasó | ¿Reproducible? | Severidad |
| --- | --- | --- | --- | --- | --- |
| A2 | Stimulus 2 / Client | Bloque sigue la mano sin saltos | | Sí / No / A veces | Bloqueante / Alta / Media / Baja |

### Material a guardar al cerrar la sesión

De **los tres equipos**:

- [ ] Log completo de la Console de Unity — especialmente `[JengaGrab]`, `[Calibration]`, `[LanDiscovery]`
- [ ] IP asignada a cada equipo, y si coincidía con la reservada en el router
- [ ] Branch y commit ejecutado (`git log -1 --oneline`)
- [ ] Planilla de observaciones completa
- [ ] Los CSV generados en `EyeTrackingLogs\`, renombrados por nodo para evitar colisión (Bug 2)

Sin el branch y el commit, un reporte de bug no es accionable.

---

## 8. Referencias

* [`architecture.md`](architecture.md) — mapa de subsistemas, flujos de datos y acoplamientos del sistema
* [`bug_dev.md`](bug_dev.md) — registro de bugs conocidos con causa raíz y correcciones propuestas
* [`body_tracking_guide.md`](body_tracking_guide.md) — guía operativa de calibración y registro de trackers
* [`eye_tracking_data_format.md`](eye_tracking_data_format.md) — especificación del CSV de mirada
* [`dev_log.md`](dev_log.md) — historial técnico de desarrollo
