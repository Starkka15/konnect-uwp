using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using ZorinConnect.Core;

namespace ZorinConnect.Backends.Lan
{
    /// <summary>
    /// Payload transfer (SPEC §I.payload, §V2/§V16). A packet with a payload rides a SECOND TLS
    /// socket on a fresh port 1739..1764:
    ///  - RECEIVE: peer put payloadTransferInfo.port in the packet; we dial that port at the peer's
    ///    address and TLS-CLIENT it (the sender is the TLS server on the payload link).
    ///  - SEND: we open a ServerSocket on a free port, advertise it, accept + TLS-SERVER, write bytes.
    /// </summary>
    public static class PayloadChannel
    {
        private const int PayloadMinPort = 1739;
        private const int PayloadMaxPort = 1764;
        private const int AcceptTimeoutMs = 10000;

        /// <summary>Receiver: connect to the peer's payload port and return the decrypted read stream.</summary>
        public static async Task<Stream> OpenReceiveStreamAsync(string remoteAddress, int port,
                                                                Org.BouncyCastle.X509.X509Certificate pinnedPeer)
        {
            StartupTrace.Mark($"payload-dial:{remoteAddress}:{port} pinned={(pinnedPeer != null)}");
            var socket = new StreamSocket();
            var cts = new System.Threading.CancellationTokenSource(AcceptTimeoutMs);
            await socket.ConnectAsync(new HostName(remoteAddress), port.ToString()).AsTask(cts.Token);
            StartupTrace.Mark("payload-connected");
            var input = socket.InputStream.AsStreamForRead(0);
            var output = socket.OutputStream.AsStreamForWrite(0);
            // We dialed -> we are TLS server? NO: payload role is by SENDER. The sender is TLS server;
            // the receiver (us, dialing) is TLS CLIENT. (§V2 note: payload roles differ from main link.)
            TlsConnectionResult tls;
            try
            {
                tls = TlsHelper.AsClient(input, output, pinnedPeer);
            }
            catch (Exception ex)
            {
                var detail = ex.ToString().Replace("\r", " ").Replace("\n", " | ");
                StartupTrace.Mark($"payload-tls-fail:{(detail.Length > 1000 ? detail.Substring(0, 1000) : detail)}");
                throw;
            }
            StartupTrace.Mark("payload-tls-ok");
            return new PayloadSocketStream(socket, tls);
        }

        /// <summary>
        /// Sender: open a ServerSocket on a free payload port, return (port, task). The task accepts
        /// one connection, TLS-SERVERs it, copies <paramref name="source"/> (payloadSize bytes), closes.
        /// </summary>
        public static async Task<int> OpenSendServerAsync(Stream source, long payloadSize,
                                                          Org.BouncyCastle.X509.X509Certificate pinnedPeer,
                                                          Action<long> onProgress)
        {
            StreamSocketListener listener = null;
            int boundPort = 0;
            for (int port = PayloadMinPort; port <= PayloadMaxPort; port++)
            {
                listener = new StreamSocketListener();
                try { await listener.BindServiceNameAsync(port.ToString()); boundPort = port; break; }
                catch { listener.Dispose(); listener = null; }
            }
            if (listener == null) throw new IOException("no free payload port 1739..1764");

            var tcs = new TaskCompletionSource<StreamSocket>();
            listener.ConnectionReceived += (s, e) => tcs.TrySetResult(e.Socket);

            // Fire the accept+send in the background; port returns immediately so the packet can be sent.
            var _ = Task.Run(async () =>
            {
                try
                {
                    var accepted = await WithTimeout(tcs.Task, AcceptTimeoutMs);
                    listener.Dispose();
                    if (accepted == null) { onProgress?.Invoke(-1); return; }

                    var input = accepted.InputStream.AsStreamForRead(0);
                    var output = accepted.OutputStream.AsStreamForWrite(0);
                    var tls = TlsHelper.AsServer(input, output, pinnedPeer); // sender = TLS server
                    var buffer = new byte[4096];
                    long sent = 0;
                    long lastReport = 0;
                    int read;
                    while (sent < payloadSize && (read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, payloadSize - sent))) > 0)
                    {
                        tls.Stream.Write(buffer, 0, read);
                        sent += read;
                        if (sent - lastReport >= 65536) { onProgress?.Invoke(sent); lastReport = sent; }
                    }
                    tls.Stream.Flush();
                    try { tls.Protocol.Close(); } catch { }
                    accepted.Dispose();
                    onProgress?.Invoke(sent);
                }
                catch (Exception) { onProgress?.Invoke(-1); }
                finally { try { source.Dispose(); } catch { } }
            });

            return boundPort;
        }

        private static async Task<T> WithTimeout<T>(Task<T> task, int ms) where T : class
        {
            var done = await Task.WhenAny(task, Task.Delay(ms));
            return done == task ? task.Result : null;
        }
    }

    /// <summary>Read stream over a payload TLS socket that disposes both on close.</summary>
    internal sealed class PayloadSocketStream : Stream
    {
        private readonly StreamSocket _socket;
        private readonly TlsConnectionResult _tls;
        public PayloadSocketStream(StreamSocket socket, TlsConnectionResult tls) { _socket = socket; _tls = tls; }

        public override int Read(byte[] b, int o, int c) => _tls.Stream.Read(b, o, c);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _tls.Protocol.Close(); } catch { }
                try { _socket.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
