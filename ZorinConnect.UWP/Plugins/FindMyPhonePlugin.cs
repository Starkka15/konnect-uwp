using System;
using System.Collections.Generic;
using Windows.Data.Xml.Dom;
using Windows.Foundation.Metadata;
using Windows.Phone.Devices.Notification;
using Windows.System.Threading;
using Windows.UI.Notifications;
using ZorinConnect.Core;
using ZorinConnect.Helpers;

namespace ZorinConnect.Plugins
{
    /// <summary>
    /// FindMyPhone (SPEC T17). Rings the phone on kdeconnect.findmyphone.request.
    /// The ring is a scenario="alarm" TOAST with looping audio — this is the only mechanism that
    /// plays on the LOCK SCREEN / from a backgrounded or socket-woken app (MediaPlayer on the UI
    /// thread only plays once foregrounded). Vibration uses a ThreadPoolTimer (no UI thread).
    /// </summary>
    public sealed class FindMyPhonePlugin : IPlugin
    {
        private const string RequestType = "kdeconnect.findmyphone.request";
        private const string ToastTag = "zc-findmyphone";

        private PluginContext _ctx;
        private ToastNotification _toast;
        private ThreadPoolTimer _vibrateTimer;
        private VibrationDevice _vibrator;
        private bool _ringing;

        public event Action RingStarted;   // in-app overlay (foreground only)
        public event Action RingStopped;

        public string Key => "FindMyPhonePlugin";
        public string DisplayName => "Find My Phone";
        public bool EnabledByDefault => true;
        public IEnumerable<string> SupportedPacketTypes => new[] { RequestType };
        public IEnumerable<string> OutgoingPacketTypes => new[] { RequestType };

        public static FindMyPhonePlugin Current { get; private set; }

        public void OnCreate(PluginContext context) { _ctx = context; Current = this; }

        public void OnDestroy()
        {
            StopRing();
            if (Current == this) Current = null;
        }

        public bool OnPacketReceived(NetworkPacket np)
        {
            if (np.Type != RequestType) return false;
            _ctx?.Log?.Invoke("findmyphone: ring requested");
            StartRing(); // NOT UI-marshaled — must work while locked / backgrounded
            return true;
        }

        public void RingRemote() => _ctx?.SendPacket(new NetworkPacket(RequestType));

        public bool IsRinging => _ringing;

        public void StartRing()
        {
            if (_ringing) return;
            _ringing = true;
            ShowAlarmToast();      // audio on lock screen, any thread
            StartVibration();      // ThreadPoolTimer, any thread
            RingStarted?.Invoke(); // in-app overlay if/when foreground
        }

        public void StopRing()
        {
            if (!_ringing) return;
            _ringing = false;
            HideAlarmToast();
            StopVibration();
            RingStopped?.Invoke();
        }

        // ---- alarm toast (plays looping audio on the lock screen) ----

        private void ShowAlarmToast()
        {
            try
            {
                var name = SecurityEscape(DeviceHelper.DeviceName);
                // scenario="alarm" -> loops audio + persists until dismissed, shows on lock screen.
                var xml =
                    "<toast scenario='alarm' launch='findmyphone'>" +
                    "<visual><binding template='ToastGeneric'>" +
                    "<text>Find My Phone</text>" +
                    "<text>" + name + " is ringing</text>" +
                    "</binding></visual>" +
                    "<audio src='ms-appx:///Assets/findmyphone.wav' loop='true'/>" +
                    "<actions><action content='Found it' arguments='dismiss' activationType='foreground'/></actions>" +
                    "</toast>";
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                _toast = new ToastNotification(doc) { Tag = ToastTag };
                _toast.Dismissed += (s, e) => StopRing(); // user dismissed -> stop vibration/state
                _toast.Activated += (s, e) => StopRing();
                ToastNotificationManager.CreateToastNotifier().Show(_toast);
            }
            catch (Exception e) { _ctx?.Log?.Invoke($"alarm toast failed: {e.Message}"); }
        }

        private void HideAlarmToast()
        {
            try
            {
                var notifier = ToastNotificationManager.CreateToastNotifier();
                if (_toast != null) notifier.Hide(_toast);
                _toast = null;
                // Also clear any lingering copy from history.
                ToastNotificationManager.History?.Remove(ToastTag);
            }
            catch { }
        }

        private static string SecurityEscape(string s) =>
            (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&apos;");

        // ---- vibration (ThreadPoolTimer — no UI dispatcher needed) ----

        private void StartVibration()
        {
            if (!ApiInformation.IsTypePresent("Windows.Phone.Devices.Notification.VibrationDevice")) return;
            try
            {
                _vibrator = VibrationDevice.GetDefault();
                _vibrator?.Vibrate(TimeSpan.FromMilliseconds(600));
                _vibrateTimer = ThreadPoolTimer.CreatePeriodicTimer(
                    t => { try { _vibrator?.Vibrate(TimeSpan.FromMilliseconds(600)); } catch { } },
                    TimeSpan.FromMilliseconds(1200));
            }
            catch (Exception e) { _ctx?.Log?.Invoke($"vibrate failed: {e.Message}"); }
        }

        private void StopVibration()
        {
            try { _vibrateTimer?.Cancel(); } catch { }
            _vibrateTimer = null;
            try { _vibrator?.Cancel(); } catch { }
            _vibrator = null;
        }
    }
}
