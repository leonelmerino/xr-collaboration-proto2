# Integración de la base DILAB en main — 2026-09-22

## Alcance y referencia

Integrar los diez commits de `fixes-ui-new` sin squash ni rebase. La referencia
de partida es `dc9c506a6c1d7206367083adf97d6637afed8211`; el `main` anterior es
`f0d32289304ff9a554f0ad0307e87331b0329922`. No había commits exclusivos de main.
Punto de recuperación: tag `baseline/main-before-xr-20260922`.

Los cambios adicionales a esa base son exclusivamente documentación. Se conservan
los scripts, prefabs, escena, parámetros de física, paquetes y versión de Unity.
La finalidad es establecer una base común; no cerrar los bugs del experimento.

## Validación realizada

- Editor nativo Windows: Unity **2022.3.62f3**, changeset `96770f904ca7`.
- Build Windows x64 de `Assets/Scenes/Room.unity`: **Success**, salida 0.
  El build de producción no contiene el código temporal de prueba.
- Auditoría mediante APIs del Editor: **0 scripts faltantes** en Room,
  AvatarHumanoid y JengaBlock; las seis cadenas IK tienen root/mid/tip/target
  asignados y sus targets corresponden a la mano izquierda/derecha.
- Los dos interactores de poke mantienen los valores serializados de la escena:
  fuerza `0.1`, cooldown `0.05`. No se sustituyeron por los defaults del script.
- Prueba instrumentada en una compilación separada: tres procesos nativos,
  Host/Client/Helper, transporte real NGO/UTP por `127.0.0.1:17777` y gráficos
  Direct3D. Conectaron los clientes 0/1/2 y se replicaron los 12 bloques.
- Cada rol solicitó empuje mediante `RequestPush`, tomó y movió un bloque y lo
  soltó. Los dos empujes remotos aparecen aplicados en el log del servidor.
  Los tres peers observaron el retorno de ownership al servidor. Los tres
  procesos finalizaron con código 0 para estas comprobaciones de conectividad
  y ownership. Esto no certifica la precisión de movimiento.

La prueba temporal usó un punto de pinch sintético y llamadas a las APIs de
producción; no ejercitó detección de dedos, raycast físico, teclado H/C ni LAN
discovery. Los scripts de prueba y logs permanecen como artefactos locales fuera
del repositorio, en `outputs/xr-main-integration-20260922` del workspace padre.
La ejecución final es `smoke-signals-3` con logs `host-3.log`, `client-3.log` y
`helper-3.log`. El primer intento se interrumpió por asumir 18 bloques en vez de
leer los 4 niveles de la escena; el segundo detectó la discrepancia cinemática
descrita abajo. No se contabilizan como ejecuciones satisfactorias.

## Fallos y límites que permanecen abiertos

1. **Bug 12 reproducido, no corregido.** Con un desplazamiento sintético de mano
   de 0.025 m, los desplazamientos del bloque fueron Host `0.02499958` m,
   Client `0.2597605` m y Helper `0.2831124` m. La conectividad y el ciclo de
   ownership funcionan, pero la precisión de movimiento en clientes falla.
2. **Discrepancia código/prefab:** JengaBlock sí contiene `NetworkRigidbody`
   (GUID `f6c0be61502bb534f922ebb746851216`). Los comentarios de
   `NetworkedJengaBlock` que afirman lo contrario no describen el prefab real.
   Se observó `isKinematic=false` durante el agarre en los tres roles. Investigar
   el orden de callbacks y la interacción entre ambos componentes dentro del
   diagnóstico del Bug 12; esta observación no demuestra por sí sola su causa.
3. **Excepción del visualizador de depuración:** `AvatarHandJointDebugViz.Awake`
   llega a `new Material(Shader.Find("Unlit/Color"))` con shader nulo en el
   player; el fallback posterior es demasiado tarde. El archivo del visualizador
   y JengaBlock son idénticos entre el main anterior y fixes-ui-new. Esta
   integración no introduce una corrección de ese comportamiento preexistente.
4. Persisten advertencias de compilación previas, incluyendo OnDestroy que oculta
   el método de NetworkBehaviour y código de depuración inalcanzable. Los builds
   terminaron correctamente; no deben describirse como libres de advertencias.
5. **No ejecutado:** validación física DILAB con tres visores, trackers, mirada,
   calibración, LAN entre estaciones, precisión del poke y percepción visual del
   brazo. Seguir `test_protocol.md` y conservar los logs de cada estación.

## Uso y verificación después de integrar

Con el árbol limpio, actualizar main sin reescribir historia:

```bash
git fetch origin
git switch main
git pull --ff-only origin main
git merge-base --is-ancestor dc9c506 origin/main
git log origin/main..origin/fixes-ui-new
```

La comprobación de ascendencia debe devolver 0 y el log debe quedar vacío.
Comparar también el commit ejecutado en las tres estaciones. Las ramas nuevas
de registro con passthrough e infraestructura de testeo parten del main integrado.

Para investigar o reproducir el estado anterior sin modificar main, crear una
rama separada desde `baseline/main-before-xr-20260922`. Si se requiere revertir
la integración compartida, hacerlo mediante un nuevo commit de revert revisado;
no resetear ni forzar el push de main.
