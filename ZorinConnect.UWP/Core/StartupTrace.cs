using System;
using System.Text;
using Windows.Storage;

namespace ZorinConnect.Core
{
    /// <summary>
    /// Crash forensics: stage markers written synchronously to LocalSettings so the trace of a
    /// dying run is readable on the NEXT run (and on-screen). Diagnostics stay in until release.
    /// </summary>
    public static class StartupTrace
    {
        private const string KeyCurrent = "trace_current";
        private const string KeyPrevious = "trace_previous";
        private static readonly StringBuilder Buffer = new StringBuilder();
        private static bool _rotated;

        public static void Mark(string stage)
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (!_rotated)
                {
                    _rotated = true;
                    settings.Values[KeyPrevious] = settings.Values.ContainsKey(KeyCurrent) ? settings.Values[KeyCurrent] : "";
                    settings.Values[KeyCurrent] = "";
                }
                Buffer.Append(stage).Append(';');
                var s = Buffer.ToString();
                settings.Values[KeyCurrent] = s.Length > 4000 ? s.Substring(s.Length - 4000) : s;
            }
            catch
            {
                // tracing must never take the app down
            }
        }

        public static void MarkError(string stage, Exception ex)
        {
            Mark($"{stage}!ERR[{ex.GetType().Name}:{Trim(ex.Message)}]");
        }

        public static string PreviousRun()
        {
            try { return ApplicationData.Current.LocalSettings.Values[KeyPrevious] as string ?? ""; }
            catch { return ""; }
        }

        private static string Trim(string s) => s == null ? "" : (s.Length > 200 ? s.Substring(0, 200) : s);
    }
}
