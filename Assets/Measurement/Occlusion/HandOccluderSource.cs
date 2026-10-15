using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace XRCollab.Measurement.Occlusion
{
    /// <summary>
    /// Manos y antebrazos reales como oclusores.
    ///
    /// <b>Mano:</b> la malla con piel del ejemplo «Hand Visualizer» de XR Hands (Resources/Occlusion/Hands), con cada
    /// hueso puesto en la pose de su articulación (26 de XR_EXT_hand_tracking). El visor (VIVE Focus Vision) no ofrece
    /// malla de mano ni profundidad en vivo por OpenXR, así que esta es la forma más fiel que hay. Ajuste al usuario:
    /// el largo de la mano (muñeca → punta del medio, medido en vivo) escala el grosor de la malla, y
    /// <see cref="Thickness"/> lo corrige a mano («occlusion thickness=1.1»).
    /// <b>Antebrazo:</b> de la muñeca al codo estimado (hombro desde la cabeza + brazo de dos huesos), ver
    /// <see cref="ForearmDirection"/>.
    ///
    /// <b>Tiempo (lo que más se notaba como «lag»):</b>
    ///  - La pose sale del callback BeforeRender de XR Hands (en el visor, OpenXR la entrega para el instante en que se
    ///    mostrará el cuadro) y en ese mismo callback se «hornea» la malla (BakeMesh) y se dibuja con Graphics.DrawMesh.
    ///    Antes se movían los huesos de un SkinnedMeshRenderer, que puede tomar los huesos del cuadro anterior según
    ///    cuándo corre el skinning: ahora la forma es siempre la pose de este cuadro.
    ///  - <see cref="OffsetMs"/> corre la pose: + predice (posición Y rotación de cada articulación, con las velocidades
    ///    del runtime si las da, si no por diferencias), − usa poses de antes.
    ///  - <see cref="TrailMs"/>: además de la pose actual se dibuja la de hace TrailMs. La unión de las dos cubre el
    ///    lugar donde se ve la mano en el passthrough aunque la cámara vaya algo atrasada respecto del tracking, sin
    ///    agrandar la mano quieta (con la mano quieta las dos poses coinciden y no se dibuja la segunda).
    ///
    /// Las poses de XR Hands están en el espacio de tracking: se pasan al mundo con el padre de la cámara.
    /// </summary>
    public sealed class HandOccluderSource : IOccluderSource, IDisposable
    {
        public const string LeftModel = "Occlusion/Hands/LeftHand";
        public const string RightModel = "Occlusion/Hands/RightHand";

        /// <summary>Desfase temporal de la pose usada (ms): + predice, - retrasa.</summary>
        public static float OffsetMs;

        /// <summary>Estela (ms): también se dibuja la pose de hace este tiempo, ver la clase.</summary>
        public static float TrailMs = 30f;

        /// <summary>Corrección manual del grosor (1 = el de la malla escalada al largo de la mano del usuario).</summary>
        public static float Thickness = 1f;

        /// <summary>Esferas en cada articulación con el radio del runtime (vista de ajuste, «occlusion joints»).</summary>
        public static bool ShowJoints;

        /// <summary>Inflado extra según la rapidez de la mano (m por m/s), ver README.</summary>
        public static float SpeedInflate = 0.01f;
        private const float MaxSpeedInflate = 0.008f;
        private static readonly int InflateId = Shader.PropertyToID("_Inflate");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        public static float CapsuleInflate = 1.15f;
        private const float DefaultFingerRadius = 0.009f;
        private const float PalmRadius = 0.017f;
        private const float WristRadius = 0.029f;
        private const float ElbowRadius = 0.038f;
        private const float MaxPredictMeters = 0.08f;
        private const float MaxPredictDegrees = 45f;
        private const float MinTrailSpeed = 0.05f;          // m/s: con la mano quieta no se dibuja la estela

        // Brazo de dos huesos desde un hombro estimado (como HandRayDriver): el tracking no da codo.
        private const float UpperArm = 0.30f;
        private const float ForearmLength = 0.27f;
        private const float ShoulderLateral = 0.18f;
        private const float ShoulderDown = 0.22f;
        private const float ShoulderBack = 0.06f;
        private const float MaxWristBendDegrees = 70f;

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
        // Muñeca → punta del medio: el largo de la mano con que se escala la malla.
        private static readonly XRHandJointID[] LengthChain =
        {
            XRHandJointID.Wrist, XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate,
            XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip,
        };

        /// <summary>Una pose de toda la mano en el espacio de tracking.</summary>
        private sealed class Sample
        {
            public double T;
            public readonly Pose[] Joints = new Pose[JointCount];
            public readonly bool[] Valid = new bool[JointCount];
            public readonly float[] Radius = new float[JointCount];
            public readonly Vector3[] LinVel = new Vector3[JointCount];
            public readonly Vector3[] AngVel = new Vector3[JointCount];   // rad/s, eje en el espacio de tracking
            public bool HasVel;
        }

        private sealed class Hand
        {
            public bool Left;
            public readonly Sample[] Ring = new Sample[48];   // ~0,5 s a 90 Hz
            public int Count, Head;                            // Head = la más reciente
            public readonly Sample Eval = new Sample(), Trail = new Sample();
            public bool Tracked;
            public GameObject Model;
            public SkinnedMeshRenderer Skin;
            public Transform[] Bones;                          // por índice de articulación (null = sin hueso)
            public Transform[] ScaleRoots;                     // huesos movidos cuyo padre no es hueso movido
            public Vector3[] ScaleRootsLocal;
            public float TemplateLength;                       // largo muñeca → punta del medio de la malla (m)
            public float UserLength;                           // el del usuario, suavizado (0 = aún no)
            public Mesh Baked, BakedTrail;
            public readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
            public float Speed;                                // rapidez de la muñeca (m/s)
            // Estadísticas para el canal de control
            public int NewSamples;
            public float SampleHz;
            public bool TrailDrawn;
        }

        private readonly Hand[] _hands = { new Hand { Left = true }, new Hand { Left = false } };
        private readonly List<XRHandSubsystem> _list = new List<XRHandSubsystem>();
        private readonly MaterialPropertyBlock _jointBlock = new MaterialPropertyBlock();
        private XRHandSubsystem _subsystem;
        private float _nextLookup;
        private Camera _cam;
        private bool _meshes;
        private GameObject _root;
        private Mesh _sphere;
        private float _statsT;
        private string _velSource = "?";

        /// <summary>"mesh" (malla con piel horneada cada cuadro) o "capsules".</summary>
        public string Kind => _meshes ? "mesh" : "capsules";

        public HandOccluderSource()
        {
            foreach (Hand h in _hands)
                for (int i = 0; i < h.Ring.Length; i++) h.Ring[i] = new Sample();
            _root = new GameObject("[HandOccluders]");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _meshes = BuildModel(_hands[0], LeftModel, "L_") & BuildModel(_hands[1], RightModel, "R_");
            if (!_meshes) Debug.LogWarning("[Occluders] sin mallas de mano (Resources/" + LeftModel + "): se usan cápsulas");
            GameObject s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphere = s.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.Destroy(s);
        }

        public void Dispose()
        {
            if (_subsystem != null) _subsystem.updatedHands -= OnUpdatedHands;
            _subsystem = null;
            foreach (Hand h in _hands)
            {
                if (h.Baked != null) UnityEngine.Object.Destroy(h.Baked);
                if (h.BakedTrail != null) UnityEngine.Object.Destroy(h.BakedTrail);
            }
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
            // El renderer nunca se dibuja solo: se hornea en BeforeRender y se dibuja con Graphics.DrawMesh.
            h.Skin.enabled = false;
            h.Skin.updateWhenOffscreen = true;
            h.Bones = new Transform[JointCount];
            int found = 0;
            for (int i = 0; i < JointCount; i++)
            {
                XRHandJointID id = XRHandJointIDUtility.FromIndex(i);
                if (byName.TryGetValue(prefix + id, out Transform b)) { h.Bones[i] = b; found++; }
            }
            if (found < 20) { UnityEngine.Object.Destroy(h.Model); h.Model = null; h.Skin = null; return false; }

            var bones = new HashSet<Transform>(h.Bones);
            var roots = new List<Transform>();
            foreach (Transform b in h.Bones) if (b != null && !bones.Contains(b.parent)) roots.Add(b);
            h.ScaleRoots = roots.ToArray();
            h.ScaleRootsLocal = roots.ConvertAll(r => r.localScale).ToArray();
            h.TemplateLength = ChainLength(i => h.Bones[i] != null ? h.Bones[i].position : (Vector3?)null);
            h.Baked = new Mesh { name = h.Model.name + "Baked" };
            h.BakedTrail = new Mesh { name = h.Model.name + "Trail" };
            h.Baked.MarkDynamic();
            h.BakedTrail.MarkDynamic();
            return true;
        }

        private static float ChainLength(Func<int, Vector3?> at)
        {
            float len = 0f;
            Vector3? prev = null;
            foreach (XRHandJointID id in LengthChain)
            {
                Vector3? p = at(id.ToIndex());
                if (!p.HasValue) return 0f;
                if (prev.HasValue) len += Vector3.Distance(prev.Value, p.Value);
                prev = p;
            }
            return len;
        }

        // ---------------- cada cuadro ----------------

        // Application.onBeforeRender (Occluders): solo busca el subsistema y las estadísticas. El dibujo va en el
        // callback BeforeRender de XR Hands, que tiene la pose de este cuadro.
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
            float now = Time.unscaledTime;
            if (now - _statsT >= 1f)
            {
                float dt = now - _statsT;
                foreach (Hand h in _hands) { h.SampleHz = h.NewSamples / dt; h.NewSamples = 0; }
                _statsT = now;
            }
        }

        // XR Hands llama esto dos veces por cuadro (Dynamic y BeforeRender). En BeforeRender queda la pose final y se dibuja.
        private void OnUpdatedHands(XRHandSubsystem s, XRHandSubsystem.UpdateSuccessFlags flags, XRHandSubsystem.UpdateType type)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            Record(_hands[0], s.leftHand, now);
            Record(_hands[1], s.rightHand, now);
            if (type != XRHandSubsystem.UpdateType.BeforeRender) return;
            Occluders occ = Occluders.Instance;
            if (occ == null || _cam == null || occ.CurrentMaterials.Length == 0) return;
            Transform space = _cam.transform.parent;
            foreach (Hand h in _hands)
            {
                h.TrailDrawn = false;
                if (!h.Tracked || h.Count == 0) continue;
                Evaluate(h, now + OffsetMs / 1000.0, h.Eval, true);
                if (!h.Eval.Valid[XRHandJointID.Wrist.ToIndex()]) continue;
                UpdateUserLength(h);
                float k = HandScale(h) * Thickness;
                float inflate = occ.InflateMm / 1000f + Mathf.Min(MaxSpeedInflate, h.Speed * SpeedInflate);
                h.Block.SetFloat(InflateId, inflate);
                if (_meshes)
                {
                    DrawPose(occ, h, h.Eval, space, k, h.Baked);
                    if (TrailMs > 0f && h.Speed >= MinTrailSpeed)
                    {
                        Evaluate(h, now + (OffsetMs - TrailMs) / 1000.0, h.Trail, false);
                        if (h.Trail.Valid[XRHandJointID.Wrist.ToIndex()]) { DrawPose(occ, h, h.Trail, space, k, h.BakedTrail); h.TrailDrawn = true; }
                    }
                }
                else DrawCapsules(occ, h.Eval, space, k, h.Block);
                DrawForearm(occ, h, h.Eval, space, k);
                if (ShowJoints) DrawJoints(occ, h.Eval, space);
            }
        }

        private void Record(Hand h, XRHand hand, double now)
        {
            if (!hand.isTracked) { h.Tracked = false; h.Count = 0; return; }
            h.Tracked = true;
            int wrist = XRHandJointID.Wrist.ToIndex();
            bool hasWrist = hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out Pose wp);
            // Mismos datos que la muestra anterior (el tracking corre a otra frecuencia que el render): no se duplica,
            // así la velocidad por diferencias no se va a cero entre muestras nuevas.
            if (h.Count > 0 && hasWrist)
            {
                Sample last = h.Ring[h.Head];
                if (last.Valid[wrist] && last.Joints[wrist].position == wp.position && last.Joints[wrist].rotation == wp.rotation) return;
            }
            h.Head = (h.Head + 1) % h.Ring.Length;
            h.Count = Math.Min(h.Count + 1, h.Ring.Length);
            h.NewSamples++;
            Sample smp = h.Ring[h.Head];
            smp.T = now;
            bool vel = true;
            for (int i = 0; i < JointCount; i++)
            {
                XRHandJoint j = hand.GetJoint(XRHandJointIDUtility.FromIndex(i));
                smp.Valid[i] = j.TryGetPose(out smp.Joints[i]);
                smp.Radius[i] = j.TryGetRadius(out float r) && r > 0.001f ? r : DefaultFingerRadius;
                bool lv = j.TryGetLinearVelocity(out smp.LinVel[i]);
                bool av = j.TryGetAngularVelocity(out smp.AngVel[i]);
                if (smp.Valid[i] && !(lv && av)) vel = false;
            }
            smp.HasVel = vel;
            _velSource = vel ? "runtime" : "diff";
        }

        private Sample At(Hand h, int back) => h.Ring[(h.Head - back + h.Ring.Length * 2) % h.Ring.Length];

        /// <summary>Pose de la mano en el instante <paramref name="t"/> (interpolada hacia atrás o predicha hacia adelante).</summary>
        private void Evaluate(Hand h, double t, Sample e, bool setSpeed)
        {
            Sample latest = At(h, 0);
            Array.Copy(latest.Radius, e.Radius, JointCount);
            int w = XRHandJointID.Wrist.ToIndex();
            if (t >= latest.T)
            {
                float ahead = (float)(t - latest.T);
                // Velocidad: la del runtime (XrHandJointVelocitiesEXT) si viene; si no, por diferencias con una muestra
                // de hace >= 20 ms (menos ruido).
                Sample old = null;
                if (!latest.HasVel)
                    for (int k = 1; k < h.Count; k++) { Sample c = At(h, k); if (latest.T - c.T >= 0.02) { old = c; break; } if (latest.T - c.T > 0.15) break; }
                float dt = old != null ? (float)(latest.T - old.T) : 0f;
                if (setSpeed)
                {
                    if (latest.HasVel && latest.Valid[w]) h.Speed = latest.LinVel[w].magnitude;
                    else h.Speed = old != null && dt > 0f && latest.Valid[w] && old.Valid[w] ? (latest.Joints[w].position - old.Joints[w].position).magnitude / dt : 0f;
                }
                for (int i = 0; i < JointCount; i++)
                {
                    e.Valid[i] = latest.Valid[i];
                    e.Joints[i] = latest.Joints[i];
                    if (ahead <= 0f || !latest.Valid[i]) continue;
                    Vector3 step;
                    Quaternion turn;
                    if (latest.HasVel)
                    {
                        step = latest.LinVel[i] * ahead;
                        Vector3 av = latest.AngVel[i];
                        float deg = Mathf.Min(av.magnitude * ahead * Mathf.Rad2Deg, MaxPredictDegrees);
                        turn = deg > 0.01f ? Quaternion.AngleAxis(deg, av.normalized) : Quaternion.identity;
                    }
                    else
                    {
                        if (old == null || dt <= 0f || !old.Valid[i]) continue;
                        step = (latest.Joints[i].position - old.Joints[i].position) / dt * ahead;
                        Quaternion delta = latest.Joints[i].rotation * Quaternion.Inverse(old.Joints[i].rotation);
                        delta.ToAngleAxis(out float ang, out Vector3 axis);
                        if (ang > 180f) ang -= 360f;
                        float deg = Mathf.Clamp(ang / dt * ahead, -MaxPredictDegrees, MaxPredictDegrees);
                        turn = Mathf.Abs(deg) > 0.01f && !float.IsInfinity(axis.x) ? Quaternion.AngleAxis(deg, axis) : Quaternion.identity;
                    }
                    if (step.sqrMagnitude > MaxPredictMeters * MaxPredictMeters) step = step.normalized * MaxPredictMeters;
                    e.Joints[i].position += step;
                    e.Joints[i].rotation = turn * e.Joints[i].rotation;
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
            if (setSpeed)
                h.Speed = latest.HasVel && latest.Valid[w] ? latest.LinVel[w].magnitude
                    : b.T > a.T && a.Valid[w] && b.Valid[w] ? (b.Joints[w].position - a.Joints[w].position).magnitude / (float)(b.T - a.T) : 0f;
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

        // Largo de la mano del usuario (muñeca → punta del medio), suavizado: solo con el dedo medio bastante estirado.
        private static void UpdateUserLength(Hand h)
        {
            Sample e = h.Eval;
            float len = ChainLength(i => e.Valid[i] ? e.Joints[i].position : (Vector3?)null);
            if (len < 0.12f || len > 0.26f) return;
            Vector3 wrist = e.Joints[XRHandJointID.Wrist.ToIndex()].position;
            float straight = Vector3.Distance(wrist, e.Joints[XRHandJointID.MiddleTip.ToIndex()].position) / len;
            if (straight < 0.93f) return;
            h.UserLength = h.UserLength <= 0f ? len : Mathf.Lerp(h.UserLength, len, 0.02f);
        }

        /// <summary>Escala del grosor de la malla: largo de la mano del usuario / largo de la malla (0,8–1,25).</summary>
        private static float HandScale(Hand h) =>
            h.TemplateLength > 0.05f && h.UserLength > 0f ? Mathf.Clamp(h.UserLength / h.TemplateLength, 0.8f, 1.25f) : 1f;

        private void DrawPose(Occluders occ, Hand h, Sample e, Transform space, float k, Mesh into)
        {
            for (int r = 0; r < h.ScaleRoots.Length; r++) h.ScaleRoots[r].localScale = h.ScaleRootsLocal[r] * k;
            // En orden de índice: cada padre (muñeca, metacarpianos…) va antes que sus hijos.
            for (int i = 0; i < JointCount; i++)
            {
                Transform b = h.Bones[i];
                if (b == null || !e.Valid[i]) continue;
                Pose p = e.Joints[i];
                b.SetPositionAndRotation(World(space, p.position), space != null ? space.rotation * p.rotation : p.rotation);
            }
            h.Skin.BakeMesh(into, true);
            Transform t = h.Skin.transform;
            occ.DrawMesh(into, Matrix4x4.TRS(t.position, t.rotation, Vector3.one), h.Block);
        }

        private void DrawForearm(Occluders occ, Hand h, Sample e, Transform space, float k)
        {
            int wrist = XRHandJointID.Wrist.ToIndex(), middle = XRHandJointID.MiddleProximal.ToIndex();
            Vector3 w = World(space, e.Joints[wrist].position);
            Vector3 back = e.Valid[middle] ? (w - World(space, e.Joints[middle].position)).normalized : -_cam.transform.forward;
            Vector3 dir = ForearmDirection(w, back, _cam.transform, h.Left);
            Vector3 mid = w + dir * (ForearmLength * 0.45f);
            occ.DrawMesh(occ.CapsuleMesh, Occluders.CapsuleMatrix(w, mid, WristRadius * k), h.Block);
            occ.DrawMesh(occ.CapsuleMesh, Occluders.CapsuleMatrix(mid, w + dir * ForearmLength, ElbowRadius * k), h.Block);
        }

        // Vista de ajuste: una esfera por articulación con el radio que da el runtime (magenta), encima de la forma.
        private void DrawJoints(Occluders occ, Sample e, Transform space)
        {
            _jointBlock.SetColor(ColorId, new Color(1f, 0.1f, 0.8f, 0.8f));
            for (int i = 0; i < JointCount; i++)
            {
                if (!e.Valid[i]) continue;
                float d = e.Radius[i] * 2f;
                occ.DrawDebugMesh(_sphere, Matrix4x4.TRS(World(space, e.Joints[i].position), Quaternion.identity, new Vector3(d, d, d)), _jointBlock);
            }
        }

        private static Vector3 World(Transform space, Vector3 p) => space != null ? space.TransformPoint(p) : p;

        // Respaldo sin mallas: cápsulas por hueso (radio del runtime) + palma rellena.
        private static void DrawCapsules(Occluders occ, Sample e, Transform space, float k, MaterialPropertyBlock block)
        {
            int wristI = XRHandJointID.Wrist.ToIndex();
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
                    if (hasPrev) occ.DrawMesh(occ.CapsuleMesh, Occluders.CapsuleMatrix(prev, p, Mathf.Max(prevR, e.Radius[i]) * CapsuleInflate * k), block);
                    prev = p; prevR = e.Radius[i]; hasPrev = true;
                }
            }
            Vector3? lastKnuckle = null;
            foreach (XRHandJointID kn in Knuckles)
            {
                int i = kn.ToIndex();
                if (!e.Valid[i]) continue;
                Vector3 p = World(space, e.Joints[i].position);
                occ.DrawMesh(occ.CapsuleMesh, Occluders.CapsuleMatrix(wrist, p, PalmRadius * CapsuleInflate * k), block);
                if (lastKnuckle.HasValue) occ.DrawMesh(occ.CapsuleMesh, Occluders.CapsuleMatrix(lastKnuckle.Value, p, PalmRadius * CapsuleInflate * k), block);
                lastKnuckle = p;
            }
        }

        /// <summary>Objeto JSON con el estado del ajuste (canal de control, "occlusionInfo.fit").</summary>
        public string FitJson()
        {
            var c = CultureInfo.InvariantCulture;
            string HandJson(Hand h) =>
                "{\"tracked\":" + (h.Tracked ? "true" : "false") +
                ",\"hz\":" + h.SampleHz.ToString("0", c) +
                ",\"speed\":" + h.Speed.ToString("0.00", c) +
                ",\"lengthMm\":" + (h.UserLength * 1000f).ToString("0", c) +
                ",\"scale\":" + HandScale(h).ToString("0.00", c) +
                ",\"trail\":" + (h.TrailDrawn ? "true" : "false") + "}";
            return "{\"vel\":\"" + _velSource + "\",\"trailMs\":" + TrailMs.ToString("0", c) +
                   ",\"thickness\":" + Thickness.ToString("0.00", c) + ",\"joints\":" + (ShowJoints ? "true" : "false") +
                   ",\"templateMm\":" + (_hands[1].TemplateLength * 1000f).ToString("0", c) +
                   ",\"L\":" + HandJson(_hands[0]) + ",\"R\":" + HandJson(_hands[1]) + "}";
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
