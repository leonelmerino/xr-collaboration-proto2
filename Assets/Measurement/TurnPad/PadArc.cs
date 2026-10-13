using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace XRCollab.Measurement.Turns
{
    /// <summary>
    /// Un arco de anillo (corona circular parcial) en el plano XY local, que mira a -Z como los quads de las señales.
    /// Se rehace cada vez que cambia (≤ 96 segmentos: barato). Bordes suavizados con una franja de ~1 mm que va a alpha 0,
    /// así se ve nítido en el visor sin depender del MSAA. Sirve para el progreso de «mantener» y para los anillos.
    /// Ángulos en grados, 0 = arriba (12 en punto), positivo = sentido horario visto desde el participante.
    /// </summary>
    internal sealed class PadArc
    {
        private const int Segments = 96;
        private const float Edge = 0.0012f;

        public readonly Transform Transform;
        private readonly Mesh _mesh;
        private readonly Material _mat;
        private readonly Vector3[] _v = new Vector3[(Segments + 1) * 4];
        private readonly Color[] _c = new Color[(Segments + 1) * 4];
        private float _inner = -1f, _outer, _from, _sweep;
        private Color _color;

        public PadArc(string name, Transform parent, Material baseAlpha, int layer, List<Material> owned)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            Transform = go.transform;
            _mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
            _mesh.MarkDynamic();
            var tris = new int[Segments * 3 * 6];
            int t = 0;
            for (int i = 0; i < Segments; i++)
            {
                int a = i * 4, b = (i + 1) * 4;
                // Tres bandas (borde exterior, cuerpo, borde interior); Cull Off en el shader: el orden no importa.
                for (int k = 0; k < 3; k++)
                {
                    tris[t++] = a + k; tris[t++] = b + k; tris[t++] = b + k + 1;
                    tris[t++] = a + k; tris[t++] = b + k + 1; tris[t++] = a + k + 1;
                }
            }
            _mesh.vertices = _v;
            _mesh.colors = _c;
            _mesh.uv = new Vector2[_v.Length];
            _mesh.triangles = tris;
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = go.AddComponent<MeshRenderer>();
            _mat = new Material(baseAlpha) { mainTexture = Texture2D.whiteTexture, renderQueue = 3012 };
            _mat.SetFloat("_Premul", 1f);
            _mat.SetFloat("_Intensity", 1f);
            _mat.color = Color.white;
            owned.Add(_mat);
            r.sharedMaterial = _mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        public bool Visible
        {
            get => Transform.gameObject.activeSelf;
            set { if (Transform.gameObject.activeSelf != value) Transform.gameObject.SetActive(value); }
        }

        /// <summary>Arco de <paramref name="inner"/> a <paramref name="outer"/> (m), desde <paramref name="fromDeg"/>
        /// recorriendo <paramref name="sweepDeg"/> grados, del color dado (alpha = opacidad).</summary>
        public void Set(float inner, float outer, float fromDeg, float sweepDeg, Color color)
        {
            sweepDeg = Mathf.Clamp(sweepDeg, 0f, 360f);
            if (Mathf.Approximately(inner, _inner) && Mathf.Approximately(outer, _outer) && Mathf.Approximately(fromDeg, _from) &&
                Mathf.Approximately(sweepDeg, _sweep) && color == _color) return;
            _inner = inner; _outer = outer; _from = fromDeg; _sweep = sweepDeg; _color = color;
            float[] radii = { outer + Edge, outer, inner, Mathf.Max(0f, inner - Edge) };
            var clear = new Color(color.r, color.g, color.b, 0f);
            for (int i = 0; i <= Segments; i++)
            {
                float deg = fromDeg + sweepDeg * i / Segments;
                float rad = (90f - deg) * Mathf.Deg2Rad;   // 0° arriba, horario
                float cx = Mathf.Cos(rad), cy = Mathf.Sin(rad);
                for (int k = 0; k < 4; k++)
                {
                    _v[i * 4 + k] = new Vector3(cx * radii[k], cy * radii[k], 0f);
                    _c[i * 4 + k] = k == 0 || k == 3 ? clear : color;
                }
            }
            _mesh.vertices = _v;
            _mesh.colors = _c;
            _mesh.RecalculateBounds();
        }
    }
}
