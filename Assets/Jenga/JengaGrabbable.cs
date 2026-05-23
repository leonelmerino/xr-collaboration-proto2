using UnityEngine;

/// <summary>
/// Logica de grab local de un bloque Jenga. El bloque sigue la pose de un "pinch transform"
/// de la mano via Rigidbody.MovePosition con un Lerp configurable (soft follow).
///
/// Mejoras introducidas con el tuner de fisica:
/// - Soft follow factor (grabPositionLerp) ajustable en runtime.
/// - Preservacion de velocidad al soltar: si se activa, al EndGrab se estima la velocidad
///   de los ultimos N samples y se setea sobre el Rigidbody. Esto permite "sacudir" un
///   bloque y que conserve momentum, en vez de caer en linea recta.
///
/// Sigue siendo compatible con el flujo de NGO via NetworkedJengaBlock (que es el que
/// invoca BeginGrab/EndGrab segun el cambio de ownership).
/// </summary>
public class JengaGrabbable : MonoBehaviour
{
    // Tuning (modificable en runtime por JengaPhysicsRuntime).
    private float grabPositionLerp = 0.35f;
    private bool preserveVelocityOnRelease = true;
    private int releaseVelocitySamples = 3;
    private float releaseVelocityMax = 3f;

    private Rigidbody rb;
    private bool isGrabbed = false;
    private Transform grabPoint;

    private Vector3 initialGrabOffset;
    private Vector3 allowedAxisWorld;

    // Ring buffer de samples para estimar velocidad al soltar.
    // 8 slots es mas que suficiente para releaseVelocitySamples = 1..6.
    private const int BufSize = 8;
    private readonly Vector3[] _posBuf = new Vector3[BufSize];
    private readonly float[] _timeBuf = new float[BufSize];
    private int _bufHead;  // proximo slot a escribir
    private int _bufCount; // cuantos slots validos hay (max BufSize)

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    public bool IsGrabbed() => isGrabbed;

    /// <summary>
    /// Llamado por JengaPhysicsRuntime cuando carga una config. Permite que el tuner
    /// modifique el comportamiento del grab sin tocar el prefab.
    /// </summary>
    public void SetTuning(float positionLerp, bool preserveVelocity, int velocitySamples, float velocityMax)
    {
        grabPositionLerp = Mathf.Clamp(positionLerp, 0.01f, 1f);
        preserveVelocityOnRelease = preserveVelocity;
        releaseVelocitySamples = Mathf.Clamp(velocitySamples, 1, BufSize - 1);
        releaseVelocityMax = Mathf.Max(0f, velocityMax);
    }

    public void BeginGrab(Transform pinchTransform)
    {
        if (rb == null) return;

        isGrabbed = true;
        grabPoint = pinchTransform;

        initialGrabOffset = transform.position - grabPoint.position;
        allowedAxisWorld = transform.right.normalized;

        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.constraints = RigidbodyConstraints.FreezeRotation;

        // Reset del buffer para no contaminar con samples del grab anterior.
        _bufHead = 0;
        _bufCount = 0;
    }

    public void EndGrab()
    {
        if (rb == null) return;

        // Estimar velocidad ANTES de unfreeze + clear grabPoint.
        Vector3 estimatedVel = Vector3.zero;
        if (preserveVelocityOnRelease && _bufCount >= 2)
        {
            // Usamos los ultimos N+1 samples (N intervalos de dt).
            int n = Mathf.Min(_bufCount, releaseVelocitySamples + 1);
            int newest = (_bufHead - 1 + BufSize) % BufSize;
            int oldest = (_bufHead - n + BufSize) % BufSize;
            float dt = _timeBuf[newest] - _timeBuf[oldest];
            if (dt > 1e-6f)
            {
                estimatedVel = (_posBuf[newest] - _posBuf[oldest]) / dt;
                // Clamp para que jitter de hand tracking no se traduzca en velocidades absurdas.
                if (releaseVelocityMax > 0f && estimatedVel.magnitude > releaseVelocityMax)
                    estimatedVel = estimatedVel.normalized * releaseVelocityMax;
            }
        }

        isGrabbed = false;
        grabPoint = null;
        rb.constraints = RigidbodyConstraints.None;

        if (preserveVelocityOnRelease)
        {
            rb.velocity = estimatedVel;
            // angularVelocity intencionalmente queda en cero: durante grab teniamos FreezeRotation,
            // asi que no hay rotacion historica que conservar. Mantenerlo en 0 evita spins extraños.
        }

        _bufHead = 0;
        _bufCount = 0;
    }

    void FixedUpdate()
    {
        if (!isGrabbed || grabPoint == null || rb == null) return;

        Vector3 desired = grabPoint.position + initialGrabOffset;
        Vector3 delta = desired - transform.position;

        Vector3 constrainedDelta = Vector3.Project(delta, allowedAxisWorld);
        Vector3 target = transform.position + constrainedDelta;

        Vector3 newPos = Vector3.Lerp(rb.position, target, grabPositionLerp);
        rb.MovePosition(newPos);

        // Registrar sample para estimacion de velocidad de release.
        // Usamos la posicion comandada (newPos) en vez de transform.position, asi reflejamos
        // la intencion del usuario incluso si la fisica esta resistiendose contra otro bloque.
        _posBuf[_bufHead] = newPos;
        _timeBuf[_bufHead] = Time.fixedTime;
        _bufHead = (_bufHead + 1) % BufSize;
        if (_bufCount < BufSize) _bufCount++;
    }
}
