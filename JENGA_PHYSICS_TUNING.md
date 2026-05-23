# Jenga Physics Tuning — Sandbox Branch

> **Branch**: `jenga-physics-prototype`
>
> **⚠️ Este branch NO está pensado para mergear a `main` tal cual.** Es un entorno de experimentación para encontrar la configuración física óptima de los bloques Jenga. Una vez identificados los valores ganadores, **sólo esos valores** se promueven al `main` (editando los presets `.asset` fuera de Play). El resto del andamiaje (HUD runtime, ScriptableObjects de presets sandbox, auto-build standalone) queda aquí.

---

## 1. Qué hay en este branch

Sobre `main`, este branch agrega:

| Elemento | Archivo / ruta | Propósito |
|---|---|---|
| ScriptableObject de configuración | `Assets/Jenga/Physics/JengaPhysicsConfig.cs` | Agrupa 27 parámetros físicos tuneables |
| Singleton runtime | `Assets/Jenga/Physics/JengaPhysicsRuntime.cs` | Aplica live-config a Project Physics + PhysicMaterial + Rigidbodies + Grabbables |
| HUD dual (desktop + VR) | `Assets/Jenga/Physics/JengaPhysicsTunerHUD.cs` | F6 toggle. OnGUI 2D para desktop + world-space TMP canvas para VR |
| 5 presets pre-tuneados | `Assets/Jenga/Physics/Presets/*.asset` | Baseline, RealWood, StableDemo, LoosePlay, PolishedVR |
| Editor tool | `Assets/Jenga/Physics/Editor/JengaPhysicsPresetCreator.cs` | Menú `XR Collab > Jenga > Create or Reset Default Physics Presets` |
| Preservación de velocidad al soltar | `Assets/Jenga/JengaGrabbable.cs` (modificado) | Ring buffer de 8 samples + clamp |
| Auto-build de torre sin pulsar H | `Assets/Jenga/JengaTowerGenerator.cs` (modificado) | Timeout de 2s en single-player |

Documentación técnica completa de cada componente: ver `Documentation/dev_log.md`, entrada `2026-05-22`.

---

## 2. Setup inicial (una sola vez después de checkout)

### 2.1. Generar los presets

1. En Unity: menú **XR Collab → Jenga → Create or Reset Default Physics Presets**.
2. Se crean (o sobreescriben) 5 `.asset` en `Assets/Jenga/Physics/Presets/`.
3. Es idempotente — podés re-correrlo en cualquier momento como "factory reset" si tocaste valores y querés volver al origen.

### 2.2. Crear el GameObject runtime

En la escena `Room.unity`:

1. Crear empty GameObject llamado `JengaPhysicsTuner` (cualquier ubicación de la jerarquía, p. ej. junto al `Network` o como child de `GameSystems`).
2. Add Component → `Jenga Physics Runtime`.
3. Add Component → `Jenga Physics Tuner HUD`.

### 2.3. Wiring del Inspector

En `JengaPhysicsRuntime`:

* **Block Material**: arrastrar `Assets/Jenga/JengaBlockPhysics.physicMaterial`.
* **Presets**: array tamaño 5, asignar en orden:
  * `[0]` → `01_Baseline.asset`
  * `[1]` → `02_RealWood.asset`
  * `[2]` → `03_StableDemo.asset`
  * `[3]` → `04_LoosePlay.asset`
  * `[4]` → `05_PolishedVR.asset`
* **Override Initial Config**: dejar vacío. (Si lo asignás, ese config se usa como punto de partida en vez del primer preset.)

En `JengaPhysicsTunerHUD`:

* Defaults razonables. Si el panel VR aparece muy lejos / cerca, ajustar `VR Panel Distance` (default 0.6 m). Si queda fuera del campo de visión inicial, ajustar `VR Panel Vertical Offset` o `Lateral Offset`.

### 2.4. Verificación

1. Play.
2. Si no pulsás nada en 2 segundos, la torre se construye automáticamente en modo standalone.
3. Pulsá **F6**. Debería aparecer el panel flotante con los parámetros.

---

## 3. Cómo usar el tuner

### 3.1. Hotkeys (todos por teclado físico)

| Tecla | Acción |
|---|---|
| `F6` | Toggle visibilidad del panel (anclar a vista actual al abrir) |
| `Tab` / `Shift+Tab` | Seleccionar siguiente / anterior parámetro |
| `↑` / `↓` (o `+` / `-`) | Ajustar el valor del parámetro seleccionado por su step base |
| `Shift` + ajuste | Step × 10 (cambios gruesos) |
| `Alt` + ajuste | Step × 0.1 (refinamiento fino) |
| `1` – `5` | Cargar preset N directamente |
| `[` / `]` | Ciclar al preset anterior / siguiente |
| `R` | Re-aplicar liveConfig (útil si algún bloque quedó con valores stale) |
| `B` | Reconstruir torre (necesario sólo para parámetros marcados `[B]`: H-Spacing, V-Spacing, Drop Height) |
| `P` | Re-anclar panel VR a la vista actual (si te moviste y quedó lejos) |
| `Esc` | Cerrar panel |

### 3.2. UX del panel VR

* El panel **se ancla a la vista actual cuando se abre con F6** y queda fijo en world space. No te sigue la cabeza (eso sería nauseabundo).
* Si te alejaste o el panel quedó en un ángulo raro, pulsá **P** para re-anclarlo a tu vista actual.
* Si preferís que sí siga la cabeza (modo "wrist menu"), activá `VR Panel Follow Head` en el Inspector.

### 3.3. Lo que ves en el HUD

```
== JENGA PHYSICS TUNER (F6) ==
Preset: [3/5] Stable Demo
Blocks applied: 18

  Static Friction         = 1.0000
> Dynamic Friction        = 0.8500           ← parámetro seleccionado
  Bounciness              = 0.0000
  Friction Combine        = Maximum
  ...
  H-Spacing               = 0.0000            [B]
  V-Spacing               = 0.0000            [B]
  Drop Height             = 0.0050            [B]
```

* La flecha `>` indica el parámetro activo (modificado por `↑/↓`).
* `[B]` marca parámetros que requieren `B` (rebuild) para tomar efecto.
* `Blocks applied: N` confirma a cuántos bloques se aplicó el último config.

---

## 4. Caveats importantes

### 4.1. Los valores en Play mode **NO persisten**

Cuando salís de Play mode, Unity **revierte automáticamente todas las mutaciones a assets** (`PhysicMaterial`, presets ScriptableObject). Esto es intencional de Unity y es lo que permite "experimentar libremente sin romper nada".

Implicación práctica: si encontrás una combinación ganadora dentro de una sesión de Play, **tenés que anotar los valores manualmente** (capturar el HUD con un screenshot, o anotarlos a mano) y después salir de Play, editar el preset `.asset` correspondiente en el Inspector, y guardar la escena (Ctrl+S).

Excepción: los cambios al `ProjectSettings/DynamicsManager.asset` (Solver Iterations, Contact Offset, Bounce Threshold, Sleep Threshold) **sí persisten** entre sesiones de Play. Si llegás a un valor global ganador, queda como default del proyecto.

### 4.2. NGO desactivado durante tuning

El sistema es **single-player only**. Si pulsás H o C durante una sesión de tuning, los cambios afectan sólo a tu instancia local; los remotos no los ven. Recomendación: **no pulses H/C mientras tuneás**.

Si tenés un Network Manager en escena, el `JengaTowerGenerator` con `autoBuildIfNoServer = true` (default) construye la torre standalone después de 2s sin que pulses nada.

### 4.3. Tower spacings requieren rebuild explícito

Los parámetros marcados `[B]` en el HUD (`H-Spacing`, `V-Spacing`, `Drop Height`) afectan **cómo se construye la torre**, no cómo se comportan bloques existentes. Pulsá `B` después de cambiarlos para reconstruir.

### 4.4. Velocidad de release puede sentirse "demasiado"

Si activás `Preserve Vel @Release` y el bloque "vuela" al soltar, bajá `Release Vel Max` (default 3 m/s) a 1.5 o 2. El clamp existe específicamente para que jitter de hand tracking no se traduzca en velocidades irreales, pero el límite por defecto puede ser generoso para gestos chicos.

### 4.5. PhysicMaterial mutado in-memory

El `JengaBlockPhysics.physicMaterial` se muta en memoria al aplicar configs. Si abrís el asset en el Inspector durante Play, vas a ver los valores actuales del HUD (no los originales). Esto es esperable. Al salir de Play vuelven a los valores del disco.

---

## 5. Workflow típico de una sesión de tuning

Una sesión típica de 20-30 min:

1. **Play**. Esperar 2s, la torre aparece.
2. **F6**. Panel aparece anclado al frente.
3. **`1`**: cargar Baseline. Validar que se siente como antes de la sandbox (sanity check).
4. **`2`** → `3` → `4` → `5`: ciclar por los 5 presets, dedicando ~1-2 min a cada uno. Hacer la misma acción en cada uno (agarrar un bloque, sacudirlo, soltarlo; hacer un poke; intentar tumbar la torre). Tomar nota mental de cuál se siente mejor.
5. Volver al favorito (ej. `5` = PolishedVR).
6. **Tab**, navegar al parámetro que sospechás puede mejorar (típicamente `Static Friction` o `Mass`).
7. **↑/↓** para ajustar. Usar `Shift+↑` para cambios gruesos cuando estás explorando, `Alt+↑` para refinar cerca del valor que te gusta.
8. Al encontrar un set ganador: **anotar todos los valores del panel** (screenshot o copia manual).
9. **Salir de Play**.
10. Editar el preset `.asset` correspondiente en el Inspector con los valores anotados. Guardar.
11. Volver a Play, cargar ese preset, verificar que se siente igual a lo que anotaste.

---

## 6. Plan de pruebas propuesto

Para acotar el trabajo de tuning a algo concreto y reproducible. Pensado como ~4-6 sesiones de 20-30 minutos cada una.

### Fase 1 — Validación de Baseline (1 sesión)

Objetivo: confirmar que el preset `Baseline` reproduce fielmente el comportamiento previo al tuner.

* Cargar `01_Baseline`.
* Hacer las acciones típicas: agarrar un bloque del medio, deslizarlo afuera, ponerlo encima de la torre. Empujar la torre con un dedo. Sacudir un bloque y soltarlo.
* Comparar mentalmente con la memoria del pre-tuner. **No debería haber diferencias.**
* Si hay diferencia, es indicio de que algún campo del SO no replica fielmente el setup heredado — investigar antes de seguir.

### Fase 2 — A/B blind entre los 5 presets (1-2 sesiones)

Objetivo: ranking subjetivo inicial de los 5 presets sin sesgo.

Protocolo:

1. Cerrar los ojos. Pedirle a alguien que pulse 1-5 en orden aleatorio (o pulsar uno mismo intentando no mirar). Tape el monitor.
2. Hacer 3 acciones estándar durante 30s (definirlas antes; sugerencia: "extraer un bloque del medio + ponerlo en la cima + dar 2 pokes a la torre").
3. Mirar el HUD: anotar qué preset estaba y un rating 1-5 de "se sintió natural".
4. Repetir con otro preset.
5. Hacer al menos 2 pases por todos para reducir sesgo de orden.

Output esperable: 1 o 2 presets favoritos. Probablemente `PolishedVR` y/o `RealWood`.

### Fase 3 — Fine-tuning del preset ganador (2 sesiones)

Objetivo: tomar el preset ganador de la fase 2 y refinarlo por parámetro.

Orden sugerido de exploración (de más impactante a menos):

1. **Grab feel** (`Preserve Vel`, `Grab Pos Lerp`, `Release Vel Samples`). Probable: subir Lerp a 0.6-0.7 para follow más 1:1. Probar `Release Vel Max` entre 2.0 y 4.0.
2. **PhysicMaterial fricción** (`Static`, `Dynamic`). Probable rango ganador: 0.4-0.7 para Static; Dynamic = Static - 0.05.
3. **Mass**. Entre 0.05 y 0.12 kg. Lo más impactante en sensación de "presencia" del bloque.
4. **Angular Drag**. Entre 0.1 y 0.3. Demasiado bajo y los bloques giran indefinidamente al ser tocados; demasiado alto y se sienten "amortiguados".
5. **Bounciness**. Mantener ≤ 0.05. Cualquier valor mayor se siente "cartón húmedo".
6. **Solver Iterations** (global). Probar 10, 12, 15. Usar `PerformanceHUD` (F3) para verificar impacto en p95 frame.

Por cada cambio: hacer las mismas 3 acciones estándar de la Fase 2. Si mejoró, mantener. Si empeoró o no se nota, volver al valor anterior.

**Anotar todo en una tabla** (texto plano o spreadsheet) con: fecha, parámetro, valor probado, sensación 1-5, notas.

### Fase 4 — Validación con un segundo evaluador (1 sesión)

Objetivo: contrastar el resultado del tuning con alguien que NO participó del proceso (reduce sesgo de familiaridad).

* Configurar el preset ganador como Override Initial Config en `JengaPhysicsRuntime` (así arranca con él al Play).
* El evaluador hace las acciones típicas sin saber qué preset es.
* Después, ciclar entre el ganador y `Baseline` y preguntar cuál se siente más natural.
* Si el evaluador NO prefiere el ganador, volver a Fase 3 con sus observaciones.

### Fase 5 — Graduación de valores a main (no sesión, trabajo de git)

Objetivo: promover los valores ganadores al `main` sin llevar el resto del andamiaje de tuning.

Opciones:

* **Opción A — cherry-pick selectivo**: copiar a mano los valores del preset ganador a la configuración del prefab + project settings + PhysicMaterial en un branch nuevo creado desde `main`. Commit minimal con solo esos cambios. Merge a main.
* **Opción B — promover el sistema completo**: si después del tuning decidís que el sistema runtime también vale la pena en main (para tunear en el futuro con participantes reales), mergear el branch entero. En ese caso conviene primero limpiar (deshabilitar `autoBuildIfNoServer` o ponerlo `false` como default) y agregar guards `#if UNITY_EDITOR` o `[Conditional("JENGA_TUNING")]` al HUD para que no se compile en builds de producción.

**Decisión recomendada**: Opción A. Tener el HUD de tuning en producción mete superficie de bug y UI accidental. El branch sobrevive como referencia (`jenga-physics-prototype`) para futuras iteraciones de tuning sin contaminar `main`.

---

## 7. Solución de problemas comunes

### El panel VR no aparece al pulsar F6

* Verificar que `Show In VR` está activo en el componente `JengaPhysicsTunerHUD`.
* Verificar que el panel no quedó ANCLADO atrás tuyo. Pulsá `P` para re-anclar.
* Si seguís sin verlo, revisar la consola: `[JengaPhysicsRuntime]` debería loguear al cargar un preset.

### La torre no se construye al pulsar Play

* Si pasaron menos de 2 segundos: esperar.
* Si pasaron más: revisar que `JengaTowerGenerator` tiene `autoBuildIfNoServer = true`.
* Si tenés `NetworkManager.Singleton` y `IsServer == false` y `autoBuildIfNoServer = false`: o pulsás H, o ponés el flag en true.

### "Blocks applied: 0" en el HUD

* No hay GameObjects con `JengaBlockTag` en escena. La torre no se construyó. Ver punto anterior.

### El HUD desktop tapa otros HUDs

* El HUD del tuner se pinta abajo-izquierda. Si choca con otro, ajustá `Margin` o `Fixed Width` en el Inspector.

### Cambié valores del HUD pero la torre se comporta igual

* Algunos parámetros (Tower spacings) requieren rebuild — pulsá `B`.
* Pulsá `R` para forzar re-aplicación.
* Revisá si hay otro `NetworkRigidbody` interfiriendo (en multiplayer; no debería ser el caso si seguís single-player).

---

## 8. Cuando termines la sesión de tuning

1. Anotar los valores ganadores en un archivo aparte (sugerencia: `Documentation/jenga-tuning-results.md` en este mismo branch, con fecha y comentarios).
2. Editar el preset SO correspondiente con esos valores y commitear (`Assets/Jenga/Physics/Presets/`).
3. Push del branch.
4. Cuando decidas promover a main: seguir la Opción A de la Fase 5.

Este branch puede vivir indefinidamente como sandbox de tuning. Cada nueva ronda de afinamiento puede usar este mismo branch (o un branch derivado para no perder el estado anterior).
