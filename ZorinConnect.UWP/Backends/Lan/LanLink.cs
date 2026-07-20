using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZorinConnect.Core;
using BcX509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace ZorinConnect.Backends.Lan
{
    /// <summary>
    /// Established encrypted link (SPEC §V12, §V15). Line-based read loop; serialized writes.
    /// </summary>
    public sealed class LanLink : IDisposable
    {
        private readonly Stream _stream;          // BC TLS application stream
        private readonly StreamReader _reader;
        private readonly object _writeLock = new object();
        private readonly Windows.Networking.Sockets.StreamSocket _socket; // keep-alive: owns the transport
        private volatile bool _closed;

        public string DeviceId { get; }
        public string RemoteAddress { get; }
        public BcX509Certificate PeerCertificate { get; }

        public event Action<LanLink, NetworkPacket> PacketReceived;
        public event Action<LanLink> ConnectionLost;

        public LanLink(string deviceId, string remoteAddress, Windows.Networking.Sockets.StreamSocket socket,
                       Stream tlsStream, BcX509Certificate peerCert)
        {
            DeviceId = deviceId;
            RemoteAddress = remoteAddress;
            _socket = socket;
            _stream = tlsStream;
            PeerCertificate = peerCert;
            _reader = new StreamReader(_stream, new UTF8Encoding(false));
        }

        public void StartReadLoop()
        {
            Task.Factory.StartNew(ReadLoop, TaskCreationOptions.LongRunning);
        }

        private void ReadLoop()
        {
            try
            {
                while (!_closed)
                {
                    var line = _reader.ReadLine();
                    if (line == null) break; // EOF (SPEC §V12)
                    if (line.Length == 0) continue; // empty lines skipped
                    NetworkPacket np;
                    try { np = NetworkPacket.Deserialize(line); }
                    catch (Exception) { continue; } // malformed packet: skip, keep link
                    PacketReceived?.Invoke(this, np);
                }
            }
            catch (Exception)
            {
                // socket death falls through to disconnect
            }
            Disconnect();
        }

        /// <summary>Blocking send. False on failure — caller disconnects link (SPEC §V15).</summary>
        public bool SendPacket(NetworkPacket np)
        {
            if (_closed) return false;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(np.Serialize());
                lock (_writeLock)
                {
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();
                }
                return true;
            }
            catch (Exception)
            {
                Disconnect();
                return false;
            }
        }

        public void Disconnect()
        {
            if (_closed) return;
            _closed = true;
            try { _stream.Dispose(); } catch { }
            try { _socket?.Dispose(); } catch { }
            ConnectionLost?.Invoke(this);
        }

        public void Dispose() => Disconnect();
    }
}
