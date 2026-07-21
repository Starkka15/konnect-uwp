using System.Collections.Generic;
using Windows.UI.Notifications;
using ZorinConnect.Core;

namespace ZorinConnect.Plugins
{
    /// <summary>
    /// Ping (SPEC T15). Both directions: kdeconnect.ping, optional "message" field.
    /// Recv -> toast (title = device name, text = message or "Ping!"). Send -> empty ping.
    /// </summary>
    public sealed class PingPlugin : IPlugin
    {
        private const string PacketType = "kdeconnect.ping";

        private PluginContext _ctx;

        public string Key => "PingPlugin";
        public string DisplayName => "Ping";
        public bool EnabledByDefault => true;
        public IEnumerable<string> SupportedPacketTypes => new[] { PacketType };
        public IEnumerable<string> OutgoingPacketTypes => new[] { PacketType };

        public void OnCreate(PluginContext context) => _ctx = context;
        public void OnDestroy() { }

        public bool OnPacketReceived(NetworkPacket np)
        {
            if (np.Type != PacketType) return false;
            var message = np.Has("message") ? np.GetString("message") : "Ping!";
            _ctx?.Log?.Invoke($"ping from {_ctx.Device.Name}: {message}");
            ShowToast(_ctx?.Device?.Name ?? "Konnect UWP", message);
            return true;
        }

        /// <summary>Send an empty ping to the peer (context-menu action).</summary>
        public void SendPing()
        {
            _ctx?.SendPacket(new NetworkPacket(PacketType));
        }

        private static void ShowToast(string title, string body)
        {
            try
            {
                var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var texts = xml.GetElementsByTagName("text");
                texts[0].AppendChild(xml.CreateTextNode(title));
                texts[1].AppendChild(xml.CreateTextNode(body));
                ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
            }
            catch { }
        }
    }
}
