using System.Text;
using UnityEngine;

/// <summary>
/// HUD interactivo para tunear los offsets Euler de muñeca del avatar humanoide en runtime.
/// La idea: en vez de calibrar (incomodo de mantener la pose y apretar tecla a la vez), el
/// usuario ajusta los Euler con teclado y ve el efecto en vivo en el avatar remoto.
///
/// Los valores se escriben a NetworkedAvatarPose.LeftWristOffsetEuler / RightWristOffsetEuler
/// (sincronizados), asi todos los viewers ven la misma correccion.
///
/// SETUP:
/// - Agregar este componente a CUALQUIER GameObject de la escena (ej: NetworkManager).
/// - El script busca solito el AvatarPoseDriver del owner local en runtime.
/// - Se ve siempre en OnGUI (monitor). Para verlo dentro del VR, asignar `worldSpacePanel` a
///   un Canvas world-space con un TMP_Text adentro (auto-posiciona frente al HMD).
///
/// CONTROLES (default):
/// - F4: toggle visibilidad del HUD.
/// - 1: seleccionar muñeca izquierda. 2: derecha.
/// - Q/A: X +/-, W/S: Y +/-, E/D: Z +/-.
/// - R: reset el wrist seleccionado a (0,0,0).
/// - Shift (hold): step grande (5x). Ctrl (hold): step chico (0.2x).
/// - C: dispara CalibrateHands() en NetworkedAvatarPose (la calibracion existente).
/// - V: limpia la calibracion (vuelve a identity).
/// </summary>
public class AvatarHandTunerHUD : MonoBehaviour
{
    public enum Wrist { Left, Right }

    [Header("Display")]
    [Tooltip("Si esta asignado, se muestra el texto del HUD ademas en este Canvas world-space. " +
             "Util para verlo dentro del VR. Si esta vacio, solo OnGUI (monitor).")]
    [SerializeField] private TMPro.TMP_Text worldSpacePanel;

    [Tooltip("Si worldSpacePanel esta asignado, el HUD se auto-posiciona delante de esta camara. " +
             "Default: Camera.main.")]
    [SerializeField] private Camera followCamera;

    [Tooltip("Distancia del HUD world-space respecto de la camara.")]
    [SerializeField] private float worldSpaceDistance = 1.2f;

    [Tooltip("Posicion vertical (Y) relativa al forward de la camara. Negativo = abajo.")]
    [SerializeField] private float worldSpaceVerticalOffset = -0.3f;

    [Header("Controls")]
    [SerializeField] private KeyCode toggleKey = KeyCode.F4;
    [SerializeField] private KeyCode resetKey = KeyCode.R;
    [SerializeField] private KeyCode selectLeftKey = KeyCode.Alpha1;
    [SerializeField] private KeyCode selectRightKey = KeyCode.Alpha2;
    [SerializeField] private KeyCode xPosKey = KeyCode.Q;
    [SerializeField] private KeyCode xNegKey = KeyCode.A;
    [SerializeField] private KeyCode yPosKey = KeyCode.W;
    [SerializeField] private KeyCode yNegKey = KeyCode.S;
    [SerializeField] private KeyCode zPosKey = KeyCode.E;
    [SerializeField] private KeyCode zNegKey = KeyCode.D;
    [SerializeField] private KeyCode clearCalibrationKey = KeyCode.V;
    [SerializeField] private KeyCode saveKey = KeyCode.Y;
    [SerializeField] private KeyCode loadKey = KeyCode.U;
    [SerializeField] private KeyCode exportKey = KeyCode.X;

    private const string PrefsKeyLeftX = "AvatarHandTuner.Left.X";
    private const string PrefsKeyLeftY = "AvatarHandTuner.Left.Y";
    private const string PrefsKeyLeftZ = "AvatarHandTuner.Left.Z";
    private const string PrefsKeyRightX = "AvatarHandTuner.Right.X";
    private const string PrefsKeyRightY = "AvatarHandTuner.Right.Y";
    private const string PrefsKeyRightZ = "AvatarHandTuner.Right.Z";

    private bool _prefsLoaded;

    [Header("Step sizes (degrees)")]
    [SerializeField] private float stepNormal = 5f;
    [SerializeField] private float stepBig = 25f;
    [SerializeField] private float stepSmall = 1f;

    [Header("OnGUI overlay")]
    [SerializeField] private bool showOnGUI = true;
    [SerializeField] private Vector2 hudScreenPos = new Vector2(10, 200);
    [SerializeField] private Vector2 hudSize = new Vector2(380, 280);

    private bool _visible = true;
    private Wrist _selected = Wrist.Left;
    private NetworkedAvatarPose _ownerPose;
    private string _statusMsg = "";

    private void Start()
    {
        if (followCamera == null) followCamera = Camera.main;
        if (worldSpacePanel != null)
        {
            // Asegurar que el canvas no se rinderee hasta que tengamos texto.
            worldSpacePanel.text = "";
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            _visible = !_visible;
            if (worldSpacePanel != null)
                worldSpacePanel.gameObject.SetActive(_visible);
        }

        // Refrescar referencia al owner cada cierto tiempo (por si todavia no spawneo).
        if (_ownerPose == null || !_ownerPose.IsSpawned || !_ownerPose.IsOwner)
        {
            TryFindOwnerPose();
        }

        // Posicionar panel world-space frente al HMD.
        UpdateWorldPanelPose();

        if (!_visible) return;
        if (_ownerPose == null || !_ownerPose.IsSpawned) return;
        if (!_ownerPose.IsOwner) return; // Solo el owner puede escribir al NetworkVariable.

        // Auto-cargar offsets guardados la primera vez que tenemos al owner spawneado.
        if (!_prefsLoaded)
        {
            LoadFromPrefs();
            _prefsLoaded = true;
        }

        HandleInput();
        RefreshDisplay();
    }

    private void LoadFromPrefs()
    {
        if (!PlayerPrefs.HasKey(PrefsKeyLeftX))
        {
            _statusMsg = "No saved offsets found in PlayerPrefs.";
            return;
        }
        Vector3 l = new Vector3(
            PlayerPrefs.GetFloat(PrefsKeyLeftX),
            PlayerPrefs.GetFloat(PrefsKeyLeftY),
            PlayerPrefs.GetFloat(PrefsKeyLeftZ));
        Vector3 r = new Vector3(
            PlayerPrefs.GetFloat(PrefsKeyRightX),
            PlayerPrefs.GetFloat(PrefsKeyRightY),
            PlayerPrefs.GetFloat(PrefsKeyRightZ));
        _ownerPose.LeftWristOffsetEuler.Value = l;
        _ownerPose.RightWristOffsetEuler.Value = r;
        Debug.Log($"[AvatarHandTunerHUD] Loaded saved offsets from PlayerPrefs: " +
                  $"L=({l.x:F1},{l.y:F1},{l.z:F1}) R=({r.x:F1},{r.y:F1},{r.z:F1})");
        _statusMsg = "Loaded saved offsets.";
    }

    private void SaveToPrefs()
    {
        Vector3 l = _ownerPose.LeftWristOffsetEuler.Value;
        Vector3 r = _ownerPose.RightWristOffsetEuler.Value;
        PlayerPrefs.SetFloat(PrefsKeyLeftX, l.x);
        PlayerPrefs.SetFloat(PrefsKeyLeftY, l.y);
        PlayerPrefs.SetFloat(PrefsKeyLeftZ, l.z);
        PlayerPrefs.SetFloat(PrefsKeyRightX, r.x);
        PlayerPrefs.SetFloat(PrefsKeyRightY, r.y);
        PlayerPrefs.SetFloat(PrefsKeyRightZ, r.z);
        PlayerPrefs.Save();
        Debug.Log($"[AvatarHandTunerHUD] SAVED offsets to PlayerPrefs: " +
                  $"L=({l.x:F1},{l.y:F1},{l.z:F1}) R=({r.x:F1},{r.y:F1},{r.z:F1})");
        _statusMsg = "SAVED to PlayerPrefs.";
    }

    private void ExportToConsole()
    {
        Vector3 l = _ownerPose.LeftWristOffsetEuler.Value;
        Vector3 r = _ownerPose.RightWristOffsetEuler.Value;
        Debug.Log("[AvatarHandTunerHUD] === EXPORT === Copy these into AvatarPoseDriver " +
                  "Inspector on AvatarHumanoid.prefab (cada uno de los 3 sub-meshes):\n" +
                  $"  Left Wrist Rotation Offset Euler:  X={l.x:F1}  Y={l.y:F1}  Z={l.z:F1}\n" +
                  $"  Right Wrist Rotation Offset Euler: X={r.x:F1}  Y={r.y:F1}  Z={r.z:F1}");
        _statusMsg = "EXPORTED to Console (read log).";
    }

    private void TryFindOwnerPose()
    {
        var allPoses = FindObjectsOfType<NetworkedAvatarPose>();
        for (int i = 0; i < allPoses.Length; i++)
        {
            if (allPoses[i].IsSpawned && allPoses[i].IsOwner)
            {
                _ownerPose = allPoses[i];
                return;
            }
        }
    }

    private void HandleInput()
    {
        // Cambiar muñeca seleccionada.
        if (Input.GetKeyDown(selectLeftKey)) { _selected = Wrist.Left; _statusMsg = "Selected: LEFT"; }
        if (Input.GetKeyDown(selectRightKey)) { _selected = Wrist.Right; _statusMsg = "Selected: RIGHT"; }

        // Calcular step.
        float step = stepNormal;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) step = stepBig;
        else if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) step = stepSmall;

        // Acumular delta este frame.
        Vector3 delta = Vector3.zero;
        if (Input.GetKeyDown(xPosKey)) delta.x += step;
        if (Input.GetKeyDown(xNegKey)) delta.x -= step;
        if (Input.GetKeyDown(yPosKey)) delta.y += step;
        if (Input.GetKeyDown(yNegKey)) delta.y -= step;
        if (Input.GetKeyDown(zPosKey)) delta.z += step;
        if (Input.GetKeyDown(zNegKey)) delta.z -= step;

        if (delta != Vector3.zero)
        {
            ApplyDelta(delta);
            _statusMsg = $"{_selected} += ({delta.x:+0.0;-0.0;0}, {delta.y:+0.0;-0.0;0}, {delta.z:+0.0;-0.0;0})";
        }

        // Reset.
        if (Input.GetKeyDown(resetKey))
        {
            if (_selected == Wrist.Left) _ownerPose.LeftWristOffsetEuler.Value = Vector3.zero;
            else _ownerPose.RightWristOffsetEuler.Value = Vector3.zero;
            _statusMsg = $"{_selected} RESET to (0,0,0)";
        }

        // Limpiar calibracion (vuelve a identity, asi solo cuenta el offset Euler).
        if (Input.GetKeyDown(clearCalibrationKey))
        {
            _ownerPose.LeftWristCalibration.Value = Quaternion.identity;
            _ownerPose.RightWristCalibration.Value = Quaternion.identity;
            _statusMsg = "Calibration CLEARED (both wrists)";
        }

        // Save / Load / Export persistencia.
        if (Input.GetKeyDown(saveKey))
            SaveToPrefs();
        if (Input.GetKeyDown(loadKey))
            LoadFromPrefs();
        if (Input.GetKeyDown(exportKey))
            ExportToConsole();
    }

    private void ApplyDelta(Vector3 delta)
    {
        if (_selected == Wrist.Left)
        {
            _ownerPose.LeftWristOffsetEuler.Value = _ownerPose.LeftWristOffsetEuler.Value + delta;
        }
        else
        {
            _ownerPose.RightWristOffsetEuler.Value = _ownerPose.RightWristOffsetEuler.Value + delta;
        }
    }

    private void UpdateWorldPanelPose()
    {
        if (worldSpacePanel == null || followCamera == null) return;
        var canvasTr = worldSpacePanel.transform.root;
        // Posicionar delante del HMD.
        Vector3 forward = followCamera.transform.forward;
        Vector3 up = followCamera.transform.up;
        Vector3 pos = followCamera.transform.position + forward * worldSpaceDistance +
                      up * worldSpaceVerticalOffset;
        canvasTr.position = pos;
        canvasTr.rotation = Quaternion.LookRotation(forward, up);
    }

    private void RefreshDisplay()
    {
        if (worldSpacePanel != null)
        {
            worldSpacePanel.text = BuildHudText();
        }
    }

    private string BuildHudText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== AVATAR HAND TUNER ===");
        if (_ownerPose == null || !_ownerPose.IsSpawned)
        {
            sb.AppendLine("(waiting for owner avatar spawn...)");
            return sb.ToString();
        }

        Vector3 l = _ownerPose.LeftWristOffsetEuler.Value;
        Vector3 r = _ownerPose.RightWristOffsetEuler.Value;
        sb.AppendLine($"Selected: {(_selected == Wrist.Left ? "[LEFT]" : "[RIGHT]")}");
        sb.AppendLine($"Left  offset: ({l.x:F1}, {l.y:F1}, {l.z:F1})");
        sb.AppendLine($"Right offset: ({r.x:F1}, {r.y:F1}, {r.z:F1})");
        sb.AppendLine();
        sb.AppendLine("Controls:");
        sb.AppendLine($"  {selectLeftKey}/{selectRightKey}: select wrist");
        sb.AppendLine($"  {xPosKey}/{xNegKey}: X +/-   {yPosKey}/{yNegKey}: Y +/-   {zPosKey}/{zNegKey}: Z +/-");
        sb.AppendLine($"  Shift = big step ({stepBig}°), Ctrl = small ({stepSmall}°)");
        sb.AppendLine($"  {resetKey}: reset selected   {clearCalibrationKey}: clear calibration");
        sb.AppendLine($"  {saveKey}: SAVE to PlayerPrefs   {loadKey}: LOAD from PlayerPrefs");
        sb.AppendLine($"  {exportKey}: EXPORT values to Console (paste in prefab Inspector)");
        sb.AppendLine($"  {toggleKey}: toggle HUD");
        if (!string.IsNullOrEmpty(_statusMsg))
        {
            sb.AppendLine();
            sb.AppendLine($"> {_statusMsg}");
        }
        return sb.ToString();
    }

    private void OnGUI()
    {
        if (!showOnGUI || !_visible) return;

        var rect = new Rect(hudScreenPos.x, hudScreenPos.y, hudSize.x, hudSize.y);
        GUI.Box(rect, GUIContent.none);
        var inner = new Rect(rect.x + 8, rect.y + 6, rect.width - 16, rect.height - 12);
        GUILayout.BeginArea(inner);
        GUILayout.Label(BuildHudText());
        GUILayout.EndArea();
    }
}
