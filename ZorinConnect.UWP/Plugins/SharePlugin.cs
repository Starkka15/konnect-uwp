using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Windows.UI.Notifications;
using Windows.Data.Xml.Dom;
using ZorinConnect.Core;

namespace ZorinConnect.Plugins
{
    /// <summary>
    /// Share (SPEC T19). Receives kdeconnect.share.request:
    ///  - {filename, numberOfFiles, totalPayloadSize} + payload -> save to Downloads.
    ///  - {text} -> copy to clipboard + toast.
    ///  - {url}  -> open in browser.
    /// Sending files uses the payload channel (SendFileAsync).
    /// </summary>
    public sealed class SharePlugin : IPlugin
    {
        private const string RequestType = "kdeconnect.share.request";

        private PluginContext _ctx;

        public string Key => "SharePlugin";
        public string DisplayName => "Share";
        public bool EnabledByDefault => true;
        public IEnumerable<string> SupportedPacketTypes => new[] { RequestType };
        public IEnumerable<string> OutgoingPacketTypes => new[] { RequestType };

        public static SharePlugin Current { get; private set; }

        public void OnCreate(PluginContext context) { _ctx = context; Current = this; }
        public void OnDestroy() { if (Current == this) Current = null; }

        public bool OnPacketReceived(NetworkPacket np)
        {
            if (np.Type != RequestType) return false;

            if (np.Has("filename") && np.HasPayloadTransferInfo)
            {
                var _ = ReceiveFileAsync(np);
                return true;
            }
            if (np.Has("text"))
            {
                SetClipboardText(np.GetString("text"));
                Toast("Text received", np.GetString("text"));
                return true;
            }
            if (np.Has("url"))
            {
                var url = np.GetString("url");
                var _ = Launcher.LaunchUriAsync(new Uri(url));
                Toast("Link received", url);
                return true;
            }
            return false;
        }

        private async Task ReceiveFileAsync(NetworkPacket np)
        {
            var filename = np.GetString("filename");
            long size = np.GetLong("totalPayloadSize", np.PayloadSize);
            StartupTrace.Mark($"share-recv:{filename} size={size} port={np.PayloadPort} psize={np.PayloadSize}");
            try
            {
                var stream = await _ctx.OpenIncomingPayloadAsync(np);
                if (stream == null) { StartupTrace.Mark("share-nopayload"); _ctx?.Log?.Invoke("share: no payload stream"); return; }
                StartupTrace.Mark("share-payload-open");

                // DownloadsFolder on W10M writes to an opaque location the Files app doesn't show.
                // Save to a "Zorin Connect" folder inside the matching media library instead —
                // those ARE browsable (Files app -> Pictures/Music/Videos).
                var destFolder = await TargetFolderAsync(filename);
                var folder = await destFolder.CreateFileAsync(filename, CreationCollisionOption.GenerateUniqueName);
                var tag = "zc-recv-" + Math.Abs(filename.GetHashCode());
                ShowProgressToast(tag, filename);
                uint seq = 1;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (stream)
                using (var outStream = await folder.OpenStreamForWriteAsync())
                {
                    var buffer = new byte[8192];
                    long received = 0;
                    long lastReport = 0;
                    int read;
                    while ((size <= 0 || received < size) &&
                           (read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        outStream.Write(buffer, 0, read);
                        received += read;
                        // Update the toast progress at most every ~400ms (Android throttles at 500).
                        if (size > 0 && sw.ElapsedMilliseconds - lastReport >= 400)
                        {
                            lastReport = sw.ElapsedMilliseconds;
                            UpdateProgress(tag, (double)received / size, $"{Human(received)} / {Human(size)}", seq++);
                        }
                    }
                    await outStream.FlushAsync();
                    if (size > 0 && received != size)
                    {
                        _ctx?.Log?.Invoke($"share: size mismatch {received}/{size}, deleting");
                        await folder.DeleteAsync();
                        FinishProgressToast(tag, filename, false, seq);
                        return;
                    }
                }
                var loc = LibraryLabel(filename) + "\\" + ReceivedFolderName;
                StartupTrace.Mark($"share-saved:{folder.Name}");
                _ctx?.Log?.Invoke($"share: saved {folder.Name} -> {loc}");
                FinishProgressToast(tag, $"{folder.Name}  ({loc})", true, seq);

                if (np.GetBool("open"))
                    await Launcher.LaunchFileAsync(folder);
            }
            catch (Exception e) { StartupTrace.MarkError("share-recv", e); _ctx?.Log?.Invoke($"share receive failed: {e.Message}"); }
        }

        /// <summary>Send a file to the desktop over the payload channel.</summary>
        public async Task SendFileAsync(StorageFile file)
        {
            try
            {
                var props = await file.GetBasicPropertiesAsync();
                long size = (long)props.Size;
                var source = await file.OpenStreamForReadAsync();
                var np = new NetworkPacket(RequestType)
                    .Set("filename", file.Name)
                    .Set("numberOfFiles", 1)
                    .Set("totalPayloadSize", size)
                    .Set("lastModified", props.DateModified.ToUnixTimeMilliseconds());
                await _ctx.SendPacketWithPayloadAsync(np, source, size, null);
                _ctx?.Log?.Invoke($"share: sending {file.Name} ({size} bytes)");
            }
            catch (Exception e) { _ctx?.Log?.Invoke($"share send failed: {e.Message}"); }
        }

        public void SendText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _ctx?.SendPacket(new NetworkPacket(RequestType).Set("text", text));
        }

        // ---- helpers ----

        private static void SetClipboardText(string text)
        {
            try
            {
                var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                pkg.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
            }
            catch { }
        }

        private const string ProgressGroup = "zc-share";
        private const string ReceivedFolderName = "Zorin Connect";

        /// <summary>
        /// Pick a browsable library by file type and return its "Zorin Connect" subfolder. W10M's
        /// DownloadsFolder is not surfaced by the Files app, so we route to Pictures/Music/Videos
        /// (which are). Non-media falls back to Pictures (always present + browsable).
        /// </summary>
        private static async Task<StorageFolder> TargetFolderAsync(string filename)
        {
            var ext = System.IO.Path.GetExtension(filename ?? "").ToLowerInvariant();
            StorageFolder lib;
            if (IsAudio(ext)) lib = KnownFolders.MusicLibrary;
            else if (IsVideo(ext)) lib = KnownFolders.VideosLibrary;
            else if (IsImage(ext)) lib = KnownFolders.PicturesLibrary;
            else lib = KnownFolders.PicturesLibrary; // catch-all (browsable)
            try { return await lib.CreateFolderAsync(ReceivedFolderName, CreationCollisionOption.OpenIfExists); }
            catch { return lib; }
        }

        private static string LibraryLabel(string filename)
        {
            var e = System.IO.Path.GetExtension(filename ?? "").ToLowerInvariant();
            if (IsAudio(e)) return "Music";
            if (IsVideo(e)) return "Videos";
            return "Pictures";
        }

        private static bool IsAudio(string e) => e == ".wav" || e == ".mp3" || e == ".flac" || e == ".m4a" || e == ".aac" || e == ".ogg" || e == ".wma";
        private static bool IsVideo(string e) => e == ".mp4" || e == ".mkv" || e == ".avi" || e == ".mov" || e == ".wmv" || e == ".webm" || e == ".m4v";
        private static bool IsImage(string e) => e == ".jpg" || e == ".jpeg" || e == ".png" || e == ".gif" || e == ".bmp" || e == ".webp" || e == ".heic";

        private void ShowProgressToast(string tag, string filename)
        {
            try
            {
                var xml =
                    "<toast>" +
                    "<visual><binding template='ToastGeneric'>" +
                    "<text>Receiving file</text>" +
                    "<text>" + XmlEscape(filename) + "</text>" +
                    "<progress value='{progressValue}' status='{progressStatus}' valueStringOverride='{progressString}'/>" +
                    "</binding></visual></toast>";
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                var toast = new ToastNotification(doc) { Tag = tag, Group = ProgressGroup };
                var data = new NotificationData();
                data.SequenceNumber = 0;
                data.Values["progressValue"] = "0";
                data.Values["progressStatus"] = "Receiving…";
                data.Values["progressString"] = "0%";
                toast.Data = data;
                ToastNotificationManager.CreateToastNotifier().Show(toast);
            }
            catch (Exception e) { _ctx?.Log?.Invoke($"progress toast failed: {e.Message}"); }
        }

        private void UpdateProgress(string tag, double frac, string valueStr, uint seq)
        {
            try
            {
                var data = new NotificationData { SequenceNumber = seq };
                data.Values["progressValue"] = frac.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                data.Values["progressStatus"] = "Receiving…";
                data.Values["progressString"] = valueStr;
                ToastNotificationManager.CreateToastNotifier().Update(data, tag, ProgressGroup);
            }
            catch { }
        }

        private void FinishProgressToast(string tag, string name, bool ok, uint seq)
        {
            try
            {
                var notifier = ToastNotificationManager.CreateToastNotifier();
                var data = new NotificationData { SequenceNumber = seq };
                data.Values["progressValue"] = ok ? "1.0" : "0";
                data.Values["progressStatus"] = ok ? "Saved to Downloads" : "Transfer failed";
                data.Values["progressString"] = name;
                notifier.Update(data, tag, ProgressGroup);
            }
            catch { }
        }

        private static string Human(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
        }

        private static string XmlEscape(string s) =>
            (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&apos;");

        private static void Toast(string title, string body)
        {
            try
            {
                var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var texts = xml.GetElementsByTagName("text");
                texts[0].AppendChild(xml.CreateTextNode(title));
                texts[1].AppendChild(xml.CreateTextNode(body.Length > 100 ? body.Substring(0, 100) : body));
                ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
            }
            catch { }
        }
    }
}
