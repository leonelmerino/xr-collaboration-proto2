using System;
using System.Globalization;
using UnityEngine;

namespace XRCollab.Measurement.Cues
{
    /// <summary>Tono de la señal: define colores, ícono por defecto, sonido y animación.</summary>
    public enum CueTone { Great, Good, Bad, Slow, Warn, Info }

    public enum CueIcon { Auto, None, Check, Cross, Clock, Star, Warning, Bolt }

    /// <summary>
    /// Una señal 3D pedida desde afuera: «tono|ícono|título|subtítulo|segundos» (solo el título es obligatorio en la
    /// práctica; lo demás tiene valores por defecto). Ej.: «bad|cross|MAL COLOCADO|El bloque quedó torcido».
    /// Tonos: great good bad slow warn info. Íconos: auto none check cross clock star warning bolt.
    /// </summary>
    public sealed class FeedbackCueSpec
    {
        public const float DefaultHoldSeconds = 2.4f;
        public const int MaxTitleLength = 32;
        public const int MaxSubtitleLength = 60;

        public CueTone Tone = CueTone.Info;
        public CueIcon Icon = CueIcon.Auto;
        public string Title = "";
        public string Subtitle = "";
        public float HoldSeconds = DefaultHoldSeconds;

        public CueIcon ResolvedIcon => Icon != CueIcon.Auto ? Icon : DefaultIcon(Tone);

        /// <returns>null y <paramref name="error"/> si el texto no sirve.</returns>
        public static FeedbackCueSpec Parse(string text, out string error)
        {
            error = null;
            string[] parts = (text ?? "").Split('|');
            var spec = new FeedbackCueSpec();
            if (parts.Length == 1)
            {
                spec.Title = Clean(parts[0], MaxTitleLength);
            }
            else
            {
                if (!TryTone(parts[0].Trim(), out spec.Tone)) { error = $"tono desconocido '{parts[0].Trim()}' (great good bad slow warn info)"; return null; }
                if (parts.Length > 1 && !TryIcon(parts[1].Trim(), out spec.Icon)) { error = $"ícono desconocido '{parts[1].Trim()}' (auto none check cross clock star warning bolt)"; return null; }
                if (parts.Length > 2) spec.Title = Clean(parts[2], MaxTitleLength);
                if (parts.Length > 3) spec.Subtitle = Clean(parts[3], MaxSubtitleLength);
                if (parts.Length > 4 && parts[4].Trim().Length > 0)
                {
                    if (!float.TryParse(parts[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float s) || s < 0.5f || s > 15f)
                    { error = $"segundos inválidos '{parts[4].Trim()}' (0.5 a 15)"; return null; }
                    spec.HoldSeconds = s;
                }
            }
            if (spec.Title.Length == 0 && spec.ResolvedIcon == CueIcon.None) { error = "la señal necesita un título o un ícono"; return null; }
            return spec;
        }

        public override string ToString() => $"{Tone.ToString().ToLowerInvariant()}|{ResolvedIcon.ToString().ToLowerInvariant()}|{Title}|{Subtitle}";

        public static CueIcon DefaultIcon(CueTone tone)
        {
            switch (tone)
            {
                case CueTone.Great: return CueIcon.Star;
                case CueTone.Good: return CueIcon.Check;
                case CueTone.Bad: return CueIcon.Cross;
                case CueTone.Slow: return CueIcon.Clock;
                case CueTone.Warn: return CueIcon.Warning;
                default: return CueIcon.None;
            }
        }

        private static bool TryTone(string s, out CueTone tone)
        {
            switch (s.ToLowerInvariant())
            {
                case "great": case "excelente": tone = CueTone.Great; return true;
                case "good": case "bien": tone = CueTone.Good; return true;
                case "bad": case "mal": tone = CueTone.Bad; return true;
                case "slow": case "lento": tone = CueTone.Slow; return true;
                case "warn": case "cuidado": tone = CueTone.Warn; return true;
                case "info": case "": tone = CueTone.Info; return true;
                default: tone = CueTone.Info; return false;
            }
        }

        private static bool TryIcon(string s, out CueIcon icon)
        {
            switch (s.ToLowerInvariant())
            {
                case "": case "auto": icon = CueIcon.Auto; return true;
                case "none": icon = CueIcon.None; return true;
                case "check": icon = CueIcon.Check; return true;
                case "cross": case "x": icon = CueIcon.Cross; return true;
                case "clock": icon = CueIcon.Clock; return true;
                case "star": icon = CueIcon.Star; return true;
                case "warning": icon = CueIcon.Warning; return true;
                case "bolt": icon = CueIcon.Bolt; return true;
                default: icon = CueIcon.Auto; return false;
            }
        }

        private static string Clean(string s, int max)
        {
            s = (s ?? "").Trim();
            // TMP interpreta etiquetas <...>: se neutralizan para que el texto se muestre tal cual.
            s = s.Replace("<", "‹").Replace(">", "›");
            return s.Length > max ? s.Substring(0, max) : s;
        }
    }

    /// <summary>Colores de cada tono (lineales en el shader; aquí en sRGB como se eligen a ojo).</summary>
    public readonly struct CuePalette
    {
        /// <summary>Cara del texto arriba / abajo (degradé), ícono, laterales (extrusión), brillo aditivo.</summary>
        public readonly Color FaceTop, FaceBottom, Icon, Side, Glow;
        public readonly float Metallic, Smoothness;

        private CuePalette(Color faceTop, Color faceBottom, Color icon, Color side, Color glow, float metallic, float smoothness)
        {
            FaceTop = faceTop; FaceBottom = faceBottom; Icon = icon; Side = side; Glow = glow; Metallic = metallic; Smoothness = smoothness;
        }

        public static CuePalette For(CueTone tone)
        {
            switch (tone)
            {
                case CueTone.Great: return new CuePalette(Hex("#FFF4C2"), Hex("#FFC23A"), Hex("#FFC93C"), Hex("#8A4B00"), Hex("#FFB627"), 0.6f, 0.8f);
                case CueTone.Good: return new CuePalette(Hex("#E8FFEE"), Hex("#4BE07A"), Hex("#3DDC74"), Hex("#0E5A2A"), Hex("#36E07A"), 0.35f, 0.7f);
                case CueTone.Bad: return new CuePalette(Hex("#FFE3DF"), Hex("#FF4A3D"), Hex("#F2382B"), Hex("#5E0B06"), Hex("#FF3B2E"), 0.3f, 0.62f);
                case CueTone.Slow: return new CuePalette(Hex("#FFF0DC"), Hex("#FF9A2E"), Hex("#FF9026"), Hex("#6A3200"), Hex("#FF8A1F"), 0.45f, 0.68f);
                case CueTone.Warn: return new CuePalette(Hex("#FFFBE0"), Hex("#FFD23F"), Hex("#FFCF2E"), Hex("#6B5200"), Hex("#FFD02A"), 0.4f, 0.7f);
                default: return new CuePalette(Hex("#EAF6FF"), Hex("#5BB8FF"), Hex("#4AB0FF"), Hex("#0B3A63"), Hex("#4AB0FF"), 0.35f, 0.72f);
            }
        }

        private static Color Hex(string hex) => ColorUtility.TryParseHtmlString(hex, out Color c) ? c : Color.white;
    }
}
