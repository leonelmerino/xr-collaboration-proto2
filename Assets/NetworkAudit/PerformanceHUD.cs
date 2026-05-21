using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// Overlay con metricas de rendimiento en tiempo real:
/// - FPS instantaneo y promedio (1 s).
/// - Frametime actual y p95 sobre ventana deslizante (1 s).
/// - Memoria managed (Mono).
/// - Alloc/frame.
/// - NGO RTT al host (si es cliente).
/// - Estado del HandRayDriver (modo + smoothing).
/// - Estado del NetworkClockSync (offset al host).
///
/// Se pinta en la esquina superior derecha para no chocar con BuildAuditHUD (top-left).
/// </summary>
public class PerformanceHUD : MonoBehaviour
{
    [SerializeField] private bool showInEditor = true;
    [SerializeField] private bool showInBuild = true;
    [SerializeField] private int fontSize = 13;
    [SerializeField] private Vector2 margin = new Vector2(12f, 12f);
    [Tooltip("Ancho fijo del overlay en pixeles. Evita que la caja cambie de tamano cuando varian los numeros.")]
    [SerializeField] private float fixedWidth = 360f;
    [SerializeField, Range(0.2f, 5f)] private float sampleWindowSec = 1f;
    [Tooltip("Toggle visible-no visible en runtime con esta tecla.")]
    [SerializeField] private KeyCode toggleKey = KeyCode.F3;

    private readonly Queue<float> frameTimes = new();
    private GUIStyle style;

    private float fpsSmoothed;
    private float p95Ms;
    private float p99Ms;
    private long lastMonoUsed;
    private long allocBytesLastFrame;
    private float p95RecomputeAt;

    private bool visible = true;

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            visible = !visible;

        float dt = Time.unscaledDeltaTime;
        if (dt <= 0f) return;

        frameTimes.Enqueue(dt);

        // Drenar samples mas viejos que el window.
        float maxSamples = sampleWindowSec / Mathf.Max(dt, 1e-6f);
        while (frameTimes.Count > maxSamples)
            frameTimes.Dequeue();

        // FPS con suavizado exponencial (constante de tiempo ~0.5 s).
        float alpha = 1f - Mathf.Exp(-dt / 0.5f);
        float instFps = 1f / dt;
        fpsSmoothed = fpsSmoothed <= 0f ? instFps : Mathf.Lerp(fpsSmoothed, instFps, alpha);

        // p95 / p99 cada 250 ms para no sortear cada frame.
        float now = Time.realtimeSinceStartup;
        if (now >= p95RecomputeAt && frameTimes.Count > 4)
        {
            var arr = frameTimes.ToArray();
            System.Array.Sort(arr);
            int i95 = Mathf.Clamp(Mathf.RoundToInt(arr.Length * 0.95f) - 1, 0, arr.Length - 1);
            int i99 = Mathf.Clamp(Mathf.RoundToInt(arr.Length * 0.99f) - 1, 0, arr.Length - 1);
            p95Ms = arr[i95] * 1000f;
            p99Ms = arr[i99] * 1000f;
            p95RecomputeAt = now + 0.25f;
        }

        // Memoria managed.
        long monoUsed = Profiler.GetMonoUsedSizeLong();
        allocBytesLastFrame = System.Math.Max(0, monoUsed - lastMonoUsed);
        lastMonoUsed = monoUsed;
    }

    private void OnGUI()
    {
        if (!visible) return;
#if UNITY_EDITOR
        if (!showInEditor) return;
#else
        if (!showInBuild) return;
#endif

        if (style == null)
        {
            style = new GUIStyle(GUI.skin.box);
            style.alignment = TextAnchor.UpperLeft;
            style.fontSize = fontSize;
            style.normal.textColor = Color.white;
            style.padding = new RectOffset(10, 10, 8, 8);
        }

        var sb = new StringBuilder();
        sb.AppendLine("== PERF HUD (F3) ==");
        sb.AppendLine($"FPS: {fpsSmoothed:F1}  (inst {1f / Mathf.Max(Time.unscaledDeltaTime, 1e-6f):F0})");
        sb.AppendLine($"Frame ms: now={Time.unscaledDeltaTime * 1000f:F1}");
        sb.AppendLine($"           p95={p95Ms:F1}  p99={p99Ms:F1}");
        sb.AppendLine($"Mono used: {lastMonoUsed / 1048576f:F1} MB");
        sb.AppendLine($"Alloc/frame: {allocBytesLastFrame / 1024f:F2} KB");

        // NGO RTT y estado de red.
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening)
        {
            if (nm.IsHost)
                sb.AppendLine($"NGO: HOST | clients={nm.ConnectedClients.Count}");
            else if (nm.IsServer)
                sb.AppendLine($"NGO: SERVER | clients={nm.ConnectedClients.Count}");
            else if (nm.IsClient)
            {
                ulong rtt = 0;
                var transport = nm.NetworkConfig?.NetworkTransport;
                if (transport != null)
                {
                    try { rtt = transport.GetCurrentRtt(NetworkManager.ServerClientId); } catch { }
                }
                sb.AppendLine($"NGO: CLIENT (id={nm.LocalClientId})  RTT to host: {rtt} ms");
            }
        }
        else
        {
            sb.AppendLine("NGO: idle");
        }

        // Hand Ray Driver status.
        var hrd = HandRayDriver.Instance;
        if (hrd != null)
        {
            string left = hrd.LeftActiveThisFrame ? "ON" : "off";
            string right = hrd.RightActiveThisFrame ? "ON" : "off";
            sb.AppendLine($"HandRay: meta-shoulder  smooth={hrd.RotationSmoothK:F2}");
            sb.AppendLine($"  L={left}  R={right}");
        }

        // Clock sync.
        var clk = NetworkClockSync.Instance;
        if (clk != null && clk.IsSynced)
        {
            sb.AppendLine($"ClockSync: offset={clk.OffsetToHost * 1000f:F2} ms");
        }

        string text = sb.ToString();
        float height = style.CalcHeight(new GUIContent(text), fixedWidth);

        float x = Screen.width - fixedWidth - margin.x;
        float y = margin.y;
        Rect rect = new Rect(x, y, fixedWidth, height + 16f);

        GUI.color = new Color(0f, 0f, 0f, 0.7f);
        GUI.Box(rect, GUIContent.none);
        GUI.color = Color.white;
        GUI.Label(rect, text, style);
    }
}
