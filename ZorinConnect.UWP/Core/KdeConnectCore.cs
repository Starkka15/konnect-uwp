using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using ZorinConnect.Backends.Lan;
using ZorinConnect.Helpers;

namespace ZorinConnect.Core
{
    /// <summary>
    /// App-wide singleton: owns the LAN backend and the set of live links.
    /// Grows into the full Device registry in T8 (reachable/paired axes, plugin dispatch).
    /// </summary>
    public sealed class KdeConnectCore
    {
        private static KdeConnectCore _instance;
        public static KdeConnectCore Instance => _instance ?? (_instance = new KdeConnectCore());

        public LanLinkProvider Lan { get; } = new LanLinkProvider();

        /// <summary>deviceId -> (info, link) of currently connected peers.</summary>
        public ConcurrentDictionary<string, Tuple<DeviceInfo, LanLink>> Links { get; }
            = new ConcurrentDictionary<string, Tuple<DeviceInfo, LanLink>>();

        public event Action LinksChanged;
        public event Action<string> Log;

        private bool _started;

        private KdeConnectCore()
        {
            Lan.Log += msg => Log?.Invoke(msg);
            Lan.LinkEstablished += OnLinkEstablished;
        }

        public async Task StartAsync()
        {
            if (_started) return;
            _started = true;
            StartupTrace.Mark("ssl-init");
            await Task.Run(() => SslHelper.EnsureInitialized()); // BC keygen off the UI thread
            StartupTrace.Mark("ssl-init-done");
            await Lan.StartAsync();
            StartupTrace.Mark("lan-started");
        }

        public Task RefreshAsync() => Lan.OnNetworkChangeAsync();

        private void OnLinkEstablished(DeviceInfo info, LanLink link)
        {
            // Persist advertised protocolVersion for the downgrade guard (§I; capabilities NOT persisted, §V20)
            SettingsStore.Set(SettingsStore.ForDevice(info.Id), "protocolVersion", info.ProtocolVersion);
            SettingsStore.Set(SettingsStore.ForDevice(info.Id), "deviceName", info.Name);
            SettingsStore.Set(SettingsStore.ForDevice(info.Id), "deviceType", info.Type.ToProtocolString());

            // Replacement links just overwrite; stale ConnectionLost handlers no-op via the
            // ReferenceEquals guard in OnLinkLost.
            Links[info.Id] = Tuple.Create(info, link);
            link.ConnectionLost += OnLinkLost(info.Id);
            link.PacketReceived += OnPacket;
            LinksChanged?.Invoke();
        }

        private Action<LanLink> OnLinkLost(string deviceId)
        {
            return link =>
            {
                if (Links.TryGetValue(deviceId, out var cur) && ReferenceEquals(cur.Item2, link))
                {
                    Links.TryRemove(deviceId, out _);
                    LinksChanged?.Invoke();
                    Log?.Invoke($"link lost: {deviceId}");
                }
            };
        }

        private void OnPacket(LanLink link, NetworkPacket np)
        {
            Log?.Invoke($"recv {np.Type} from {link.DeviceId}");
            // T8/T9: dispatch to Device -> PairingHandler/plugins
        }
    }
}
