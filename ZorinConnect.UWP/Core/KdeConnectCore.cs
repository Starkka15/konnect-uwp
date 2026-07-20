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

        /// <summary>deviceId -> pairing handler for the live link.</summary>
        public ConcurrentDictionary<string, PairingHandler> Pairing { get; }
            = new ConcurrentDictionary<string, PairingHandler>();

        public event Action LinksChanged;
        public event Action<string> Log;
        public event Action<string> PairingChanged; // deviceId
        public event Action RingStarted;
        public event Action RingStopped;

        private bool _started;

        /// <summary>deviceId -> loaded plugins for the paired link.</summary>
        public ConcurrentDictionary<string, System.Collections.Generic.List<ZorinConnect.Plugins.IPlugin>> DevicePlugins { get; }
            = new ConcurrentDictionary<string, System.Collections.Generic.List<ZorinConnect.Plugins.IPlugin>>();

        private KdeConnectCore()
        {
            Lan.Log += msg => Log?.Invoke(msg);
            Lan.LinkEstablished += OnLinkEstablished;
            // Advertise exactly the implemented plugin capabilities (§V10).
            Lan.IncomingCapabilities = ZorinConnect.Plugins.PluginRegistry.AllIncomingCapabilities();
            Lan.OutgoingCapabilities = ZorinConnect.Plugins.PluginRegistry.AllOutgoingCapabilities();
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

            // Always-on: register the socket-activity background task + arm the discovery sockets
            // for standby wake (§T29/T30).
            await BackgroundManager.EnsureRegisteredAsync();
            if (BackgroundManager.Registered)
                Lan.EnableBackgroundWake(BackgroundManager.SocketTaskId);
        }

        public void OnSuspending() => Lan.TransferToBroker();

        public Task OnResumingAsync() => Lan.ReclaimAndRestartAsync();

        public Task HandleSocketActivityAsync(Windows.Networking.Sockets.SocketActivityTriggerDetails d)
            => Lan.HandleSocketActivityAsync(d);

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

            var initial = SettingsStore.IsTrusted(info.Id) ? PairState.Paired : PairState.NotPaired;
            var handler = new PairingHandler(info.Id, initial,
                np => link.SendPacket(np),
                () => Links.TryGetValue(info.Id, out var t) ? t.Item1.Certificate : null);
            handler.StateChanged += s => OnPairStateChanged(info.Id, s);
            handler.PairingFailed += reason => { Log?.Invoke($"pairing failed ({info.Id}): {reason}"); PairingChanged?.Invoke(info.Id); };
            Pairing[info.Id] = handler;

            link.ConnectionLost += OnLinkLost(info.Id);
            link.PacketReceived += OnPacket;

            // Already-trusted device reconnecting -> load plugins immediately.
            if (initial == PairState.Paired) LoadPlugins(info.Id);

            LinksChanged?.Invoke();
            PairingChanged?.Invoke(info.Id);
        }

        private void OnPairStateChanged(string deviceId, PairState state)
        {
            Log?.Invoke($"pair state {deviceId}: {state}");
            if (state == PairState.Paired)
            {
                // TOFU: persist peer cert + trust flag (§V3, §V18 inverse)
                if (Links.TryGetValue(deviceId, out var t) && t.Item1.Certificate != null)
                    SslHelper.StorePeerCertificate(deviceId, t.Item1.Certificate);
                SettingsStore.SetTrusted(deviceId, true);
                LoadPlugins(deviceId);
            }
            else if (state == PairState.NotPaired)
            {
                UnloadPlugins(deviceId);
                SettingsStore.WipeDevice(deviceId); // §V18
            }
            PairingChanged?.Invoke(deviceId);
        }

        private void LoadPlugins(string deviceId)
        {
            if (!Links.TryGetValue(deviceId, out var t)) return;
            UnloadPlugins(deviceId);
            var info = t.Item1;
            var link = t.Item2;
            var list = new System.Collections.Generic.List<ZorinConnect.Plugins.IPlugin>();
            foreach (var plugin in ZorinConnect.Plugins.PluginRegistry.CreateAll())
            {
                var ctx = new ZorinConnect.Plugins.PluginContext(info, np => link.SendPacket(np), m => Log?.Invoke(m));
                plugin.OnCreate(ctx);
                if (plugin is ZorinConnect.Plugins.FindMyPhonePlugin fmp)
                {
                    fmp.RingStarted += () => RingStarted?.Invoke();
                    fmp.RingStopped += () => RingStopped?.Invoke();
                }
                list.Add(plugin);
            }
            DevicePlugins[deviceId] = list;
            Log?.Invoke($"loaded {list.Count} plugin(s) for {info.Name}");
        }

        private void UnloadPlugins(string deviceId)
        {
            if (DevicePlugins.TryRemove(deviceId, out var list))
                foreach (var p in list) { try { p.OnDestroy(); } catch { } }
        }

        private Action<LanLink> OnLinkLost(string deviceId)
        {
            return link =>
            {
                if (Links.TryGetValue(deviceId, out var cur) && ReferenceEquals(cur.Item2, link))
                {
                    Links.TryRemove(deviceId, out _);
                    UnloadPlugins(deviceId);
                    LinksChanged?.Invoke();
                    Log?.Invoke($"link lost: {deviceId}");
                }
            };
        }

        private void OnPacket(LanLink link, NetworkPacket np)
        {
            Log?.Invoke($"recv {np.Type} from {link.DeviceId}");
            if (np.Type == NetworkPacket.TypePair)
            {
                if (Pairing.TryGetValue(link.DeviceId, out var handler))
                    handler.PacketReceived(np);
                return;
            }
            // Dispatch to plugins (only loaded when paired)
            if (DevicePlugins.TryGetValue(link.DeviceId, out var plugins))
                foreach (var p in plugins)
                {
                    try { if (p.OnPacketReceived(np)) return; }
                    catch (System.Exception e) { Log?.Invoke($"plugin {p.Key} error: {e.Message}"); }
                }
        }

        // ---- pairing API for the UI ----
        public void RequestPair(string deviceId) { if (Pairing.TryGetValue(deviceId, out var h)) h.RequestPairing(); }
        public void AcceptPair(string deviceId) { if (Pairing.TryGetValue(deviceId, out var h)) h.AcceptPairing(); }
        public void RejectPair(string deviceId) { if (Pairing.TryGetValue(deviceId, out var h)) h.RejectPairing(); }
        public void Unpair(string deviceId) { if (Pairing.TryGetValue(deviceId, out var h)) h.Unpair(); }

        public T GetPlugin<T>(string deviceId) where T : class, ZorinConnect.Plugins.IPlugin
        {
            if (DevicePlugins.TryGetValue(deviceId, out var list))
                foreach (var p in list) if (p is T match) return match;
            return null;
        }
    }
}
