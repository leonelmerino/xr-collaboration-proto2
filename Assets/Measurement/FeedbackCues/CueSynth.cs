using System;
using System.Collections.Generic;
using UnityEngine;

namespace XRCollab.Measurement.Cues
{
    /// <summary>
    /// Sonidos de las señales, sintetizados al arrancar (sin archivos de audio): campanas con armónicos y caída
    /// exponencial, un «whoosh» de ruido filtrado para la entrada, golpes graves, tic-tac. Mono, 48 kHz.
    /// </summary>
    public static class CueSynth
    {
        private const int Rate = 48000;
        private static readonly Dictionary<string, AudioClip> Cache = new Dictionary<string, AudioClip>();

        public static AudioClip Whoosh() => Get("whoosh", () => Render(0.55f, WhooshSample));

        public static AudioClip For(CueTone tone)
        {
            switch (tone)
            {
                case CueTone.Great: return Get("great", () => Render(1.6f, GreatSample));
                case CueTone.Good: return Get("good", () => Render(1.1f, GoodSample));
                case CueTone.Bad: return Get("bad", () => Render(0.9f, BadSample));
                case CueTone.Slow: return Get("slow", () => Render(1.3f, SlowSample));
                case CueTone.Warn: return Get("warn", () => Render(0.8f, WarnSample));
                default: return Get("info", () => Render(0.7f, InfoSample));
            }
        }

        private static AudioClip Get(string key, Func<AudioClip> make)
        {
            if (!Cache.TryGetValue(key, out AudioClip clip) || clip == null) Cache[key] = clip = make();
            return clip;
        }

        private static AudioClip Render(float seconds, Func<float, float> sample)
        {
            int n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            float peak = 1e-4f;
            for (int i = 0; i < n; i++)
            {
                data[i] = sample(i / (float)Rate);
                peak = Mathf.Max(peak, Mathf.Abs(data[i]));
            }
            float gain = 0.85f / peak;
            int fade = Rate / 200;   // 5 ms al final para que no haga clic
            for (int i = 0; i < n; i++)
            {
                data[i] *= gain;
                if (i > n - fade) data[i] *= (n - i) / (float)fade;
            }
            var clip = AudioClip.Create("Cue_" + seconds, n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // ---------------- bloques ----------------

        private static float Sine(float f, float t) => Mathf.Sin(2f * Mathf.PI * f * t);

        /// <summary>Campana: fundamental + parciales inarmónicos suaves, ataque de 4 ms y caída exponencial.</summary>
        private static float Bell(float f, float t, float start, float decay)
        {
            float x = t - start;
            if (x < 0f) return 0f;
            float env = Mathf.Min(1f, x / 0.004f) * Mathf.Exp(-x / decay);
            return env * (Sine(f, x) + 0.42f * Sine(f * 2.01f, x) * Mathf.Exp(-x / (decay * 0.5f)) + 0.18f * Sine(f * 3.02f, x) * Mathf.Exp(-x / (decay * 0.3f)));
        }

        private static float Note(int midi) => 440f * Mathf.Pow(2f, (midi - 69) / 12f);

        // Ruido determinista (mismo sonido cada vez).
        private static float Noise(float t)
        {
            uint h = (uint)(t * Rate) * 747796405u + 2891336453u;
            h = ((h >> (int)((h >> 28) + 4u)) ^ h) * 277803737u;
            return ((h >> 22) ^ h) / (float)uint.MaxValue * 2f - 1f;
        }

        private static float _lp, _bp;

        private static float WhooshSample(float t)
        {
            if (t == 0f) { _lp = 0f; _bp = 0f; }
            // Ruido filtrado con un pasabanda que sube de 300 a 3000 Hz; envolvente en campana.
            float f = Mathf.Lerp(300f, 3000f, Mathf.SmoothStep(0f, 1f, t / 0.5f));
            float k = 2f * Mathf.Sin(Mathf.PI * f / Rate);
            float hp = Noise(t) - _lp - 0.6f * _bp;
            _bp += k * hp;
            _lp += k * _bp;
            float env = Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / 0.5f));
            return _bp * env * env;
        }

        private static float GreatSample(float t)
        {
            // Arpegio mayor ascendente (Do6 Mi6 Sol6 Do7) + destellos agudos.
            float s = Bell(Note(84), t, 0.00f, 0.55f) + Bell(Note(88), t, 0.09f, 0.55f) + Bell(Note(91), t, 0.18f, 0.6f) + 1.2f * Bell(Note(96), t, 0.27f, 0.9f);
            for (int i = 0; i < 6; i++) s += 0.25f * Bell(Note(100 + (i * 5) % 12), t, 0.3f + i * 0.07f, 0.12f);
            return s;
        }

        private static float GoodSample(float t) => Bell(Note(88), t, 0f, 0.45f) + 1.1f * Bell(Note(95), t, 0.11f, 0.7f);

        private static float BadSample(float t)
        {
            // «Buuum» grave que baja (sierra suavizada) + golpe de ruido.
            float f = Mathf.Lerp(196f, 98f, Mathf.Clamp01(t / 0.6f));
            float env = Mathf.Min(1f, t / 0.01f) * Mathf.Exp(-t / 0.35f);
            float saw = 0f;
            for (int k = 1; k <= 6; k++) saw += Sine(f * k, t) / k;
            float thump = Noise(t) * Mathf.Exp(-t / 0.04f) * 0.6f + Sine(70f, t) * Mathf.Exp(-t / 0.12f);
            return saw * env * 0.6f + thump + 0.35f * Bell(Note(57), t, 0.18f, 0.35f);
        }

        private static float SlowSample(float t)
        {
            // Tic-tac de reloj + «uh-oh» (Sol4 → Re4) en onda triangular.
            float s = 0f;
            for (int i = 0; i < 4; i++)
            {
                float x = t - i * 0.14f;
                if (x >= 0f) s += Noise(t) * Mathf.Exp(-x / 0.006f) * (i % 2 == 0 ? 0.8f : 0.5f) + Sine(i % 2 == 0 ? 2400f : 1900f, x) * Mathf.Exp(-x / 0.01f) * 0.5f;
            }
            s += 0.8f * Triangle(Note(67), t, 0.62f, 0.28f) + 0.8f * Triangle(Note(62), t, 0.86f, 0.4f);
            return s;
        }

        private static float Triangle(float f, float t, float start, float decay)
        {
            float x = t - start;
            if (x < 0f) return 0f;
            float phase = x * f - Mathf.Floor(x * f);
            float tri = 4f * Mathf.Abs(phase - 0.5f) - 1f;
            return tri * Mathf.Min(1f, x / 0.01f) * Mathf.Exp(-x / decay);
        }

        private static float WarnSample(float t)
        {
            float s = 0f;
            for (int i = 0; i < 2; i++)
            {
                float x = t - i * 0.26f;
                if (x < 0f || x > 0.2f) continue;
                float env = Mathf.Min(1f, x / 0.01f) * Mathf.Min(1f, (0.2f - x) / 0.03f);
                s += env * (Sine(880f, x) + 0.3f * Sine(1760f, x) + 0.15f * Sine(2640f, x));
            }
            return s;
        }

        private static float InfoSample(float t) => Bell(Note(81), t, 0f, 0.35f) + 0.6f * Bell(Note(88), t, 0.07f, 0.45f);
    }
}
