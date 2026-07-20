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

                var folder = await DownloadsFolder.CreateFileAsync(filename, CreationCollisionOption.GenerateUniqueName);
                using (stream)
                using (var outStream = await folder.OpenStreamForWriteAsync())
                {
                    var buffer = new byte[4096];
                    long received = 0;
                    int read;
                    while ((size <= 0 || received < size) &&
                           (read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        outStream.Write(buffer, 0, read);
                        received += read;
                    }
                    await outStream.FlushAsync();
                    if (size > 0 && received != size)
                    {
                        _ctx?.Log?.Invoke($"share: size mismatch {received}/{size}, deleting");
                        await folder.DeleteAsync();
                        return;
                    }
                }
                StartupTrace.Mark($"share-saved:{folder.Name}");
                _ctx?.Log?.Invoke($"share: saved {folder.Name}");
                Toast("File received", folder.Name);

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
