using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using XRCollab.Measurement.Recording;

namespace XRCollab.Measurement
{
    /// <summary>
    /// Diagnóstico de rendimiento sin recompilar: apaga componentes por nombre de tipo, 5 s después de arrancar (así
    /// todos alcanzaron a inicializar: passthrough, cámara, XR…; solo se corta su trabajo por cuadro).
    /// <c>adb shell am start -n cl.dictuc.xrcollab.measurement/com.unity3d.player.UnityPlayerActivity --es off "NetworkLauncher,PerformanceHUD"</c>
    /// (PC: <c>-off "A,B"</c>).
    ///  - <c>off list</c>: solo escribe en el log los tipos activos (en trozos, logcat corta líneas largas).
    ///  - <c>off all --es keep "A,B"</c>: apaga todos los scripts del proyecto (no los de Unity/paquetes) salvo los de keep.
    ///  - En el visor (Android), sin <c>off</c>, se apaga <see cref="AndroidDefault"/>; <c>--es off none</c> deja todo.
    /// </summary>
    public sealed class LaunchDisable : MonoBehaviour
    {
        private const float Delay = 5f;

        /// <summary>
        /// Lo que en el visor nativo no sirve: depuración/HUD del PC y la locomoción de XRI (girar, teletransportarse,
        /// trepar, moverse con controles), que en passthrough solo correría el rig respecto de la sala real.
        /// </summary>
        public const string AndroidDefault =
            "AvatarHandJointDebugViz,AvatarHandTunerHUD,BuildAuditHUD,BuildFlyCamera,PerformanceHUD,PinchDebugVisualizer,TrackerVisualizer," +
            "ActionBasedContinuousTurnProvider,ActionBasedSnapTurnProvider,ClimbProvider,DynamicMoveProvider,TeleportationProvider,ClimbTeleportDestinationIndicator";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            string arg = LaunchArgs.Get("-off");
#if UNITY_ANDROID && !UNITY_EDITOR
            if (string.IsNullOrWhiteSpace(arg)) arg = AndroidDefault;
#endif
            if (string.IsNullOrWhiteSpace(arg) || arg.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return;
            var go = new GameObject("[LaunchDisable]");
            DontDestroyOnLoad(go);
            go.AddComponent<LaunchDisable>().StartCoroutine(Run(arg.Trim(), LaunchArgs.Get("-keep") ?? ""));
        }

        private static HashSet<string> Names(string csv) =>
            new HashSet<string>(csv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);

        // Scripts del proyecto: sin namespace de Unity, XR Interaction Toolkit, TextMeshPro, VIVE…
        private static bool IsProjectScript(Behaviour b)
        {
            if (!(b is MonoBehaviour) || b is LaunchDisable) return false;
            string ns = b.GetType().Namespace ?? "";
            return !(ns.StartsWith("UnityEngine") || ns.StartsWith("Unity.") || ns.StartsWith("TMPro") || ns.StartsWith("VIVE"));
        }

        private static IEnumerator Run(string arg, string keepCsv)
        {
            yield return new WaitForSeconds(Delay);
            if (arg.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                List<string> types = FindObjectsOfType<Behaviour>()
                    .Where(b => b.isActiveAndEnabled)
                    .GroupBy(b => b.GetType().FullName)
                    .OrderBy(g => g.Key)
                    .Select(g => $"{g.Key}x{g.Count()}")
                    .ToList();
                for (int i = 0; i < types.Count; i += 25)
                    Debug.Log($"[LaunchDisable] activos {i}: " + string.Join(",", types.Skip(i).Take(25)));
                yield break;
            }
            bool all = arg.Equals("all", StringComparison.OrdinalIgnoreCase);
            HashSet<string> names = Names(arg), keep = Names(keepCsv);
            var done = new HashSet<string>();
            for (int pass = 0; pass < 10; pass++)
            {
                foreach (Behaviour b in FindObjectsOfType<Behaviour>())
                {
                    if (!b.enabled) continue;
                    string n = b.GetType().Name;
                    bool hit = all ? IsProjectScript(b) && !keep.Contains(n) : names.Contains(n);
                    if (!hit) continue;
                    b.enabled = false;
                    if (b is MonoBehaviour mb) mb.StopAllCoroutines();   // p. ej. el reintento de conexión de NetworkLauncher
                    done.Add(n);
                }
                yield return new WaitForSeconds(1f);
            }
            List<string> list = done.OrderBy(x => x).ToList();
            for (int i = 0; i < list.Count; i += 25)
                Debug.Log($"[LaunchDisable] apagados {i}: " + string.Join(",", list.Skip(i).Take(25)));
        }
    }
}
