using System.Collections.Generic;
using ZorinConnect.Core;

namespace ZorinConnect.Plugins
{
    /// <summary>
    /// Plugin contract (SPEC §T10, mirrors Android Plugin.kt). A plugin instance is created per
    /// paired+reachable device. Capabilities it declares gate whether it loads for a given peer
    /// and what the identity packet advertises (§V10).
    /// </summary>
    public interface IPlugin
    {
        /// <summary>Stable key = class name sans "Plugin" (e.g. "PingPlugin"). Used for settings + registry.</summary>
        string Key { get; }
        string DisplayName { get; }
        bool EnabledByDefault { get; }

        /// <summary>Packet types this plugin HANDLES (its incomingCapabilities contribution).</summary>
        IEnumerable<string> SupportedPacketTypes { get; }
        /// <summary>Packet types this plugin SENDS (its outgoingCapabilities contribution).</summary>
        IEnumerable<string> OutgoingPacketTypes { get; }

        void OnCreate(PluginContext context);
        void OnDestroy();

        /// <summary>Handle an inbound packet. Return true if consumed.</summary>
        bool OnPacketReceived(NetworkPacket np);
    }

    /// <summary>Per-device services handed to a plugin at OnCreate.</summary>
    public sealed class PluginContext
    {
        public DeviceInfo Device { get; }
        private readonly System.Func<NetworkPacket, bool> _send;
        private readonly ZorinConnect.Backends.Lan.LanLink _link;
        public System.Action<string> Log { get; }

        public PluginContext(DeviceInfo device, ZorinConnect.Backends.Lan.LanLink link,
                             System.Func<NetworkPacket, bool> send, System.Action<string> log)
        {
            Device = device;
            _link = link;
            _send = send;
            Log = log;
        }

        public bool SendPacket(NetworkPacket np) => _send(np);

        /// <summary>Open a decrypted read stream for an inbound packet's payload (null if none).</summary>
        public System.Threading.Tasks.Task<System.IO.Stream> OpenIncomingPayloadAsync(NetworkPacket np)
        {
            if (!np.HasPayloadTransferInfo || np.PayloadPort == 0)
                return System.Threading.Tasks.Task.FromResult<System.IO.Stream>(null);
            var pinned = ZorinConnect.Helpers.SslHelper.LoadPeerCertificate(Device.Id);
            return ZorinConnect.Backends.Lan.PayloadChannel.OpenReceiveStreamAsync(
                _link.RemoteAddress, np.PayloadPort, pinned);
        }

        /// <summary>Send a packet carrying a payload: opens the payload server, advertises the port.</summary>
        public async System.Threading.Tasks.Task SendPacketWithPayloadAsync(
            NetworkPacket np, System.IO.Stream source, long size, System.Action<long> progress)
        {
            var pinned = ZorinConnect.Helpers.SslHelper.LoadPeerCertificate(Device.Id);
            int port = await ZorinConnect.Backends.Lan.PayloadChannel.OpenSendServerAsync(source, size, pinned, progress);
            np.PayloadSize = size;
            np.PayloadTransferInfo = new Newtonsoft.Json.Linq.JObject { ["port"] = port };
            _send(np);
        }
    }
}
