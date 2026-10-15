using System;
using UnityEngine;

namespace XRCollab.Measurement.Recording
{
    /// <summary>
    /// Argumentos de lanzamiento: «-nombre valor» de la línea de comandos (PC) o, en el visor (Android), el extra del
    /// intent «nombre» sin guion: <c>adb shell am start -n cl.dictuc.xrcollab.measurement/com.unity3d.player.UnityPlayerActivity --es acq-ip 192.168.88.197</c>.
    /// </summary>
    public static class LaunchArgs
    {
        public static string Get(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            if (i >= 0 && i + 1 < args.Length) return args[i + 1];
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (AndroidJavaObject intent = activity.Call<AndroidJavaObject>("getIntent"))
                {
                    string v = intent.Call<string>("getStringExtra", name.TrimStart('-'));
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch (Exception e) { Debug.LogWarning($"[LaunchArgs] intent {name}: {e.Message}"); }
#endif
            return null;
        }
    }
}
