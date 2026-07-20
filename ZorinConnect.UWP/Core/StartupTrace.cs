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
                    var prev = settings.Values.ContainsKey(KeyCurrent) ? settings.Values[KeyCurrent] as string : "";
                    settings.Values[KeyPrevious] = prev;
                    settings.Values[KeyCurrent] = "";
                    // LocalSettings writes are synchronous -> prev holds the dead run's FULL trace.
                    MirrorToFile($"PREV-RUN-FULL=[{prev}]");
                }
                Buffer.Append(stage).Append(';');
                var s = Buffer.ToString();
                settings.Values[KeyCurrent] = s.Length > 4000 ? s.Substring(s.Length - 4000) : s;
                MirrorToFile(s);
            }
            catch
            {
                // tracing must never take the app down
            }
        }

        /// <summary>
        /// Fire-and-forget mirror to MusicLibrary\zctrace.txt — survives crashes AND is readable
        /// off-device via WDP (/api/filesystem/apps/...), unlike LocalSettings.
        /// </summary>
        private static readonly System.Threading.SemaphoreSlim FileLock = new System.Threading.SemaphoreSlim(1, 1);

        private static void MirrorToFile(string content)
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await FileLock.WaitAsync();
                try
                {
                    var file = await Windows.Storage.KnownFolders.MusicLibrary.CreateFileAsync(
                        "zctrace.txt", CreationCollisionOption.OpenIfExists);
                    await FileIO.AppendTextAsync(file, $"{stamp} {content}\n");
                }
                catch { }
                finally { FileLock.Release(); }
            });
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
