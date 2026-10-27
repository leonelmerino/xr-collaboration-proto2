using System.Collections.Generic;
using UnityEngine;

namespace XRCollab.Measurement.Cues
{
    /// <summary>
    /// Íconos 3D de las señales, hechos en código: polígonos 2D extruidos con bisel (cara frontal, bisel, lados, cara
    /// trasera) y normales planas por cara, para que el material metálico refleje la sala con aristas nítidas.
    /// La cara frontal mira a -Z (hacia quien mira, igual que el texto de TMP). Coordenadas en una caja de ~1×1.
    /// </summary>
    public static class CueMeshes
    {
        // ---------------- formas ----------------

        public static Vector2[] Star(float outer = 0.5f, float inner = 0.215f)
        {
            var pts = new Vector2[10];
            for (int i = 0; i < 10; i++)
            {
                float a = Mathf.PI * 0.5f + i * Mathf.PI / 5f;
                float r = i % 2 == 0 ? outer : inner;
                pts[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            }
            return pts;
        }

        public static Vector2[] Check() => Stroke(new[] { new Vector2(-0.42f, 0.02f), new Vector2(-0.13f, -0.29f), new Vector2(0.44f, 0.36f) }, 0.17f);

        public static Vector2[] Cross()
        {
            const float w = 0.095f, l = 0.47f;
            var plus = new[]
            {
                new Vector2(w, l), new Vector2(-w, l), new Vector2(-w, w), new Vector2(-l, w), new Vector2(-l, -w), new Vector2(-w, -w),
                new Vector2(-w, -l), new Vector2(w, -l), new Vector2(w, -w), new Vector2(l, -w), new Vector2(l, w), new Vector2(w, w),
            };
            return Rotate(plus, 45f);
        }

        public static Vector2[] Circle(float r, int segments = 48, Vector2 center = default)
        {
            var pts = new Vector2[segments];
            for (int i = 0; i < segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                pts[i] = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            }
            return pts;
        }

        public static Vector2[] Bar(Vector2 from, Vector2 to, float width) => Stroke(new[] { from, to }, width);

        public static Vector2[] Triangle(float r = 0.52f)
        {
            var pts = new Vector2[3];
            for (int i = 0; i < 3; i++)
            {
                float a = Mathf.PI * 0.5f + i * Mathf.PI * 2f / 3f;
                pts[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a) - 0.12f) * r;
            }
            return pts;
        }

        public static Vector2[] Bolt() => new[]
        {
            new Vector2(0.14f, 0.52f), new Vector2(-0.30f, -0.05f), new Vector2(-0.04f, -0.05f),
            new Vector2(-0.16f, -0.52f), new Vector2(0.31f, 0.09f), new Vector2(0.05f, 0.09f),
        };

        /// <summary>Un bloque de Jenga de frente: rectángulo de ancho <paramref name="w"/> y alto <paramref name="h"/>, girado.</summary>
        public static Vector2[] Block(Vector2 center, float w, float h, float degrees)
        {
            var pts = new[] { new Vector2(-w, -h), new Vector2(w, -h), new Vector2(w, h), new Vector2(-w, h) };
            for (int i = 0; i < 4; i++) pts[i] *= 0.5f;
            pts = Rotate(pts, degrees);
            for (int i = 0; i < 4; i++) pts[i] += center;
            return pts;
        }

        /// <summary>
        /// Torre de Jenga que se viene abajo (ícono de «torre caída», 2026-10-27): dos bloques firmes abajo y los de
        /// arriba cada vez más inclinados, el último cayendo. (centro x, centro y, giro en grados) de cada bloque.
        /// </summary>
        public static readonly Vector3[] TowerBlocks =
        {
            new Vector3(-0.14f, -0.42f, 0f), new Vector3(-0.13f, -0.26f, 0f), new Vector3(-0.07f, -0.10f, -7f),
            new Vector3(0.06f, 0.07f, -19f), new Vector3(0.25f, 0.29f, -40f),
        };

        public const float TowerBlockWidth = 0.58f, TowerBlockHeight = 0.13f;

        /// <summary>Contorno de una línea quebrada gruesa (uniones en inglete, extremos planos).</summary>
        public static Vector2[] Stroke(Vector2[] line, float width)
        {
            int n = line.Length;
            var left = new Vector2[n];
            var right = new Vector2[n];
            float h = width * 0.5f;
            for (int i = 0; i < n; i++)
            {
                Vector2 dPrev = i > 0 ? (line[i] - line[i - 1]).normalized : (line[1] - line[0]).normalized;
                Vector2 dNext = i < n - 1 ? (line[i + 1] - line[i]).normalized : dPrev;
                Vector2 nPrev = new Vector2(-dPrev.y, dPrev.x), nNext = new Vector2(-dNext.y, dNext.x);
                Vector2 miter = (nPrev + nNext).normalized;
                float len = h / Mathf.Max(0.3f, Vector2.Dot(miter, nNext));
                left[i] = line[i] + miter * len;
                right[i] = line[i] - miter * len;
            }
            var poly = new Vector2[n * 2];
            for (int i = 0; i < n; i++) { poly[i] = right[i]; poly[n * 2 - 1 - i] = left[i]; }
            return poly;
        }

        // ---------------- extrusión con bisel ----------------

        /// <summary>Malla de uno o más polígonos simples extruidos: profundidad total <paramref name="depth"/>, bisel frontal <paramref name="bevel"/>.</summary>
        public static Mesh Extrude(IList<Vector2[]> polygons, float depth, float bevel, string name = "CueShape")
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var tris = new List<int>();
            foreach (Vector2[] raw in polygons)
            {
                Vector2[] outer = EnsureCcw(raw);
                Vector2[] inset = Inset(outer, bevel);
                float zFront = -depth * 0.5f, zBevel = zFront + bevel, zBack = depth * 0.5f;

                // Cara frontal (normal -Z): el polígono biselado, triangulado.
                int[] capFront = Triangulate(inset);
                int baseFront = verts.Count;
                foreach (Vector2 p in inset) { verts.Add(new Vector3(p.x, p.y, zFront)); normals.Add(Vector3.back); }
                for (int i = 0; i < capFront.Length; i += 3)
                {
                    // CCW visto desde +Z = CW visto desde -Z: invertir para que la cara frontal mire a -Z.
                    tris.Add(baseFront + capFront[i]); tris.Add(baseFront + capFront[i + 2]); tris.Add(baseFront + capFront[i + 1]);
                }

                // Cara trasera (normal +Z).
                int[] capBack = Triangulate(outer);
                int baseBack = verts.Count;
                foreach (Vector2 p in outer) { verts.Add(new Vector3(p.x, p.y, zBack)); normals.Add(Vector3.forward); }
                for (int i = 0; i < capBack.Length; i += 3)
                {
                    tris.Add(baseBack + capBack[i]); tris.Add(baseBack + capBack[i + 1]); tris.Add(baseBack + capBack[i + 2]);
                }

                int n = outer.Length;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    // Bisel: del contorno interior en el frente al exterior un poco más atrás.
                    Quad(verts, normals, tris,
                        new Vector3(inset[i].x, inset[i].y, zFront), new Vector3(inset[j].x, inset[j].y, zFront),
                        new Vector3(outer[j].x, outer[j].y, zBevel), new Vector3(outer[i].x, outer[i].y, zBevel));
                    // Lado.
                    Quad(verts, normals, tris,
                        new Vector3(outer[i].x, outer[i].y, zBevel), new Vector3(outer[j].x, outer[j].y, zBevel),
                        new Vector3(outer[j].x, outer[j].y, zBack), new Vector3(outer[i].x, outer[i].y, zBack));
                }
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Cuadrilátero a-b-c-d con normal plana hacia afuera (a→b sigue el contorno CCW; c, d más atrás).
        private static void Quad(List<Vector3> verts, List<Vector3> normals, List<int> tris, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Vector3 n = Vector3.Cross(b - a, d - a).normalized;
            // Afuera = hacia la derecha del avance CCW (en XY); si la cruz apunta adentro, invertir.
            Vector2 edge = new Vector2(b.x - a.x, b.y - a.y);
            Vector2 outward = new Vector2(edge.y, -edge.x);
            bool flip = n.x * outward.x + n.y * outward.y < 0f;
            if (flip) n = -n;
            int i0 = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            for (int k = 0; k < 4; k++) normals.Add(n);
            if (!flip) { tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2); tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3); }
            else { tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 1); tris.Add(i0); tris.Add(i0 + 3); tris.Add(i0 + 2); }
        }

        private static Vector2[] Inset(Vector2[] ccw, float d)
        {
            int n = ccw.Length;
            var result = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 prev = ccw[(i + n - 1) % n], cur = ccw[i], next = ccw[(i + 1) % n];
                Vector2 e1 = (cur - prev).normalized, e2 = (next - cur).normalized;
                Vector2 n1 = new Vector2(-e1.y, e1.x), n2 = new Vector2(-e2.y, e2.x);   // hacia adentro en CCW
                float denom = 1f + Vector2.Dot(n1, n2);
                Vector2 offset = denom > 1e-3f ? (n1 + n2) * (d / denom) : n1 * d;
                if (offset.magnitude > d * 3f) offset = offset.normalized * d * 3f;
                result[i] = cur + offset;
            }
            return result;
        }

        private static float SignedArea(Vector2[] p)
        {
            float a = 0f;
            for (int i = 0; i < p.Length; i++) { Vector2 u = p[i], v = p[(i + 1) % p.Length]; a += u.x * v.y - v.x * u.y; }
            return a * 0.5f;
        }

        private static Vector2[] EnsureCcw(Vector2[] p)
        {
            if (SignedArea(p) >= 0f) return p;
            var r = (Vector2[])p.Clone();
            System.Array.Reverse(r);
            return r;
        }

        private static Vector2[] Rotate(Vector2[] p, float degrees)
        {
            float c = Mathf.Cos(degrees * Mathf.Deg2Rad), s = Mathf.Sin(degrees * Mathf.Deg2Rad);
            var r = new Vector2[p.Length];
            for (int i = 0; i < p.Length; i++) r[i] = new Vector2(p[i].x * c - p[i].y * s, p[i].x * s + p[i].y * c);
            return r;
        }

        /// <summary>Triangulación por recorte de orejas de un polígono simple CCW (índices locales).</summary>
        public static int[] Triangulate(Vector2[] ccw)
        {
            var idx = new List<int>();
            for (int i = 0; i < ccw.Length; i++) idx.Add(i);
            var tris = new List<int>();
            int guard = 0;
            while (idx.Count > 3 && guard++ < 1000)
            {
                bool clipped = false;
                for (int k = 0; k < idx.Count; k++)
                {
                    int ia = idx[(k + idx.Count - 1) % idx.Count], ib = idx[k], ic = idx[(k + 1) % idx.Count];
                    Vector2 a = ccw[ia], b = ccw[ib], c = ccw[ic];
                    if (Cross(b - a, c - b) <= 1e-7f) continue;   // vértice reflejo
                    bool inside = false;
                    foreach (int other in idx)
                    {
                        if (other == ia || other == ib || other == ic) continue;
                        if (PointInTriangle(ccw[other], a, b, c)) { inside = true; break; }
                    }
                    if (inside) continue;
                    tris.Add(ia); tris.Add(ib); tris.Add(ic);
                    idx.RemoveAt(k);
                    clipped = true;
                    break;
                }
                if (!clipped) break;   // polígono degenerado: lo que quede se abanica
            }
            for (int k = 1; k + 1 < idx.Count; k++) { tris.Add(idx[0]); tris.Add(idx[k]); tris.Add(idx[k + 1]); }
            return tris.ToArray();
        }

        private static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
            return d1 >= 0f && d2 >= 0f && d3 >= 0f;
        }
    }
}
