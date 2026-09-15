# Plan de la Vía B — Infraestructura de testeo e integración háptica

**Proyecto:** ANID Exploración 13250116
**Fecha:** 2026-09-14
**Documento padre:** [`roadmap_experimental.md`](roadmap_experimental.md)
**Alcance:** desarrollo de la aplicación VR en paralelo a la vía de medición, con foco en la estrategia de testeo que lo hace sostenible.

---

## 1. Propósito y estrategia

La Vía B corre sin bloquear la recolección de datos. Su objetivo no es solo cerrar defectos, sino **cambiar la economía de detectarlos**.

Hoy, verificar cómo un participante ve lo que hace otro requiere tres personas con tres visores en el laboratorio. Eso hace que la verificación casi nunca ocurra durante el desarrollo, y que los defectos de sincronización se descubran tarde — a veces recién en una sesión de pruebas.

**La estrategia es sustituir a los participantes por clientes simulados.** El sistema genera el comportamiento de dos jugadores de forma determinista, y su representación se valida programáticamente desde cualquier cliente, sin hardware. El desarrollador conserva un solo visor para juzgar la experiencia; todo lo demás — replicación, propiedad de objetos, convergencia de estado — se verifica desde un escritorio.

Esto se apoya en dos decisiones de diseño:

* **Separar la fuente de entrada del resto del sistema**, de modo que el juego no sepa si las poses vienen de un visor, de un guion o de una sesión grabada.
* **Expresar el comportamiento correcto como invariantes verificables**, no como algo que se juzga mirando la pantalla.

El principio que ordena todo el plan: **el 80–90% de los defectos de multijugador deben detectarse antes de necesitar hardware.**

---

## 2. Punto de partida real

Antes de proponer construcción conviene inventariar lo que ya existe. El proyecto tiene más base de la que parece, y también un bloqueador técnico que hay que resolver temprano.

### 2.1 Lo que ya está construido

| Componente | Ubicación | Qué aporta |
|---|---|---|
| `PresenceMode` (Real / Mock / Disabled) | `Assets/Multiplayer/Mock/` | Enumeración de modo por slot de jugador |
| `PlayerSlotConfig` | `Assets/Multiplayer/Mock/` | Rol, modo, color, posición y rotación simuladas |
| `TriadSessionManager` | `Assets/Multiplayer/` | Único consumidor actual de la configuración de slots |
| `AcquisitionMockServer` | `Assets/BiolabUDPSync/` | Servidor BioLab simulado, con fallback automático |
| `BuildXRDisabler` + `EditorXRBootstrap` | `Assets/NetworkAudit/` | Ejecutar builds sin XR, o forzar XR en el Editor |
| `BuildFlyCamera` | `Assets/NetworkAudit/` | Cámara libre para inspeccionar la escena sin visor |
| `BuildAuditHUD` + `PerformanceHUD` + `NetworkAuditLogger` | `Assets/NetworkAudit/` | Estado de red, métricas de rendimiento, log de red |
| `AvatarSetupValidator` | `Assets/Editor/` | Valida el prefab de avatar; **precedente directo del lint de assets** |

Dos de estos merecen atención especial.

La carpeta `NetworkAudit/` es, en la práctica, **el embrión del cliente espectador**: ya permite levantar un cliente sin XR, volar por la escena y ver el estado de la red. Lo que falta es poder mirar *desde la perspectiva de otro jugador*.

`AvatarSetupValidator` es **la prueba de concepto de la estrategia de lint** que este plan propone ampliar. El `dev_log` registra que detectó el `TwoBoneIKConstraint` mal configurado del Helper — un defecto invisible en el Inspector y capturado por el validador en una corrida.

El mock de presencia, en cambio, es estático: `mockPosition` y `mockEuler` son valores fijos. Sirve para que la escena no se vea vacía, pero no genera comportamiento y por lo tanto no ejercita la capa de red.

### 2.2 Lo que falta

* **Abstracción de entrada.** El hand tracking se lee directamente de `XRHandSubsystem` en **10 archivos**, y `InputDevices` en **3 más**. No hay capa intermedia, así que hoy es imposible alimentar el juego con datos que no vengan del hardware.
* **Comportamiento simulado.** Nada genera poses de cabeza y manos, gestos de pinch o poke, ni secuencias de interacción.
* **Sistema de escenarios** para reproducir situaciones de forma determinista.
* **Grabación y reproducción de sesiones.**
* **Assertions automáticas** sobre la sincronización entre jugadores.

### 2.3 Un bloqueador técnico a resolver temprano

**No hay assembly definitions.** El proyecto no tiene ningún `.asmdef`, así que todo el código vive en `Assembly-CSharp`. Sin separar al menos un ensamblado para el código de producción, los ensamblados de test no pueden referenciarlo de forma limpia.

Esto condiciona el orden de trabajo, pero **no bloquea todo**: como se explica en la §5, los lint de configuración son scripts de Editor que no requieren ensamblados ni Test Framework, y pueden empezar antes.

El Test Framework sí está disponible: viene incluido en `com.unity.feature.development`, ya presente en el manifiesto. Y `com.unity.recorder` 4.0.3 también está instalado, lo que da captura de video de sesiones sin agregar dependencias.

---

## 3. Arquitectura de testeo en capas

La aplicación tiene una cadena más larga que un juego multijugador convencional:

```
Entrada del jugador → tracking → interacción → estado del juego
   → red → representación remota → registro de datos
```

Cada eslabón se prueba de forma distinta.

| Nivel | Qué prueba | Hardware | Frecuencia |
|---|---|---|---|
| 1 — Configuración (lint) | Prefabs, escenas, manifiesto, teclas | Ninguno | Cada build |
| 2 — Unitario | Reglas, matemáticas de pose, transformaciones | Ninguno | Cada cambio |
| 3 — Simulación e invariantes | Sincronización, ownership, replicación | 1 PC | Cada cambio |
| 4 — Integridad de datos | Contenido de los CSV producidos | 1 PC | Diaria |
| 5 — Integración de hardware | Visor, trackers, guantes | 1 visor | Semanal |
| 6 — Sistema completo | 3 participantes reales | Laboratorio | Antes de cada hito |

El nivel 6 es la validación previa a una sesión experimental, no la herramienta de trabajo diaria.

---

## 4. Qué testear primero — ranking por costo-efectividad

### 4.1 El patrón de los defectos: configuración, no lógica

Revisando el historial real de defectos del `dev_log` y `bug_dev.md`, aparece un patrón que determina dónde conviene invertir:

| Defecto real | Naturaleza |
|---|---|
| `NetworkedAvatarHands` con `m_Enabled: 0` (Bug 11) | Casilla en el prefab |
| Target y Hint intercambiados en el IK del Helper | Slot en el prefab |
| `showViz: 1` guardado en la escena | Campo serializado en la escena |
| SDK de HTC apuntando a `file:C:/Users/…/Downloads/…` | Entrada en `manifest.json` |
| IP `192.168.88.182` hardcodeada en `Room.unity` | Campo en la escena |
| Tecla `C` con tres oyentes (Bug 1) | Configuración de input dispersa |
| `participantId` desincronizado (Bug 2) | Componente creado en runtime |

**Ninguno lo atrapa un test unitario de lógica. Todos los atrapa un lint de assets.** Por eso el nivel 1 va primero, aunque intuitivamente uno empezaría por los unitarios.

### 4.2 Ranking

**Nivel 1 — Lint de configuración.** Máximo retorno. Corren en segundos, sin Play mode, sin ensamblados.

| Test | Qué verifica | Esfuerzo |
|---|---|---|
| Lint de manifiesto | Ninguna dependencia con ruta `file:`; versiones fijas | 2 horas |
| Lint de escena | Flags de debug en `false`; sin IP hardcodeadas | 1 día |
| Lint de prefab | Componentes de sincronización habilitados; slots de IK completos; campos obligatorios no nulos | 1–2 días |
| Lint de teclas | Ningún `KeyCode` con más de un oyente activo | 1 día |

**Nivel 2 — Unitarios de lógica pura.** Baratos pero de alcance estrecho. Los que valen la pena en este código: `TrackingToWorld()` con la negación de X y Z — fue un bug real y es matemática pura; `ParseBroadcast()`, incluyendo un caso que lo llame desde un thread secundario, que habría atrapado el `UnityException` que se tragaba el `catch` vacío; el cálculo de altura del avatar al agacharse; la geometría de generación de la torre. Horas cada uno.

**Nivel 3 — Invariantes de estado de red.** Alto retorno. Atacan la clase de defecto que el `dev_log` describe como recurrente: *"un cambio aparentemente inocuo en la lógica visual rompió silenciosamente un sistema de física o de red"*. Son bugs de estado, invisibles a la inspección manual. Requieren el arnés y `SimulatedPlayer`, así que llegan en B.3. Esfuerzo: ~1 semana una vez que existe la simulación.

**Nivel 4 — Integridad del pipeline de datos.** Alto retorno, habitualmente subestimado. Correr una sesión simulada y validar el CSV resultante:

* El encabezado tiene las 44 columnas esperadas
* Ninguna fila con `*_valid = 1` y campos vacíos
* `timestamp_rel_s` monótonamente creciente
* `is_calibrated` coherente con la secuencia de calibración
* Los tres nodos producen archivos con identificadores distintos

Este nivel protege **el producto real del proyecto, que son los datos**. La corrupción de datos es silenciosa: no crashea, no se ve, y se descubre meses después cuando el análisis no cuadra. Ya ocurrió — 1962 filas sin un solo dato de tracker, detectadas solo porque alguien abrió el archivo. Esfuerzo: 3–4 días con el arnés montado.

**Nivel 5 — Rendimiento.** Retorno bajo, costo bajo. Con el arnés listo, agregar presupuestos es casi gratis: ancho de banda por avatar ≤ 16 KB/s — número que el `dev_log` ya declara, así que es un presupuesto listo para afirmar — y tiempo de frame bajo el umbral que mantiene la latencia aceptable. Vale la pena porque el informe a ANID afirma que el diseño de red prioriza minimizar la latencia, y conviene poder demostrarlo.

### 4.3 Lo que conviene no hacer

**No escribir tests para el código legacy marcado para eliminación** — `NetworkedAvatarSkeleton`, `HandSkeletonRenderer`, `AvatarHandJointDebugViz`. Borrarlo primero.

**No perseguir un porcentaje de cobertura.** En un equipo pequeño, la cobertura como meta produce tests de relleno que después hay que mantener.

**No testear comportamiento de Unity.** Si `MovePosition` funciona o no, no es problema de este proyecto.

### 4.4 Los primeros cinco tests

En este orden. Los cuatro primeros se pueden hacer **antes** del trabajo de assembly definitions:

1. **Lint de manifiesto** — ninguna dependencia con ruta `file:`. Dos horas, y evita repetir la sesión de debugging del SDK de HTC.
2. **Lint de escena** — `Room.unity` sin IP hardcodeada y con los flags de debug en `false`. Un día, cubre dos defectos ya vividos.
3. **Lint de prefab** — extender `AvatarSetupValidator` para verificar además que los componentes de sincronización están habilitados. Un día, y habría prevenido el Bug 11.
4. **Lint de teclas** — enumerar los `KeyCode` que escucha cada componente activo y fallar si hay colisión. Un día, y habría prevenido el Bug 1.
5. **Unitario de `TrackingToWorld`** — primer test bajo el Test Framework, ya con asmdef. Sirve para validar que el arnés funciona antes de invertir en los niveles 3 y 4.

Los cuatro primeros suman **~3 días de trabajo y cubren cinco defectos que ya costaron sesiones de laboratorio.**

---

## 5. Cómo se ejecuta cada nivel

### 5.1 Los lint son código suelto, sin infraestructura

Compilar es compilar C#; los lint inspeccionan *assets*, así que algo tiene que invocarlos. Cuatro opciones:

| Disparador | Cuándo corre | Costo de montarlo |
|---|---|---|
| Comando de menú | Cuando alguien lo pide | Nulo — es lo que ya hace `AvatarSetupValidator` |
| **Al construir la build** | Cada build | Muy bajo, y puede **abortar** la build |
| Al importar assets | Cada cambio de asset | Bajo, pero se vuelve ruidoso |
| Línea de comandos | En CI o en un git hook | Medio; requiere infraestructura externa |

La recomendación es **menú + hook de build**. El hook da automatización real sin montar CI: cada build valida el proyecto y falla si algo está mal.

```csharp
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

public class LintAlConstruir : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        var problemas = ProjectLint.RunAll();
        if (problemas.Count == 0) return;

        foreach (var p in problemas) Debug.LogError($"[Lint] {p}");
        throw new BuildFailedException($"{problemas.Count} problemas de configuración. Build abortada.");
    }
}
```

Y un lint concreto — el del manifiesto, que habría evitado la sesión completa de debugging del SDK de HTC:

```csharp
public static List<string> RevisarManifiesto()
{
    var problemas = new List<string>();
    string json = File.ReadAllText("Packages/manifest.json");
    foreach (Match m in Regex.Matches(json, "\"([^\"]+)\"\\s*:\\s*\"(file:[^\"]+)\""))
        problemas.Add($"Dependencia con ruta local del disco: {m.Groups[1].Value}");
    return problemas;
}
```

Eso es todo: scripts de Editor, sin ensamblados, sin Test Framework. Por eso los lint van primero — dan valor antes de montar nada.

### 5.2 Qué es el arnés

A partir del nivel 3 hace falta un **arnés de pruebas**: el andamiaje que permite que exista cualquier test automatizado. En este proyecto se compone de:

* Los assembly definitions que separan código de producción de código de test
* El ensamblado de test con sus referencias al Test Framework
* La clase base que levanta un host y N clientes en proceso
* `IPlayerInput` y `SimulatedPlayer`, que permiten alimentar comportamiento
* Utilidades compartidas: esperar a que converja un estado, obtener la vista remota de un jugador desde otro cliente

La característica del arnés es que **se paga una vez**. Montarlo cuesta entre una y dos semanas; después, escribir un test nuevo cuesta media hora. Antes de que exista, cada test cuesta lo mismo que el arnés entero — que es exactamente la razón por la que hoy no hay ninguno.

### 5.3 Cómo se ve un test de invariante

Los tests de red son de **PlayMode**, no de EditMode, porque necesitan el game loop corriendo. Levantan un host y dos clientes dentro del mismo proceso:

```csharp
public class SincronizacionDeManos : NetcodeIntegrationTest
{
    protected override int NumberOfClients => 2;

    [UnityTest]
    public IEnumerator LaManoDelHostLlegaAlCliente()
    {
        var host = SimulatedPlayer.Para(m_ServerNetworkManager);
        host.PonerManoDerecha(new Vector3(0.5f, 1.2f, 0.3f));

        yield return WaitForConditionOrTimeOut(() =>
            VistaRemotaDe(host, en: m_ClientNetworkManagers[0]).ManoDerecha.HasValue);

        float error = Vector3.Distance(
            host.ManoDerecha,
            VistaRemotaDe(host, en: m_ClientNetworkManagers[0]).ManoDerecha.Value);

        Assert.That(error, Is.LessThan(0.01f));
    }
}
```

> **A verificar al comenzar B.1.** NGO incluye una clase base de integración (`NetcodeIntegrationTest`) que levanta host y clientes en proceso, diseñada exactamente para esto. Hay que confirmar que está disponible en la versión 1.12.2 y cómo se referencia su ensamblado. Es una verificación de diez minutos, y conviene hacerla antes de comprometer el resto del plan: si no estuviera, habría que escribir ese armazón a mano, lo que agrega unos días a B.1.

---

## 6. Las cuatro piezas a construir

### 6.1 Abstracción de entrada — `IPlayerInput`

Es la pieza habilitante: sin ella ninguna de las otras tres es posible.

```csharp
public interface IPlayerInput
{
    Pose HeadPose { get; }
    Pose LeftHandPose { get; }
    Pose RightHandPose { get; }
    bool LeftPinch { get; }
    bool RightPinch { get; }
    bool LeftPoke { get; }
    bool RightPoke { get; }
    Pose WaistPose { get; }
    Pose LeftFootPose { get; }
    Pose RightFootPose { get; }
    bool IsValid { get; }
}
```

Con cuatro implementaciones: `XRPlayerInput` (hardware real), `SimulatedPlayerInput` (guion), `ReplayPlayerInput` (sesión grabada) y `NullPlayerInput` (ausente).

**Alcance del refactor.** Diez archivos leen `XRHandSubsystem` directamente, pero cinco son visualizaciones legacy ya marcadas para eliminación en el `TODO(cleanup)` del `dev_log`: `NetworkedAvatarSkeleton`, `HandSkeletonRenderer`, `AvatarHandJointDebugViz`, `NetworkedAvatarHands` y parte de `PinchDebugVisualizer`.

> **Consecuencia de secuenciamiento:** conviene ejecutar el Bug 10 (limpieza del código muerto) **antes** del refactor, no después. Reduce la superficie de 10 archivos a cinco de producción: `NetworkedAvatarPose`, `BodyTrackingSessionLogger`, `PinchDetector`, `HandRayDriver` y `TrackerBodyCalibration`. Eso cambia la prioridad del Bug 10 de "limpieza opcional" a "prerequisito".

### 6.2 Jugador simulado y escenarios — `SimulatedPlayer` + `ScenarioRunner`

Un `SimulatedPlayer` produce `IPlayerInput` a partir de un guion. Un `ScenarioRunner` ejecuta secuencias deterministas con primitivas como `MoveHead`, `MoveHand`, `Pinch`, `Release`, `Poke`, `Wait`, `Disconnect`, `Reconnect`.

Escenarios iniciales, elegidos por su correspondencia con defectos abiertos y con la tarea experimental:

| Escenario | Qué ejercita |
|---|---|
| `GRAB_BLOCK` | Un cliente no-Host toma un bloque y lo suelta |
| `GRAB_CONFLICT` | Dos jugadores intentan tomar el mismo bloque simultáneamente |
| `POKE_SEQUENCE` | Empujes sucesivos desde los tres roles |
| `JOINT_ATTENTION` | Un jugador apunta; los otros dos dirigen la mirada |
| `DISCONNECT_RECONNECT` | Caída y reincorporación de un cliente durante la partida |
| `IDLE_PRESENCE` | Tres jugadores quietos — línea base para detectar deriva |

**Configuraciones de ejecución.** Para desarrollo diario basta una sola máquina, aprovechando que `BuildXRDisabler` permite correr builds sin visor:

```
Editor     → Host, con el visor puesto
Build #1   → Client, sin XR, misma máquina
Build #2   → Helper, sin XR, misma máquina
```

Para validación previa a una sesión experimental, la configuración usa los tres PC del laboratorio y ejercita además el descubrimiento LAN, el firewall y la sincronía de reloj entre máquinas — que es donde han estado varios de los defectos reales:

```
        ROUTER
           │
  ┌────────┼────────┐
 PC1      PC2      PC3
  │        │        │
Host     Client   Helper
  │        │        │
visor   simulado simulado
```

En ambos casos el desarrollador se pone un solo visor y observa cómo se comportan los otros dos.

### 6.3 Cliente espectador

Un cuarto cliente sin XR — extendiendo lo que ya hace `NetworkAudit/` — que muestre el estado sincronizado de los tres jugadores y permita **cambiar la cámara a la perspectiva de cualquiera de ellos**.

Debe mostrar, por jugador: pose de cabeza y manos, estado de pinch y poke, estado de los trackers, bloque en posesión y estado del rayo de selección. Más métricas de red: latencia, paquetes, tick.

### 6.4 Grabación y reproducción

Registrar por frame todo lo que entra por `IPlayerInput`, más los eventos de red y de juego, de modo que una sesión pueda reproducirse sin que nadie se ponga un visor.

**Esta pieza tiene un segundo uso que justifica su costo por sí solo.** El mismo sistema sirve como *flight recorder* del experimento: permite reproducir la sesión de un participante real, verla desde cualquier ángulo, superponer vectores de mirada y verificar que los datos recolectados son correctos. Para un proyecto que debe demostrar la validez de sus datos, es una capacidad de análisis, no solo de testeo.

---

## 7. Invariantes

Los defectos de multijugador suelen ser **de estado, no visuales**. Conviene expresarlos como invariantes verificables automáticamente.

**Sincronización de manos.** Para todo par de jugadores A y B:
`distancia(A.manoDerecha, B.vistaRemotaDe(A).manoDerecha) < 1 cm`

**Propiedad exclusiva.** Si A posee el bloque 17:
`A.puedeManipular(17) == true` y `B.puedeManipular(17) == false` para todo B ≠ A

**Convergencia.** Tras soltar y estabilizarse, los tres jugadores convergen a la misma pose de cada bloque, dentro de una tolerancia.

**Unicidad.** Existe exactamente una instancia de cada bloque en cada cliente.

**Liberación.** Al soltar, la propiedad vuelve al servidor y el bloque sale del estado cinemático.

La cuarta y la quinta son directamente relevantes para el Bug 12.

---

## 8. Cómo esta infraestructura ataca los defectos abiertos

Esta es la razón por la que el trabajo de testeo va primero y no después.

| Defecto | Cómo cambia con la infraestructura |
|---|---|
| **Bug 12** — salto de ~20 cm al pinch | Hoy requiere dos personas con visores y es difícil de reproducir. Con `ScenarioRunner`, `GRAB_BLOCK` lo reproduce de forma determinista N veces, con los logs `[JengaGrab]` ya instrumentados. **Es la vía más directa para cerrar el defecto P0 del proyecto.** |
| **Bug 11** — rayo remoto invisible | El cliente espectador con cambio de perspectiva es literalmente el caso de uso: ver lo que ve otro sin ponerse su visor. Sirve para verificar la corrección, que es de un solo paso. |
| **Bug 3** — `C` durante discovery reinicia conexión | Escenario `DISCONNECT_RECONNECT` con assertion sobre el estado de la corrutina de conexión. |
| **Bug 5** — `StartHost()` falla en silencio | Assertion sobre `IsListening` después de `StartHost()`, en test unitario. |
| **Bugs 1 y 2** — integridad de los datos | Test de nivel 4: correr una sesión simulada y verificar el CSV resultante. Protege la corrección de la Fase 1 de la vía de medición contra regresiones. |
| **Bug 9** — colisión de tecla `R` | Lint de teclas, nivel 1. |
| **Bug 10** — código muerto | La cobertura de tests es lo que permite borrar código legacy sin miedo. |

El patrón que se repite en el `dev_log` — *"un cambio aparentemente inocuo en la lógica visual rompió silenciosamente un sistema de física o de red"* — es exactamente el que las assertions automáticas detectan y la inspección manual no.

---

## 9. Integración de guantes hápticos

Los guantes llegan durante el Semestre 3. El modelo específico determina el SDK y la ruta de integración, así que el plan se estructura de forma independiente del proveedor.

### 9.1 Por qué la abstracción de entrada importa aquí

`IPlayerInput` cambia la naturaleza del trabajo. Con la abstracción en su lugar, los guantes son **una implementación más** del mismo contrato, y el resto del sistema no se entera de dónde vienen las poses de dedos. Sin ella, integrarlos significa tocar los mismos diez archivos que hoy leen `XRHandSubsystem` directamente.

Conviene extender el contrato con un canal de salida, porque los guantes no solo leen sino que devuelven fuerza:

```csharp
public interface IHapticOutput
{
    void ApplyForceFeedback(Finger finger, float intensity);
    void Pulse(Hand hand, float intensity, float durationMs);
}
```

Con una implementación real y una simulada que solo registre las órdenes en el log — lo que permite **probar la lógica de retroalimentación háptica sin ponerse los guantes**, igual que con el resto de la entrada.

### 9.2 Fases de integración

**H.1 — Integración técnica.** SDK, emparejamiento, calibración por usuario, lectura de flexión de dedos, verificación de la frecuencia de actualización. Prueba de integración aislada, sin red.

**H.2 — Sustitución de la fuente de manos.** Los guantes pasan a alimentar `IPlayerInput` en lugar de — o combinados con — el hand tracking óptico del visor. Aquí aparece la decisión de diseño relevante: el tracking óptico falla cuando las manos salen del campo de visión, que es un problema documentado en la Fase 0 del roadmap. Los guantes no tienen esa limitación para la flexión de dedos, pero necesitan una fuente de posición de muñeca. **La combinación — muñeca del tracking óptico, dedos de los guantes — es probablemente superior a cualquiera de las dos fuentes por separado**, y conviene evaluarla explícitamente.

**H.3 — Retroalimentación de fuerza en la tarea.** Feedback al contactar un bloque y al sostenerlo. Es el uso que la propuesta describe: *"tactile feedback from haptic gloves enhances precision in virtual object manipulation"*.

**H.4 — Validación experimental.** Los guantes constituyen una de las configuraciones multisensoriales del Hito 2, y su evaluación corresponde a la Fase 5 de la vía de medición (abril–junio 2027).

### 9.3 Consideración sobre la vía de medición

Los guantes **no son compatibles con la tarea de Jenga físico en passthrough**: el participante necesita las manos libres para manipular bloques reales. Su validación experimental ocurre necesariamente en el entorno VR, lo que los ata al cumplimiento del quality gate de la Vía B.

Esto los convierte en la primera dependencia real entre ambas vías. Si la Vía B se atrasa, la configuración háptica del Hito 2 se atrasa con ella. Las otras configuraciones — visual, auditiva, interoceptiva — no tienen esa dependencia.

---

## 10. Fases de la Vía B

| Fase | Contenido | Duración | Ventana | Habilita |
|---|---|---|---|---|
| **B.0** | Los cuatro lint de configuración + hook de build | 3–4 días | Sep 2026 | Protección inmediata contra regresiones de config |
| **B.1** | Assembly definitions + arnés de test + primeros unitarios | 2 sem | Sep–Oct 2026 | Niveles 3 y 4 |
| **B.2** | Limpieza legacy (Bug 10) + `IPlayerInput` | 3–4 sem | Oct–Nov 2026 | B.3, H.1 |
| **B.3** | `SimulatedPlayer` + `ScenarioRunner` + invariantes | 4 sem | Nov–Dic 2026 | Diagnóstico del Bug 12 |
| **B.4** | Cliente espectador con cambio de perspectiva | 2–3 sem | Dic 2026 | Verificación del Bug 11 |
| **H.1–H.2** | Integración técnica de guantes | 4–6 sem | Dic 2026–Feb 2027 | H.3 |
| **B.5** | Tests de integridad de datos (nivel 4) | 3–4 días | Ene 2027 | Protección del pipeline de registro |
| **B.6** | Grabación y reproducción | 4 sem | Ene–Feb 2027 | Análisis de sesiones |
| **B.7** | **Quality gate** — criterio de entrada a sesiones VR | — | Mar 2027 | Fase C del roadmap |
| **H.3** | Retroalimentación de fuerza en la tarea | 3–4 sem | Mar–Abr 2027 | Fase 5 del roadmap |
| **B.8** | Paridad de features: AOI por bloque y feedback por turno en VR | 4 sem | Abr–May 2027 | Fase C del roadmap |

**B.0 es lo primero deliberadamente.** Son tres o cuatro días que no dependen de nada y que cubren cinco defectos ya vividos. Dan protección antes de que empiece cualquier refactor — lo cual importa, porque B.2 toca código que hoy funciona.

### Criterio del quality gate (B.7)

Definido por adelantado y no negociable en el momento:

- [ ] Poke y grab operativos en los tres roles, verificados por escenario automatizado
- [ ] Rayo remoto visible para los observadores
- [ ] Las cinco invariantes de la §7 se cumplen en `GRAB_CONFLICT` y `POKE_SEQUENCE`
- [ ] Una sesión de 20 minutos con tres clientes — al menos uno real — sin incidentes
- [ ] El CSV resultante pasa los tests de integridad de datos
- [ ] Los lint de configuración pasan en la build de la sesión

---

## 11. Relación con la vía de medición

Las dos vías corren en paralelo y se tocan en tres puntos:

| Punto | Naturaleza |
|---|---|
| Bugs 1 y 2 | Están en la ruta crítica de ambas. Se resuelven una vez, sirven a las dos. |
| Guantes hápticos | Su validación experimental (Fase 5 del roadmap) depende del quality gate de la Vía B. |
| Fase C — validación cruzada | Requiere que ambas vías hayan llegado a su punto de madurez. |

Fuera de esos tres puntos, **un atraso en la Vía B no detiene la recolección de datos**, que es el objetivo de todo el desacople.

---

## 12. Riesgos

**El refactor de `IPlayerInput` toca código que hoy funciona.** El `dev_log` documenta varios casos en que un cambio aparentemente inocuo rompió en silencio un sistema de física o de red. Mitigación: ejecutar B.0 y B.1 antes, de modo que existan lint y tests que detecten la regresión, y hacer el refactor archivo por archivo en vez de en un solo cambio.

**La simulación puede ocultar defectos específicos del hardware.** Un cliente simulado nunca pierde tracking, nunca tiene *jitter* y nunca se desconecta por interferencia. Mitigación: incorporar degradación deliberada a los escenarios — pérdida de tracking de manos durante 500 ms, latencia inyectada, pérdida de paquetes — y mantener las sesiones con hardware completo antes de cada hito.

**El modelo de guantes puede no tener SDK compatible con Unity 2022.3 LTS.** Solo se resuelve al recibirlos. Mitigación: verificar la compatibilidad apenas lleguen, antes de comprometer la configuración háptica en el calendario del Hito 2.

---

## 13. Referencias

* [`roadmap_experimental.md`](roadmap_experimental.md) — plan general y vía de medición
* [`architecture.md`](architecture.md) — mapa de subsistemas, flujos de datos y acoplamientos del sistema
* [`bug_dev.md`](bug_dev.md) — registro de defectos con causa raíz y prioridad
* [`test_protocol.md`](test_protocol.md) — protocolo de sesión de validación con hardware completo
* [`network_setup.md`](network_setup.md) — configuración de red del laboratorio
* [`dev_log.md`](dev_log.md) — historial técnico de desarrollo
