using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Windows.Networking.Connectivity;
using ZorinConnect.Core;

namespace ZorinConnect.Plugins
{
    /// <summary>
    /// ConnectivityReport (SPEC T25). Phone reports cellular signal + network type to the desktop
    /// (send-only, like Android). Packet kdeconnect.connectivity_report:
    ///   signalStrengths: { "&lt;subId&gt;": { networkType: string, signalStrength: int 0-4 } }
    /// </summary>
    public sealed class ConnectivityReportPlugin : IPlugin
    {
        private const string PacketType = "kdeconnect.connectivity_report";

        private PluginContext _ctx;

        public string Key => "ConnectivityReportPlugin";
        public string DisplayName => "Connectivity Report";
        public bool EnabledByDefault => true;
        public IEnumerable<string> SupportedPacketTypes => new string[0]; // send-only
        public IEnumerable<string> OutgoingPacketTypes => new[] { PacketType };

        public void OnCreate(PluginContext context)
        {
            _ctx = context;
            NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
            SendReport();
        }

        public void OnDestroy()
        {
            NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        }

        private void OnNetworkStatusChanged(object sender) => SendReport();

        public bool OnPacketReceived(NetworkPacket np) => false;

        private void SendReport()
        {
            try
            {
                var signalStrengths = new JObject();
                int subId = 0;

                foreach (var profile in NetworkInformation.GetConnectionProfiles())
                {
                    if (profile?.IsWwanConnectionProfile != true) continue;
                    var wwan = profile.WwanConnectionProfileDetails;
                    if (wwan == null) continue;

                    string networkType = MapDataClass(wwan.GetCurrentDataClass());
                    int signal = SignalLevel(profile);
                    signalStrengths[subId.ToString()] = new JObject
                    {
                        ["networkType"] = networkType,
                        ["signalStrength"] = signal,
                    };
                    subId++;
                }

                // No cellular adapter (Wi-Fi-only / no SIM): still report one Unknown entry so the
                // desktop shows the field, matching Android's always-present report.
                if (signalStrengths.Count == 0)
                    signalStrengths["0"] = new JObject { ["networkType"] = "Unknown", ["signalStrength"] = 0 };

                var np = new NetworkPacket(PacketType).Set("signalStrengths", signalStrengths);
                _ctx?.SendPacket(np);
                _ctx?.Log?.Invoke($"connectivity -> {signalStrengths.ToString(Newtonsoft.Json.Formatting.None)}");
            }
            catch (System.Exception e)
            {
                _ctx?.Log?.Invoke($"connectivity report failed: {e.Message}");
            }
        }

        /// <summary>W10M signal bars 0-5 -> Android signal level 0-4.</summary>
        private static int SignalLevel(ConnectionProfile profile)
        {
            var bars = profile.GetSignalBars();
            if (bars == null) return 0;
            int b = bars.Value;
            return b > 4 ? 4 : b;
        }

        private static string MapDataClass(WwanDataClass dc)
        {
            switch (dc)
            {
                case WwanDataClass.Gprs: return "GPRS";
                case WwanDataClass.Edge: return "EDGE";
                case WwanDataClass.Umts: return "UMTS";
                case WwanDataClass.Hsdpa:
                case WwanDataClass.Hsupa: return "HSPA";
                case WwanDataClass.LteAdvanced: return "LTE";
                case WwanDataClass.Cdma1xRtt: return "CDMA";
                case WwanDataClass.Cdma1xEvdo:
                case WwanDataClass.Cdma1xEvdoRevA:
                case WwanDataClass.Cdma1xEvdoRevB:
                case WwanDataClass.Cdma1xEvdv:
                case WwanDataClass.Cdma3xRtt:
                case WwanDataClass.CdmaUmb: return "CDMA2000";
                default: return "Unknown";
            }
        }
    }
}
