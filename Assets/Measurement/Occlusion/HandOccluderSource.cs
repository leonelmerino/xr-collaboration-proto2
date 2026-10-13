using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Hands;

namespace XRCollab.Measurement.Occlusion
{
    /// <summary>
    /// Manos y antebrazos reales como oclusores.
    ///
    /// <b>Mano:</b> la malla con piel del ejemplo «Hand Visualizer» de XR Hands (Resources/Occlusion/Hands), con cada
    /// hueso puesto en la pose de su articulación (26 de XR_EXT_hand_tracking): la silueta sigue la forma real de la mano
    /// (palma, grosor de cada dedo) en vez de cápsulas. Si faltan los modelos, cápsulas por hueso como antes.
    /// <b>Antebrazo:</b> de la muñeca al codo estimado (hombro desde la cabeza + brazo de dos huesos), ver
    /// <see cref="ForearmDirection"/>.
    ///
    /// <b>Tiempo:</b> las poses se toman en el callback de XR Hands del momento BeforeRender (la más reciente posible,
    /// como XRHandSkeletonDriver) y se guarda una historia corta. <see cref="OffsetMs"/> corre la pose en el tiempo
    /// para que el recorte calce con el passthrough, que llega al visor con otra latencia que lo renderizado en el PC:
    /// positivo = predice hacia adelante con la velocidad de cada articulación, negativo = usa poses de hace un rato.
    /// Se ajusta en vivo con «occlusion offset=MS» mirando «occlusion show» en un snapshot mientras la mano se mueve.
    ///
    /// Las poses de XR Hands están en el espacio de tracking: se pasan al mundo con el padre de la cámara (igual que
    /// la mirada en GazeOverlay y las manos en BodyTrackingSessionLogger).
    /// </summary>
    public sealed class HandOccluderSource : IOccluderSource, IDisposable
    {
        public const string LeftModel = "Occlusion/Hands/LeftHand";
        public const string RightModel = "Occlusion/Hands/RightHand";

        /// <summary>Desfase temporal de la pose usada (ms): + predice, - retrasa.</summary>
        public static float OffsetMs;

        /// <summary>Inflado extra según la rapidez de la mano (m por m/s): cubre lo que el desfase no alcanza a corregir
        /// cuando la mano se mueve rápido (recomendación de Meta para recortes de manos sin profundidad).</summary>
        public static float SpeedInflate = 0.015f;
        private const float MaxSpeedInflate = 0.012f;
        private static readonly int InflateId = Shader.PropertyToID("_Inflate");
        private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();

        public static float CapsuleInflate = 1.15f;
        private const float DefaultFingerRadius = 0.009f;
        private const float PalmRadius = 0.017f;
        private const float WristRadius = 0.029f;
        private const float ElbowRadius = 0.038f;
        private const float MaxPredictMeters = 0.08f;

        // Brazo de dos huesos desde un hombro estimado (como HandRayDriver): el tracking no da codo.
        private const float UpperArm = 0.30f;
        private const float ForearmLength = 0.27f;
        private const float ShoulderLateral = 0.18f;
        private const float ShoulderDown = 0.22f;
        private const float ShoulderBack = 0.06f;
        private const float MaxWristBendDegrees = 70f;   // la muñeca se dobla a lo más ~70° respecto del antebrazo

        private static readonly int JointCount = XRHandJointID.EndMarker.ToIndex();
        private static readonly XRHandJointID[][] Fingers =
        {
            new[] { XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip },
            new[] { XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip },
            new[] { XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip },
            new[] { XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip },
            new[] { XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip },
        };
        private static readonly XRHandJointID[] Knuckles =
            { XRHandJointID.IndexProximal, XRHandJointID.MiddleProximal, XRHandJointID.RingProximal, XRHandJointID.LittleProximal };

        /// <summary>Una pose de toda la mano en el espacio de tracking.</summary>
        private sealed class Sample
        {
            public double T;
            public readonly Pose[] Joints = new Pose[JointCount];
            public readonly bool[] Valid = new bool[JointCount];
            public readonly float[] Radius = new float[JointCount];
        }

        private sealed class Hand
        {
            public bool Left;
            public readonly Sample[] Ring = new Sample[48];   // ~0,5 s a 90 Hz
            public int Count, Head;                            // Head = la más reciente
            public readonly Sample Eval = new Sample();
            public bool Tracked;
            public GameObject Model;
            public SkinnedMeshRenderer Skin;
            public Transform[] Bones;                          // por índice de articulación (null = sin hueso)
            public MeshRenderer ForearmNear, ForearmFar;
            public float Speed;                                // rapidez de la muñeca (m/s), de la última evaluación
        }

        private readonly Hand[] _hands = { new Hand { Left = true }, new Hand { Left = false } };
        private readonly List<XRHandSubsystem> _list = new List<XRHandSubsystem>();
        private XRHandSubsystem _subsystem;
        private float _nextLookup;
        private Camera _cam;
        private int _version = -1;
        private bool _meshes;
        private GameObject _root;

        /// <summary>"mesh" (mallas con piel) o "capsules".</summary>
        public string Kind => _meshes ? "mesh" : "capsules";

        public HandOccluderSource()
        {
            foreach (Hand h in _hands) for (int i = 0; i < h.Ring.Length; i++) h.Ring[i] = new Sample();
            _root = new GameObject("[HandOccluders]");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _meshes = BuildModel(_hands[0], LeftModel, "L_") & BuildModel(_hands[1], RightModel, "R_");
            if (!_meshes) Debug.LogWarning("[Occluders] sin mallas de mano (Resources/" + LeftModel + "): se usan cápsulas");
            foreach (Hand h in _hands) BuildForearm(h);
        }

        public void Dispose()
        {
            if (_subsystem != null) _subsystem.updatedHands -= OnUpdatedHands;
            _subsystem = null;
            if (_root != null) UnityEngine.Object.Destroy(_root);
        }

        // ---------------- armado ----------------

        private bool BuildModel(Hand h, string resource, string prefix)
        {
            var prefab = Resources.Load<GameObject>(resource);
            if (prefab == null) return false;
            h.Model = UnityEngine.Object.Instantiate(prefab, _root.transform);
            h.Model.name = (h.Left ? "Left" : "Right") + "HandOccluder";
            var byName = new Dictionary<string, Transform>();
            foreach (Transform t in h.Model.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = Occluders.Layer;
                byName[t.name] = t;
            }
            h.Skin = h.Model.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (h.Skin == null) { UnityEngine.Object.Destroy(h.Model); h.Model = null; return false; }
            Setup(h.Skin);
            h.Skin.updateWhenOffscreen = true;
            h.Bones = new Transform[JointCount];
            int found = 0;
            for (int i = 0; i < JointCount; i++)
            {
                XRHandJointID id = XRHandJointIDUtility.FromIndex(i);
                if (byName.TryGetValue(prefix + id, out Transform b)) { h.Bones[i] = b; found++; }
            }
            if (found < 20) { UnityEngine.Object.Destroy(h.Model); h.Model = null; h.Skin = null; return false; }
            h.Model.SetActive(false);
            return true;
        }

        private void BuildForearm(Hand h)
        {
            h.ForearmNear = CapsuleRenderer((h.Left ? "Left" : "Right") + "ForearmNear");
            h.ForearmFar = CapsuleRenderer((h.Left ? "Left" : "Right") + "ForearmFar");
        }

        private MeshRenderer CapsuleRenderer(string name)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = name;
            go.layer = Occluders.Layer;
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_root.transform, false);
            var r = go.GetComponent<MeshRenderer>();
            Setup(r);
            go.SetActive(false);
            return r;
        }

        private static void Setup(Renderer r)
        {
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.allowOcclusionWhenDynamic = false;
            r.sharedMaterials = Array.Empty<Material>();   // nada visible hasta que Occluders asigne sus materiales
        }

        // ---------------- cada cuadro ----------------

        public void DrawOccluders(Occluders occ, Camera cam)
        {
            _cam = cam;
            if ((_subsystem == null || !_subsystem.running) && Time.unscaledTime >= _nextLookup)
            {
                _nextLookup = Time.unscaledTime + 2f;
                _list.Clear();
                SubsystemManager.GetSubsystems(_list);
                XRHandSubsystem next = null;
                foreach (XRHandSubsystem s in _list) { if (next == null || s.running) next = s; if (s.running) break; }
                if (next != _subsystem)
                {
                    if (_subsystem != null) _subsystem.updatedHands -= OnUpdatedHands;
                    _subsystem = next;
                    if (_subsystem != null) _subsystem.updatedHands += OnUpdatedHands;
                }
            }
            if (_version != Occluders.Version) ApplyMaterials(occ);
            bool on = _subsystem != null && _subsystem.running && occ.CurrentMaterials.Length > 0;
            if (!on) foreach (Hand h in _hands) Show(h, false);
            // Sin mallas: cápsulas inmediatas con la última pose evaluada (la del callback de este cuadro).
            if (!_meshes && on)
                foreach (Hand h in _hands) if (h.Tracked) DrawCapsules(occ, h.Eval, cam.transform.parent);
        }

        private void ApplyMaterials(Occluders occ)
        {
            _version = Occluders.Version;
            Material[] mats = occ.CurrentMaterials;
            foreach (Hand h in _hands)
            {
                if (h.Skin != null) h.Skin.sharedMaterials = mats;
                h.ForearmNear.sharedMaterials = mats;
                h.ForearmFar.sharedMaterials = mats;
            }
        }

        // XR Hands llama esto dos veces por cuadro (Dynamic y BeforeRender). En BeforeRender queda la pose final.
        private void OnUpdatedHands(XRHandSubsystem s, XRHandSubsystem.UpdateSuccessFlags flags, XRHandSubsystem.UpdateType type)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            Transform space = _cam != null ? _cam.transform.parent : null;
            Record(_hands[0], s.leftHand, now);
            Record(_hands[1], s.rightHand, now);
            foreach (Hand h in _hands)
            {
                if (!h.Tracked || h.Count == 0) { Show(h, false); continue; }
                Evaluate(h, now + OffsetMs / 1000.0);
                if (_cam != null) Apply(h, space);
            }
        }

        private static void Record(Hand h, XRHand hand, double now)
        {
            if (!hand.isTracked) { h.Tracked = false; h.Count = 0; return; }
            h.Tracked = true;
            int wrist = XRHandJointID.Wrist.ToIndex();
            bool hasWrist = hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out Pose wp);
            // Mismos datos que la muestra anterior (el tracking corre a otra frecuencia que el render): no se duplica,
            // así la velocidad no se va a cero entre muestras nuevas.
            if (h.Count > 0 && hasWrist)
            {
                Sample last = h.Ring[h.Head];
                if (last.Valid[wrist] && last.Joints[wrist].position == wp.position && last.Joints[wrist].rotation == wp.rotation) return;
            }
            h.Head = (h.Head + 1) % h.Ring.Length;
            h.Count = Math.Min(h.Count + 1, h.Ring.Length);
            Sample smp = h.Ring[h.Head];
            smp.T = now;
            for (int i = 0; i < JointCount; i++)
            {
                XRHandJoint j = hand.GetJoint(XRHandJointIDUtility.FromIndex(i));
                smp.Valid[i] = j.TryGetPose(out smp.Joints[i]);
                smp.Radius[i] = j.TryGetRadius(out float r) && r > 0.001f ? r : DefaultFingerRadius;
            }
        }

        private Sample At(Hand h, int back) => h.Ring[(h.Head - back + h.Ring.Length * 2) % h.Ring.Length];

        /// <summary>Pose de la mano en el instante <paramref name="t"/> (interpolada hacia atrás o predicha hacia adelante).</summary>
        private void Evaluate(Hand h, double t)
        {
            Sample latest = At(h, 0), e = h.Eval;
            Array.Copy(latest.Radius, e.Radius, JointCount);
            if (t >= latest.T)
            {
                // Predicción: velocidad de cada articulación con una muestra de hace >= 20 ms (menos ruido).
                Sample old = null;
                for (int k = 1; k < h.Count; k++) { Sample c = At(h, k); if (latest.T - c.T >= 0.02) { old = c; break; } if (latest.T - c.T > 0.15) break; }
                float ahead = (float)(t - latest.T);
                float dt = old != null ? (float)(latest.T - old.T) : 0f;
                int w = XRHandJointID.Wrist.ToIndex();
                h.Speed = old != null && dt > 0f && latest.Valid[w] && old.Valid[w] ? (latest.Joints[w].position - old.Joints[w].position).magnitude / dt : 0f;
                for (int i = 0; i < JointCount; i++)
                {
                    e.Valid[i] = latest.Valid[i];
                    e.Joints[i] = latest.Joints[i];
                    if (old == null || dt <= 0f || ahead <= 0f || !latest.Valid[i] || !old.Valid[i]) continue;
                    Vector3 step = (latest.Joints[i].position - old.Joints[i].position) / dt * ahead;
                    if (step.sqrMagnitude > MaxPredictMeters * MaxPredictMeters) step = step.normalized * MaxPredictMeters;
                    e.Joints[i].position += step;
                }
                return;
            }
            // Retraso: entre las dos muestras que rodean t (o la más vieja que haya).
            Sample a = latest, b = latest;
            for (int k = 1; k < h.Count; k++)
            {
                Sample c = At(h, k);
                b = a; a = c;
                if (c.T <= t) break;
            }
            float u = b.T > a.T ? Mathf.Clamp01((float)((t - a.T) / (b.T - a.T))) : 1f;
            int wi = XRHandJointID.Wrist.ToIndex();
            h.Speed = b.T > a.T && a.Valid[wi] && b.Valid[wi] ? (b.Joints[wi].position - a.Joints[wi].position).magnitude / (float)(b.T - a.T) : 0f;
            for (int i = 0; i < JointCount; i++)
            {
                bool va = a.Valid[i], vb = b.Valid[i];
                e.Valid[i] = va || vb;
                if (va && vb)
                    e.Joints[i] = new Pose(Vector3.LerpUnclamped(a.Joints[i].position, b.Joints[i].position, u),
                                           Quaternion.SlerpUnclamped(a.Joints[i].rotation, b.Joints[i].rotation, u));
                else e.Joints[i] = vb ? b.Joints[i] : a.Joints[i];
            }
        }

        private void Apply(Hand h, Transform space)
        {
            Sample e = h.Eval;
            int wrist = XRHandJointID.Wrist.ToIndex(), middle = XRHandJointID.MiddleProximal.ToIndex();
            if (!e.Valid[wrist]) { Show(h, false); return; }
            Show(h, true);
            if (h.Model != null)
            {
                // En orden de índice: cada padre (muñeca, metacarpianos…) va antes que sus hijos.
                for (int i = 0; i < JointCount; i++)
                {
                    Transform b = h.Bones[i];
                    if (b == null || !e.Valid[i]) continue;
                    Pose p = e.Joints[i];
                    b.SetPositionAndRotation(World(space, p.position), space != null ? space.rotation * p.rotation : p.rotation);
                }
            }
            Vector3 w = World(space, e.Joints[wrist].position);
            Vector3 back = e.Valid[middle] ? (w - World(space, e.Joints[middle].position)).normalized : -_cam.transform.forward;
            Vector3 dir = ForearmDirection(w, back, _cam.transform, h.Left);
            Vector3 mid = w + dir * (ForearmLength * 0.45f);
            SetCapsule(h.ForearmNear, w, mid, WristRadius);
            SetCapsule(h.ForearmFar, mid, w + dir * ForearmLength, ElbowRadius);

            // Inflado: el del material (occlusion inflate=MM) + un poco más mientras la mano se mueve rápido.
            Occluders occ = Occluders.Instance;
            float inflate = (occ != null ? occ.InflateMm / 1000f : 0.004f) + Mathf.Min(MaxSpeedInflate, h.Speed * SpeedInflate);
            _block.SetFloat(InflateId, inflate);
            if (h.Skin != null) h.Skin.SetPropertyBlock(_block);
            h.ForearmNear.SetPropertyBlock(_block);
            h.ForearmFar.SetPropertyBlock(_block);
        }

        private static Vector3 World(Transform space, Vector3 p) => space != null ? space.TransformPoint(p) : p;

        private static void SetCapsule(MeshRenderer r, Vector3 a, Vector3 b, float radius)
        {
            Matrix4x4 m = Occluders.CapsuleMatrix(a, b, radius);
            Transform t = r.transform;
            t.SetPositionAndRotation(m.GetColumn(3), m.rotation);
            t.localScale = m.lossyScale;
        }

        private void Show(Hand h, bool on)
        {
            bool model = on && _meshes;
            if (h.Model != null && h.Model.activeSelf != model) h.Model.SetActive(model);
            if (h.ForearmNear.gameObject.activeSelf != on) h.ForearmNear.gameObject.SetActive(on);
            if (h.ForearmFar.gameObject.activeSelf != on) h.ForearmFar.gameObject.SetActive(on);
        }

        // Respaldo sin mallas: cápsulas por hueso + palma rellena.
        private static void DrawCapsules(Occluders occ, Sample e, Transform space)
        {
            int wristI = XRHandJointID.Wrist.ToIndex();
            if (!e.Valid[wristI]) return;
            Vector3 wrist = World(space, e.Joints[wristI].position);
            foreach (XRHandJointID[] finger in Fingers)
            {
                bool hasPrev = false;
                Vector3 prev = default;
                float prevR = DefaultFingerRadius;
                foreach (XRHandJointID id in finger)
                {
                    int i = id.ToIndex();
                    if (!e.Valid[i]) { hasPrev = false; continue; }
                    Vector3 p = World(space, e.Joints[i].position);
                    if (hasPrev) occ.DrawCapsule(prev, p, Mathf.Max(prevR, e.Radius[i]) * CapsuleInflate);
                    prev = p; prevR = e.Radius[i]; hasPrev = true;
                }
            }
            Vector3? lastKnuckle = null;
            foreach (XRHandJointID k in Knuckles)
            {
                int i = k.ToIndex();
                if (!e.Valid[i]) continue;
                Vector3 p = World(space, e.Joints[i].position);
                occ.DrawCapsule(wrist, p, PalmRadius * CapsuleInflate);
                if (lastKnuckle.HasValue) occ.DrawCapsule(lastKnuckle.Value, p, PalmRadius * CapsuleInflate);
                lastKnuckle = p;
            }
        }

        /// <summary>
        /// Dirección muñeca → codo. «Hacia atrás desde los dedos» falla apenas se dobla la muñeca (al apuntar con el
        /// índice el antebrazo quedaba vertical bajo la mano). Ahora: hombro estimado desde la cabeza, brazo de dos huesos
        /// con el codo hacia abajo y afuera, y como mucho <see cref="MaxWristBendDegrees"/> de diferencia con el dorso.
        /// </summary>
        public static Vector3 ForearmDirection(Vector3 wrist, Vector3 handBack, Transform head, bool left)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(-head.up, Vector3.up);
            fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 side = left ? -right : right;
            Vector3 shoulder = head.position + side * ShoulderLateral - Vector3.up * ShoulderDown - fwd * ShoulderBack;

            Vector3 sw = wrist - shoulder;
            float d = Mathf.Clamp(sw.magnitude, 0.05f, UpperArm + ForearmLength - 0.005f);
            Vector3 axis = sw.sqrMagnitude > 1e-6f ? sw.normalized : -Vector3.up;
            Vector3 pole = Vector3.ProjectOnPlane(-Vector3.up + side * 0.5f - fwd * 0.2f, axis);
            pole = pole.sqrMagnitude > 1e-6f ? pole.normalized : Vector3.ProjectOnPlane(-Vector3.up, axis).normalized;
            float a = (UpperArm * UpperArm - ForearmLength * ForearmLength + d * d) / (2f * d);
            float h = Mathf.Sqrt(Mathf.Max(0f, UpperArm * UpperArm - a * a));
            Vector3 elbow = shoulder + axis * a + pole * h;

            Vector3 dir = elbow - wrist;
            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : handBack;
            if (Vector3.Angle(handBack, dir) > MaxWristBendDegrees)
                dir = Vector3.RotateTowards(handBack, dir, MaxWristBendDegrees * Mathf.Deg2Rad, 0f).normalized;
            return dir;
        }
    }
}
