using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using ZorinConnect.Core;
using ZorinConnect.Helpers;

namespace ZorinConnect.Backends.Lan
{
    /// <summary>
    /// LAN backend (SPEC §I.udp/.tcp/.tls, §V2-V6). Mirrors Android LanLinkProvider:
    ///  - UDP 1716 listener + identity broadcast (rate ≥200ms)
    ///  - TCP listener on first free 1716..1764 (advertised as tcpPort)
    ///  - UDP identity received -> WE dial TCP -> send plaintext identity -> TLS as SERVER
    ///  - TCP accepted -> read plaintext identity line -> TLS as CLIENT
    ///  - v8: both re-send identity over TLS; encrypted identity authoritative (§V4)
    /// </summary>
    public sealed class LanLinkProvider
    {
        private const int UdpPort = 1716;
        private const int MinPort = 1716;
        private const int MaxPort = 1764;
        private const int MillisDelayBetweenConnectionsToSameDevice = 1000; // §V6
        private const int MillisDelayBetweenBroadcasts = 200;               // §V6
        private const int MaxRateLimitEntries = 255;
        private const int HandshakeTimeoutMs = 10000;                       // §I.tls 10s

        private DatagramSocket _udpSocket;
        private StreamSocketListener _tcpListener;
        private int _tcpPort;
        private long _lastBroadcastMs;
        private readonly ConcurrentDictionary<string, Windows.Storage.Streams.IOutputStream> _broadcastStreams
            = new ConcurrentDictionary<string, Windows.Storage.Streams.IOutputStream>();
        private readonly ConcurrentDictionary<string, DatagramSocket> _sendSockets
            = new ConcurrentDictionary<string, DatagramSocket>();

        // Diagnostic toggles retained (SPEC §V22). The earlier "UDP send fatal" was a MISDIAGNOSIS:
        // real cause was JToken.Value<T>() fast-failing on the OWN broadcast echo (§B1). Broadcast
        // works now — GSConnect discovers the phone.
        public static bool IsolateNoUdpListener = false;
        public static bool IsolateNoBroadcast = false;

        private readonly ConcurrentDictionary<string, long> _lastConnectionByDevice = new ConcurrentDictionary<string, long>();
        private readonly ConcurrentDictionary<string, long> _lastConnectionByIp = new ConcurrentDictionary<string, long>();

        /// <summary>Capabilities advertised in identity (SPEC §V10) — set by the plugin registry before Start.</summary>
        public HashSet<string> IncomingCapabilities { get; set; } = new HashSet<string>();
        public HashSet<string> OutgoingCapabilities { get; set; } = new HashSet<string>();

        /// <summary>deviceId -> pinned peer cert provider (null = unpaired/trust-all). Wired by Device registry.</summary>
        public Func<string, Org.BouncyCastle.X509.X509Certificate> PinnedCertificateProvider { get; set; } = SslHelper.LoadPeerCertificate;

        /// <summary>Trusted-device protocol downgrade guard (§I downgrade). Returns stored version or 0.</summary>
        public Func<string, int> StoredProtocolVersionProvider { get; set; } = id =>
            SettingsStore.GetInt(SettingsStore.ForDevice(id), "protocolVersion");

        public event Action<DeviceInfo, LanLink> LinkEstablished;
        public event Action<string> Log; // diagnostics surface (kept until release per user practice)

        public int TcpPort => _tcpPort;

        public async Task StartAsync()
        {
            StartupTrace.Mark("lan-tcp-bind");
            await StartTcpListenerAsync();
            if (!IsolateNoUdpListener)
            {
                StartupTrace.Mark("lan-udp-bind");
                await StartUdpListenerAsync();
            }
            if (!IsolateNoBroadcast)
            {
                StartupTrace.Mark("lan-broadcast");
                await BroadcastIdentityAsync();
                StartupTrace.Mark("lan-broadcast-done");
            }
        }

        public async Task OnNetworkChangeAsync()
        {
            await BroadcastIdentityAsync();
        }

        // ---------- TCP listener ----------

        private async Task StartTcpListenerAsync()
        {
            for (int port = MinPort; port <= MaxPort; port++)
            {
                var listener = new StreamSocketListener();
                listener.Control.KeepAlive = true;
                listener.ConnectionReceived += OnTcpConnectionReceived;
                try
                {
                    await listener.BindServiceNameAsync(port.ToString());
                    _tcpListener = listener;
                    _tcpPort = port;
                    Log?.Invoke($"tcp listener on {port}");
                    return;
                }
                catch (Exception)
                {
                    listener.Dispose();
                }
            }
            throw new IOException("no free tcp port in 1716..1764");
        }

        // ---------- UDP ----------

        private async Task StartUdpListenerAsync()
        {
            var socket = new DatagramSocket();
            socket.MessageReceived += OnUdpMessageReceived;
            try
            {
                await socket.BindServiceNameAsync(UdpPort.ToString());
                _udpSocket = socket;
                Log?.Invoke("udp listener on 1716");
            }
            catch (Exception e)
            {
                // §I.udp: bind failure non-fatal (send still works)
                Log?.Invoke($"udp bind failed (non-fatal): {e.Message}");
                socket.Dispose();
                _udpSocket = null;
            }
        }

        public async Task BroadcastIdentityAsync()
        {
            var now = NowMs();
            if (now - Interlocked.Read(ref _lastBroadcastMs) < MillisDelayBetweenBroadcasts) return; // §V6
            Interlocked.Exchange(ref _lastBroadcastMs, now);

            if (_tcpPort == 0) return; // §I.udp: not sent if TCP server not bound yet
            if (_udpSocket == null) return; // send through the persistent bound socket only

            var np = IdentityPacket();
            np.Set("tcpPort", _tcpPort);
            var payload = Encoding.UTF8.GetBytes(np.Serialize()).AsBuffer();

            // Subnet-directed broadcast per adapter (255.255.255.255 fast-fails on W10M).
            // Output streams are CACHED and never disposed — disposing the stream returned by
            // GetOutputStreamAsync tears down shared DatagramSocket state and the async send
            // completion then fast-fails the process (uncatchable) on W10M.
            var targets = NetworkHelper.BroadcastAddresses();
            foreach (var addr in targets)
            {
                try
                {
                    var stream = await GetBroadcastStreamAsync(addr);
                    if (stream == null) continue;
                    await stream.WriteAsync(payload);
                    await stream.FlushAsync();
                    Log?.Invoke($"identity broadcast -> {addr}");
                }
                catch (Exception e)
                {
                    StartupTrace.MarkError("broadcast", e);
                    Log?.Invoke($"broadcast to {addr} failed: {e.Message}");
                }
            }
        }

        private async Task<Windows.Storage.Streams.IOutputStream> GetBroadcastStreamAsync(string addr)
        {
            if (_broadcastStreams.TryGetValue(addr, out var cached)) return cached;
            // Dedicated send socket via ConnectAsync (the reliable W10M unicast/broadcast idiom);
            // GetOutputStreamAsync on the bound listener socket fast-fails the process on send.
            var sendSocket = new DatagramSocket();
            await sendSocket.ConnectAsync(new HostName(addr), UdpPort.ToString());
            _sendSockets[addr] = sendSocket;
            _broadcastStreams[addr] = sendSocket.OutputStream;
            return sendSocket.OutputStream;
        }

        private NetworkPacket IdentityPacket()
        {
            var np = new NetworkPacket(NetworkPacket.TypeIdentity);
            DeviceHelper.LocalDeviceInfo(IncomingCapabilities, OutgoingCapabilities).FillIdentityPacket(np);
            return np;
        }

        private async void OnUdpMessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            StartupTrace.Mark("udp-rx");
            try
            {
                string line;
                using (var reader = args.GetDataReader())
                {
                    StartupTrace.Mark("udp-reader");
                    if (reader.UnconsumedBufferLength > NetworkPacket.MaxIdentityPacketSize) return; // §V5
                    var buf = new byte[reader.UnconsumedBufferLength];
                    reader.ReadBytes(buf);
                    line = Encoding.UTF8.GetString(buf, 0, buf.Length);
                }
                StartupTrace.Mark($"udp-line:{line.Length}");

                var np = NetworkPacket.Deserialize(line);
                StartupTrace.Mark($"udp-parsed:{np.Type}");
                if (np.Type != NetworkPacket.TypeIdentity) return;
                var info = DeviceInfo.FromIdentityPacket(np);
                StartupTrace.Mark($"udp-info:{(info == null ? "null" : info.Id)}");
                if (info == null) return;                                   // §V5
                if (info.Id == DeviceHelper.DeviceId) return;               // own broadcast echo

                var tcpPort = np.GetInt("tcpPort");
                StartupTrace.Mark($"udp-tcpport:{tcpPort}");
                if (tcpPort < MinPort || tcpPort > MaxPort) return;         // §I.tcp validated range

                var remoteHost = args.RemoteAddress;
                StartupTrace.Mark($"udp-remote:{remoteHost?.CanonicalName}");
                if (!RateLimitOk(info.Id, remoteHost.CanonicalName)) return; // §V6
                StartupTrace.Mark("udp-ratelimit-ok");

                Log?.Invoke($"udp identity from {info.Name} ({remoteHost.CanonicalName}:{tcpPort})");
                StartupTrace.Mark($"udp-connect-out:{remoteHost.CanonicalName}:{tcpPort}");
                await ConnectAsync(remoteHost, tcpPort, info);
            }
            catch (Exception e)
            {
                StartupTrace.MarkError("udp-rx", e);
                Log?.Invoke($"udp receive error: {e.Message}");
            }
        }

        private bool RateLimitOk(string deviceId, string ip)
        {
            var now = NowMs();
            PruneIfNeeded(_lastConnectionByDevice);
            PruneIfNeeded(_lastConnectionByIp);
            if (_lastConnectionByDevice.TryGetValue(deviceId, out var t1) && now - t1 < MillisDelayBetweenConnectionsToSameDevice)
                return false;
            if (_lastConnectionByIp.TryGetValue(ip, out var t2) && now - t2 < MillisDelayBetweenConnectionsToSameDevice)
                return false;
            _lastConnectionByDevice[deviceId] = now;
            _lastConnectionByIp[ip] = now;
            return true;
        }

        private static void PruneIfNeeded(ConcurrentDictionary<string, long> map)
        {
            if (map.Count <= MaxRateLimitEntries) return;
            foreach (var key in map.Keys.Take(map.Count - MaxRateLimitEntries))
                map.TryRemove(key, out _);
        }

        // ---------- outbound: we dial TCP -> TLS SERVER (§V2) ----------

        private async Task ConnectAsync(HostName host, int tcpPort, DeviceInfo bootstrapInfo)
        {
            var socket = new StreamSocket();
            socket.Control.KeepAlive = true;
            try
            {
                var cts = new CancellationTokenSource(HandshakeTimeoutMs);
                await socket.ConnectAsync(host, tcpPort.ToString()).AsTask(cts.Token);
                StartupTrace.Mark("tcp-connected");

                var input = socket.InputStream.AsStreamForRead(0);
                var output = socket.OutputStream.AsStreamForWrite(0);

                // plaintext identity bootstrap: connector sends own identity (§I.tcp)
                var identityBytes = Encoding.UTF8.GetBytes(IdentityPacket().Serialize());
                await output.WriteAsync(identityBytes, 0, identityBytes.Length);
                await output.FlushAsync();
                StartupTrace.Mark("tcp-identity-sent");

                await Task.Run(() => FinishHandshake(socket, input, output, bootstrapInfo, tlsServerRole: true));
            }
            catch (Exception e)
            {
                StartupTrace.MarkError("tcp-connect", e);
                Log?.Invoke($"outbound connect to {host.CanonicalName}:{tcpPort} failed: {e.Message}");
                try { socket.Dispose(); } catch { }
            }
        }

        // ---------- inbound: peer dialed us -> TLS CLIENT (§V2) ----------

        private async void OnTcpConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            var socket = args.Socket;
            try
            {
                var input = socket.InputStream.AsStreamForRead(0);
                var output = socket.OutputStream.AsStreamForWrite(0);

                // read one plaintext identity line (cap 512KiB, §V5)
                var line = await Task.Run(() => ReadSingleLine(input, NetworkPacket.MaxIdentityPacketSize));
                if (line == null) { socket.Dispose(); return; }

                var np = NetworkPacket.Deserialize(line);
                var info = DeviceInfo.FromIdentityPacket(np);
                if (info == null || info.Id == DeviceHelper.DeviceId) { socket.Dispose(); return; }
                if (!RateLimitOk(info.Id, socket.Information.RemoteAddress.CanonicalName)) { socket.Dispose(); return; } // §V6

                Log?.Invoke($"tcp identity from {info.Name} ({socket.Information.RemoteAddress.CanonicalName})");
                await Task.Run(() => FinishHandshake(socket, input, output, info, tlsServerRole: false));
            }
            catch (Exception e)
            {
                Log?.Invoke($"inbound connection error: {e.Message}");
                try { socket.Dispose(); } catch { }
            }
        }

        // ---------- shared: TLS + v8 encrypted identity exchange ----------

        private void FinishHandshake(StreamSocket socket, Stream input, Stream output, DeviceInfo bootstrapInfo, bool tlsServerRole)
        {
            StartupTrace.Mark($"handshake:{(tlsServerRole ? "srv" : "cli")}:{bootstrapInfo.Id}");
            // Downgrade guard (§I): trusted device advertising lower protocol than stored -> refuse
            var stored = StoredProtocolVersionProvider?.Invoke(bootstrapInfo.Id) ?? 0;
            if (SettingsStore.IsTrusted(bootstrapInfo.Id) && stored > 0 && bootstrapInfo.ProtocolVersion < stored)
            {
                Log?.Invoke($"downgrade refused for {bootstrapInfo.Id}: {bootstrapInfo.ProtocolVersion} < {stored}");
                socket.Dispose();
                return;
            }

            var pinned = SettingsStore.IsTrusted(bootstrapInfo.Id) ? PinnedCertificateProvider?.Invoke(bootstrapInfo.Id) : null;

            TlsConnectionResult tls;
            try
            {
                tls = tlsServerRole ? TlsHelper.AsServer(input, output, pinned)
                                    : TlsHelper.AsClient(input, output, pinned);
            }
            catch (Exception e)
            {
                Log?.Invoke($"tls handshake failed ({(tlsServerRole ? "server" : "client")}) with {bootstrapInfo.Id}: {e.Message}");
                socket.Dispose();
                return;
            }

            var finalInfo = bootstrapInfo;
            if (bootstrapInfo.ProtocolVersion >= 8)
            {
                // §V4: both send identity encrypted; encrypted identity authoritative
                try
                {
                    var mine = Encoding.UTF8.GetBytes(IdentityPacket().Serialize());
                    tls.Stream.Write(mine, 0, mine.Length);
                    tls.Stream.Flush();

                    var line = ReadSingleLine(tls.Stream, NetworkPacket.MaxIdentityPacketSize);
                    if (line == null) throw new IOException("no encrypted identity");
                    var np = NetworkPacket.Deserialize(line);
                    var secure = DeviceInfo.FromIdentityPacket(np);
                    if (secure == null) throw new IOException("invalid encrypted identity");
                    if (secure.Id != bootstrapInfo.Id) throw new IOException("identity switched post-TLS");
                    finalInfo = secure;
                }
                catch (Exception e)
                {
                    Log?.Invoke($"v8 identity exchange failed with {bootstrapInfo.Id}: {e.Message}");
                    socket.Dispose();
                    return;
                }
            }

            finalInfo.Certificate = tls.PeerCertificate;

            var remote = socket.Information.RemoteAddress?.CanonicalName ?? "?";
            var link = new LanLink(finalInfo.Id, remote, socket, tls.Stream, tls.PeerCertificate);
            Log?.Invoke($"link established with {finalInfo.Name} ({finalInfo.Id}) proto v{finalInfo.ProtocolVersion}");
            LinkEstablished?.Invoke(finalInfo, link);
            link.StartReadLoop();
        }

        /// <summary>Byte-wise single-line read so nothing beyond '\n' is consumed (stream handed to TLS/link next).</summary>
        internal static string ReadSingleLine(Stream stream, int maxBytes)
        {
            using (var ms = new MemoryStream())
            {
                while (ms.Length < maxBytes)
                {
                    int b = stream.ReadByte();
                    if (b < 0) return ms.Length > 0 ? Utf8(ms) : null;
                    if (b == '\n') return Utf8(ms);
                    ms.WriteByte((byte)b);
                }
                return null; // over cap (§V5)
            }
        }

        private static string Utf8(MemoryStream ms) => Encoding.UTF8.GetString(ms.ToArray(), 0, (int)ms.Length);

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public void Stop()
        {
            try { _udpSocket?.Dispose(); } catch { }
            try { _tcpListener?.Dispose(); } catch { }
            _udpSocket = null;
            _tcpListener = null;
        }
    }
}
