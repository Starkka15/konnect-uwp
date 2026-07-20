using System;
using System.Collections.Generic;
using Windows.ApplicationModel.Core;
using Windows.Foundation.Metadata;
using Windows.Media.Playback;
using Windows.Media.Core;
using Windows.Phone.Devices.Notification;
using Windows.UI.Core;
using Windows.UI.Xaml;
using ZorinConnect.Core;

namespace ZorinConnect.Plugins
{
    /// <summary>
    /// FindMyPhone (SPEC T17). Receives kdeconnect.findmyphone.request -> ring loop at max volume
    /// + vibrate + full-screen dismiss overlay. FindRemoteDevice send side is a separate action
    /// (RingRemote) using the same packet aimed at the desktop.
    /// </summary>
    public sealed class FindMyPhonePlugin : IPlugin
    {
        private const string RequestType = "kdeconnect.findmyphone.request";

        private PluginContext _ctx;
        private MediaPlayer _player;
        private DispatcherTimer _vibrateTimer;
        private bool _ringing;

        public event Action RingStarted;   // UI shows dismiss overlay
        public event Action RingStopped;

        public string Key => "FindMyPhonePlugin";
        public string DisplayName => "Find My Phone";
        public bool EnabledByDefault => true;
        // Phone RECEIVES findmyphone (ring me); also SENDS it (ring the desktop) = FindRemoteDevice.
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
            RunOnUi(StartRing); // packet arrives on the link's background thread; ring is UI-affine
            return true;
        }

        /// <summary>Marshal to the UI thread — MediaPlayer/DispatcherTimer/VibrationDevice are
        /// thread-affine, and packets arrive on a background read-loop thread (RPC_E_WRONG_THREAD).</summary>
        private static void RunOnUi(Windows.UI.Core.DispatchedHandler h)
        {
            var disp = CoreApplication.MainView?.CoreWindow?.Dispatcher;
            if (disp != null && !disp.HasThreadAccess)
                _ = disp.RunAsync(CoreDispatcherPriority.High, h);
            else
                h();
        }

        /// <summary>FindRemoteDevice: ring the desktop.</summary>
        public void RingRemote() => _ctx?.SendPacket(new NetworkPacket(RequestType));

        public bool IsRinging => _ringing;

        public void StartRing()
        {
            if (_ringing) return;
            _ringing = true;

            try
            {
                _player = new MediaPlayer
                {
                    IsLoopingEnabled = true,
                    AudioCategory = MediaPlayerAudioCategory.Alerts,
                    Volume = 1.0,
                };
                _player.Source = MediaSource.CreateFromUri(
                    new Uri("ms-winsoundevent:Notification.Looping.Alarm"));
                _player.Play();
            }
            catch (Exception e) { _ctx?.Log?.Invoke($"ring audio failed: {e.Message}"); }

            StartVibration();
            RingStarted?.Invoke();
        }

        public void StopRing()
        {
            if (!_ringing) return;
            _ringing = false;
            RunOnUi(() =>
            {
                try { _player?.Pause(); _player?.Dispose(); } catch { }
                _player = null;
                StopVibration();
                RingStopped?.Invoke();
            });
        }

        // ---- vibration (phone only; VibrationDevice is on the mobile extension SDK) ----

        private VibrationDevice _vibrator;

        private void StartVibration()
        {
            if (!ApiInformation.IsTypePresent("Windows.Phone.Devices.Notification.VibrationDevice")) return;
            try
            {
                _vibrator = VibrationDevice.GetDefault();
                _vibrateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                _vibrateTimer.Tick += (s, e) => { try { _vibrator?.Vibrate(TimeSpan.FromMilliseconds(600)); } catch { } };
                _vibrateTimer.Start();
                _vibrator.Vibrate(TimeSpan.FromMilliseconds(600));
            }
            catch (Exception e) { _ctx?.Log?.Invoke($"vibrate failed: {e.Message}"); }
        }

        private void StopVibration()
        {
            try { _vibrateTimer?.Stop(); } catch { }
            _vibrateTimer = null;
            try { _vibrator?.Cancel(); } catch { }
            _vibrator = null;
        }
    }
}
