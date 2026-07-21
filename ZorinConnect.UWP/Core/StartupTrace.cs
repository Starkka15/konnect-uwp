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
                    // TRUNCATE the mirror file at each run start so it can't grow unbounded (was 50MB).
                    MirrorToFile($"PREV-RUN-FULL=[{prev}]", truncate: true);
                }
                Buffer.Append(stage).Append(';');
                if (Buffer.Length > 4000) Buffer.Remove(0, Buffer.Length - 4000); // cap: no unbounded growth
                settings.Values[KeyCurrent] = Buffer.ToString();
                MirrorToFile(stage); // append ONLY the new mark (was: the whole buffer -> O(n^2) memory/IO -> crash)
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

        private static void MirrorToFile(string content, bool truncate = false)
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await FileLock.WaitAsync();
                try
                {
                    var file = await Windows.Storage.KnownFolders.MusicLibrary.CreateFileAsync(
                        "zctrace.txt", truncate ? CreationCollisionOption.ReplaceExisting
                                               : CreationCollisionOption.OpenIfExists);
                    if (truncate) await FileIO.WriteTextAsync(file, $"{stamp} {content}\n");
                    else await FileIO.AppendTextAsync(file, $"{stamp} {content}\n");
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
