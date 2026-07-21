using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation.Metadata;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using ZorinConnect.Core;

namespace ZorinConnect.Plugins
{
    /// <summary>
    /// Notifications mirror (SPEC T28). Phone's notifications -> desktop via kdeconnect.notification
    /// (UserNotificationListener, confirmed viable by T31). Polls for changes (no reliable in-proc
    /// change event without a background task). Actions/inline-reply are NOT advertised — W10M can't
    /// invoke another app's toast actions, so we never send actions[]/requestReplyId (SPEC §V10).
    /// </summary>
    public sealed class NotificationsPlugin : IPlugin
    {
        private const string PacketType = "kdeconnect.notification";
        private const string RequestType = "kdeconnect.notification.request";

        private PluginContext _ctx;
        private UserNotificationListener _listener;
        private Windows.System.Threading.ThreadPoolTimer _poll;
        private readonly HashSet<uint> _sent = new HashSet<uint>();

        public string Key => "NotificationsPlugin";
        public string DisplayName => "Notifications";
        public bool EnabledByDefault => true;
        public IEnumerable<string> SupportedPacketTypes => new[] { RequestType };
        public IEnumerable<string> OutgoingPacketTypes => new[] { PacketType };

        public void OnCreate(PluginContext context)
        {
            _ctx = context;
            var _ = InitAsync();
        }

        public void OnDestroy()
        {
            try { _poll?.Cancel(); } catch { }
            _poll = null;
            _listener = null;
        }

        private async Task InitAsync()
        {
            try
            {
                if (!ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener"))
                    return;
                _listener = UserNotificationListener.Current;
                var status = await _listener.RequestAccessAsync();
                StartupTrace.Mark($"notif-access:{status}");
                if (status != UserNotificationListenerAccessStatus.Allowed) return;

                await PushCurrentAsync(silent: true); // seed the desktop
                // Poll for new/removed notifications every 3s (no in-proc change event).
                _poll = Windows.System.Threading.ThreadPoolTimer.CreatePeriodicTimer(
                    async t => { try { await PushCurrentAsync(silent: false); } catch { } },
                    TimeSpan.FromSeconds(3));
            }
            catch (Exception e) { _ctx?.Log?.Invoke($"notif init failed: {e.Message}"); }
        }

        public bool OnPacketReceived(NetworkPacket np)
        {
            if (np.Type != RequestType) return false;
            if (np.GetBool("request")) { var _ = PushCurrentAsync(silent: true); return true; }
            if (np.Has("cancel")) { var __ = CancelAsync(np.GetString("cancel")); return true; }
            return true;
        }

        // ---- outgoing ----

        private async Task PushCurrentAsync(bool silent)
        {
            if (_listener == null) return;
            IReadOnlyList<UserNotification> notifs;
            try { notifs = await _listener.GetNotificationsAsync(NotificationKinds.Toast); }
            catch { return; }

            var current = new HashSet<uint>();
            foreach (var un in notifs)
            {
                current.Add(un.Id);
                if (!silent && _sent.Contains(un.Id)) continue; // already mirrored
                try { SendNotification(un, silent || _sent.Contains(un.Id)); _sent.Add(un.Id); }
                catch { }
            }
            // Tell the desktop about notifications that disappeared from the phone.
            foreach (var goneId in _sent.Where(id => !current.Contains(id)).ToList())
            {
                _sent.Remove(goneId);
                _ctx?.SendPacket(new NetworkPacket(PacketType).Set("id", goneId.ToString()).Set("isCancel", true));
            }
        }

        private void SendNotification(UserNotification un, bool silent)
        {
            string appName = "";
            try { appName = un.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }

            string title = "", body = "";
            try
            {
                var binding = un.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                var texts = binding?.GetTextElements();
                if (texts != null && texts.Count > 0)
                {
                    title = texts[0].Text ?? "";
                    body = string.Join("\n", texts.Skip(1).Select(t => t.Text).Where(s => !string.IsNullOrEmpty(s)));
                }
            }
            catch { }

            if (string.IsNullOrEmpty(appName) && string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body)) return;

            string ticker = string.IsNullOrEmpty(title) ? body : (string.IsNullOrEmpty(body) ? title : $"{title}: {body}");
            long time = 0;
            try { time = un.CreationTime.ToUnixTimeMilliseconds(); } catch { }

            var np = new NetworkPacket(PacketType)
                .Set("id", un.Id.ToString())
                .Set("appName", appName)
                .Set("ticker", ticker)
                .Set("title", title)
                .Set("text", body)
                .Set("time", time.ToString())
                .Set("isClearable", true)
                .Set("silent", silent);
            _ctx?.SendPacket(np);
        }

        // ---- incoming: desktop dismissed a notification ----

        private Task CancelAsync(string idStr)
        {
            if (_listener != null && uint.TryParse(idStr, out var id))
            {
                try { _listener.RemoveNotification(id); } catch (Exception e) { _ctx?.Log?.Invoke($"notif remove failed: {e.Message}"); }
                _sent.Remove(id);
            }
            return Task.CompletedTask;
        }
    }
}
