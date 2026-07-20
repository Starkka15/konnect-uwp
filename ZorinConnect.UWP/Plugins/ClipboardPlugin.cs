using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Core;
using ZorinConnect.Core;

namespace ZorinConnect.Plugins
{
    /// <summary>
    /// Clipboard (SPEC T18). Bidirectional clipboard sync.
    ///  - kdeconnect.clipboard {content}: apply verbatim.
    ///  - kdeconnect.clipboard.connect {timestamp ms, content}: apply iff timestamp!=0 &amp;&amp; ts&gt;=local.
    /// On connect we send our current clipboard as .connect. Clipboard APIs are UI-thread-affine,
    /// so all access is marshaled to the UI dispatcher (auto-read only fires while foreground).
    /// </summary>
    public sealed class ClipboardPlugin : IPlugin
    {
        private const string ClipboardType = "kdeconnect.clipboard";
        private const string ConnectType = "kdeconnect.clipboard.connect";

        private PluginContext _ctx;
        private string _localContent = "";
        private long _localTimestamp;      // ms since epoch of the last local change we know of
        private bool _applyingRemote;      // guard: don't echo a remote-applied change back

        public string Key => "ClipboardPlugin";
        public string DisplayName => "Clipboard";
        public bool EnabledByDefault => true;
        public IEnumerable<string> SupportedPacketTypes => new[] { ClipboardType, ConnectType };
        public IEnumerable<string> OutgoingPacketTypes => new[] { ClipboardType, ConnectType };

        public static ClipboardPlugin Current { get; private set; }

        public void OnCreate(PluginContext context)
        {
            _ctx = context;
            Current = this;
            RunOnUi(() =>
            {
                Clipboard.ContentChanged += OnClipboardChanged;
                var _ = SendConnectAsync();
            });
        }

        public void OnDestroy()
        {
            RunOnUi(() => Clipboard.ContentChanged -= OnClipboardChanged);
            if (Current == this) Current = null;
        }

        public bool OnPacketReceived(NetworkPacket np)
        {
            if (np.Type == ClipboardType)
            {
                ApplyRemote(np.GetString("content"), NowMs());
                return true;
            }
            if (np.Type == ConnectType)
            {
                long ts = np.GetLong("timestamp");
                if (ts != 0 && ts >= _localTimestamp && np.Has("content"))
                    ApplyRemote(np.GetString("content"), ts);
                return true;
            }
            return false;
        }

        // ---- outgoing ----

        private async void OnClipboardChanged(object sender, object e)
        {
            if (_applyingRemote) return;
            var text = await ReadClipboardAsync();
            if (text == null || text == _localContent) return;
            _localContent = text;
            _localTimestamp = NowMs();
            _ctx?.SendPacket(new NetworkPacket(ClipboardType).Set("content", text));
            _ctx?.Log?.Invoke($"clipboard -> desktop ({text.Length} chars)");
        }

        private async Task SendConnectAsync()
        {
            var text = await ReadClipboardAsync();
            _localContent = text ?? "";
            var np = new NetworkPacket(ConnectType)
                .Set("timestamp", _localTimestamp)
                .Set("content", _localContent);
            _ctx?.SendPacket(np);
        }

        /// <summary>Manual "send clipboard" (button) — always pushes current content.</summary>
        public async void SendCurrent()
        {
            var text = await ReadClipboardAsync();
            if (text == null) return;
            _localContent = text;
            _localTimestamp = NowMs();
            _ctx?.SendPacket(new NetworkPacket(ClipboardType).Set("content", text));
        }

        // ---- incoming ----

        private void ApplyRemote(string content, long timestamp)
        {
            if (content == null) return;
            _localContent = content;
            _localTimestamp = timestamp;
            RunOnUi(() =>
            {
                try
                {
                    _applyingRemote = true;
                    var pkg = new DataPackage();
                    pkg.SetText(content);
                    Clipboard.SetContent(pkg);
                    Clipboard.Flush(); // persist so it survives the app closing
                }
                catch (Exception e) { _ctx?.Log?.Invoke($"clipboard set failed: {e.Message}"); }
                finally { _applyingRemote = false; }
            });
            _ctx?.Log?.Invoke($"clipboard <- desktop ({content.Length} chars)");
        }

        // ---- helpers ----

        private static async Task<string> ReadClipboardAsync()
        {
            string result = null;
            var tcs = new TaskCompletionSource<bool>();
            RunOnUi(async () =>
            {
                try
                {
                    var view = Clipboard.GetContent();
                    if (view.Contains(StandardDataFormats.Text))
                        result = await view.GetTextAsync();
                }
                catch { }
                finally { tcs.TrySetResult(true); }
            });
            await tcs.Task;
            return result;
        }

        private static void RunOnUi(DispatchedHandler h)
        {
            var disp = CoreApplication.MainView?.CoreWindow?.Dispatcher;
            if (disp != null && !disp.HasThreadAccess)
                _ = disp.RunAsync(CoreDispatcherPriority.Normal, h);
            else
                h();
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
