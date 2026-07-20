using System.Collections.Generic;
using Windows.System.Power;
using ZorinConnect.Core;

namespace ZorinConnect.Plugins
{
    /// <summary>
    /// Battery (SPEC T16). Sends kdeconnect.battery {currentCharge, isCharging, thresholdEvent}.
    /// thresholdEvent: 0 = NONE, 1 = BATTERY_LOW (fired once when charge drops to LOW & not charging).
    /// Also receives the desktop's battery (kdeconnect.battery) for display.
    /// Delta-suppressed: only sends when a field changes.
    /// </summary>
    public sealed class BatteryPlugin : IPlugin
    {
        private const string PacketType = "kdeconnect.battery";
        private const int LowThreshold = 15; // Android BatteryManager BATTERY_LOW ~= 15%

        private PluginContext _ctx;
        private int _lastCharge = -1;
        private bool _lastCharging;
        private int _lastThreshold = -1;
        private bool _wasLow;

        // Remote (desktop) battery snapshot, surfaced to the UI.
        public int RemoteCharge { get; private set; } = -1;
        public bool RemoteCharging { get; private set; }
        public bool HasRemote { get; private set; }
        public event System.Action RemoteChanged;

        public string Key => "BatteryPlugin";
        public string DisplayName => "Battery";
        public bool EnabledByDefault => true;
        public IEnumerable<string> SupportedPacketTypes => new[] { PacketType };
        public IEnumerable<string> OutgoingPacketTypes => new[] { PacketType };

        public void OnCreate(PluginContext context)
        {
            _ctx = context;
            PowerManager.RemainingChargePercentChanged += OnPowerChanged;
            PowerManager.BatteryStatusChanged += OnPowerChanged;
            PowerManager.PowerSupplyStatusChanged += OnPowerChanged;
            SendState(force: true); // initial state on connect
        }

        public void OnDestroy()
        {
            PowerManager.RemainingChargePercentChanged -= OnPowerChanged;
            PowerManager.BatteryStatusChanged -= OnPowerChanged;
            PowerManager.PowerSupplyStatusChanged -= OnPowerChanged;
        }

        private void OnPowerChanged(object sender, object e) => SendState(force: false);

        private void SendState(bool force)
        {
            int charge = PowerManager.RemainingChargePercent;
            bool charging = PowerManager.BatteryStatus == BatteryStatus.Charging
                            || PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent
                            && PowerManager.BatteryStatus != BatteryStatus.Discharging;

            // thresholdEvent: fire BATTERY_LOW once, only while discharging (Android parity).
            int threshold = 0;
            if (!charging && charge >= 0 && charge <= LowThreshold)
            {
                if (!_wasLow) { threshold = 1; _wasLow = true; }
            }
            else if (charge > LowThreshold || charging)
            {
                _wasLow = false;
            }

            if (!force && charge == _lastCharge && charging == _lastCharging && threshold == 0)
                return; // delta-suppress

            _lastCharge = charge;
            _lastCharging = charging;
            _lastThreshold = threshold;

            var np = new NetworkPacket(PacketType)
                .Set("currentCharge", charge)
                .Set("isCharging", charging)
                .Set("thresholdEvent", threshold);
            _ctx?.SendPacket(np);
            _ctx?.Log?.Invoke($"battery -> {charge}% charging={charging} thr={threshold}");
        }

        public bool OnPacketReceived(NetworkPacket np)
        {
            if (np.Type != PacketType) return false;
            RemoteCharge = np.GetInt("currentCharge", -1);
            RemoteCharging = np.GetBool("isCharging");
            HasRemote = true;
            _ctx?.Log?.Invoke($"remote battery {RemoteCharge}% charging={RemoteCharging}");
            RemoteChanged?.Invoke();
            return true;
        }
    }
}
