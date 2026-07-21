using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Math;
using Windows.Networking.Sockets;
using Windows.Storage;
using ZorinConnect.Core;

namespace ZorinConnect.Backends.Sftp
{
    /// <summary>
    /// Minimal SSH2 server for one KDE Connect SFTP session. Transport (dh-group14-sha256 KEX,
    /// aes128-ctr + hmac-sha2-256, ssh-rsa host key) + password auth + one session channel running
    /// the SFTP v3 subsystem. Runs synchronously on a background thread per accepted connection.
    /// </summary>
    internal sealed class SshServer
    {
        // Message numbers
        private const byte SSH_MSG_DISCONNECT = 1, SSH_MSG_IGNORE = 2, SSH_MSG_UNIMPLEMENTED = 3,
            SSH_MSG_SERVICE_REQUEST = 5, SSH_MSG_SERVICE_ACCEPT = 6, SSH_MSG_KEXINIT = 20, SSH_MSG_NEWKEYS = 21,
            SSH_MSG_KEXDH_INIT = 30, SSH_MSG_KEXDH_REPLY = 31,
            SSH_MSG_USERAUTH_REQUEST = 50, SSH_MSG_USERAUTH_FAILURE = 51, SSH_MSG_USERAUTH_SUCCESS = 52,
            SSH_MSG_GLOBAL_REQUEST = 80, SSH_MSG_CHANNEL_OPEN = 90, SSH_MSG_CHANNEL_OPEN_CONFIRMATION = 91,
            SSH_MSG_CHANNEL_OPEN_FAILURE = 92, SSH_MSG_CHANNEL_WINDOW_ADJUST = 93, SSH_MSG_CHANNEL_DATA = 94,
            SSH_MSG_CHANNEL_EOF = 96, SSH_MSG_CHANNEL_CLOSE = 97, SSH_MSG_CHANNEL_REQUEST = 98,
            SSH_MSG_CHANNEL_SUCCESS = 99, SSH_MSG_CHANNEL_FAILURE = 100;

        private const string ServerVersion = "SSH-2.0-KonnectUWP_1.0";

        private readonly StreamSocket _socket;
        private readonly Stream _in, _out;
        private readonly SshCrypto _crypto;
        private readonly string _user;
        private readonly string _password;
        private readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, StorageFolder>> _roots;
        private readonly Action<string> _log;

        // Transport state
        private uint _seqIn, _seqOut;
        private bool _encrypted;
        private IBufferedCipher _encCipher, _decCipher;
        private HMac _macOut, _macIn;
        private byte[] _sessionId;

        // Channels — gvfs multiplexes command+data SFTP sessions as TWO channels over ONE connection
        // (SSH ControlMaster). Each channel is an independent SFTP session; single-channel handling
        // clobbered the first when the second opened -> "unhandled reply". Route everything by channel id.
        private sealed class Channel
        {
            public uint Peer;
            public uint PeerWindow;
            public uint LocalWindowLeft;
            public SftpSubsystem Sftp;
        }
        private readonly System.Collections.Generic.Dictionary<uint, Channel> _channels =
            new System.Collections.Generic.Dictionary<uint, Channel>();
        private uint _nextLocalChannel;
        private const uint LocalWindow = 2 * 1024 * 1024;

        // SendPacket touches shared transport state (AES-CTR keystream, seq #, output stream); with
        // multiple channels' async SFTP loops sending concurrently it MUST be serialized.
        private readonly object _sendLock = new object();

        public SshServer(StreamSocket socket, SshCrypto crypto, string user, string password,
                         System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, StorageFolder>> roots,
                         Action<string> log)
        {
            _socket = socket;
            _in = socket.InputStream.AsStreamForRead(0);
            _out = socket.OutputStream.AsStreamForWrite(0);
            _crypto = crypto;
            _user = user;
            _password = password;
            _roots = roots;
            _log = log;
        }

        public void Run()
        {
            try
            {
                VersionExchange(out var clientVersion);
                KeyExchange(clientVersion);
                Authenticate();
                ChannelLoop();
            }
            catch (Exception e) { StartupTrace.Mark($"sftp-ssh-end:{Trunc(e.Message)}"); }
            finally { try { _socket.Dispose(); } catch { } }
        }

        // ---------- version exchange ----------

        private void VersionExchange(out string clientVersion)
        {
            var vs = Encoding.ASCII.GetBytes(ServerVersion + "\r\n");
            _out.Write(vs, 0, vs.Length); _out.Flush();
            clientVersion = ReadLine();
            StartupTrace.Mark($"sftp-cli-ver:{Trunc(clientVersion)}");
        }

        private string ReadLine()
        {
            // Client may send extra lines before the SSH- id; skip until one starts with "SSH-".
            while (true)
            {
                var sb = new StringBuilder();
                int b;
                while ((b = _in.ReadByte()) >= 0 && b != '\n')
                    if (b != '\r') sb.Append((char)b);
                var line = sb.ToString();
                if (line.StartsWith("SSH-")) return line;
                if (b < 0) throw new IOException("eof during version");
            }
        }

        // ---------- key exchange ----------

        private void KeyExchange(string clientVersion)
        {
            // Send our KEXINIT
            var serverKexInit = BuildKexInit();
            SendPacket(serverKexInit);

            // Read client KEXINIT
            var clientKexInit = ReadPacket();
            if (clientKexInit[0] != SSH_MSG_KEXINIT) throw new IOException("expected KEXINIT");

            // Read KEXDH_INIT (e)
            var kexdh = ReadPacket();
            if (kexdh[0] != SSH_MSG_KEXDH_INIT) throw new IOException("expected KEXDH_INIT");
            var r = new SshReader(kexdh, 1);
            var e = r.MPInt();

            SshCrypto.DhServer(out var f, out var y);
            var k = e.ModPow(y, SshCrypto.DhP);
            var kBytes = MpintEncode(k);

            var hostKey = _crypto.HostKeyBlob();

            // Exchange hash H
            var hw = new SshWriter();
            hw.String(clientVersion);
            hw.String(ServerVersion);
            hw.String(clientKexInit);
            hw.String(serverKexInit);
            hw.String(hostKey);
            hw.MPInt(e);
            hw.MPInt(f);
            hw.Bytes(kBytes); // K already mpint-encoded
            var hHash = SshCrypto.Sha256(hw.ToArray());
            if (_sessionId == null) _sessionId = hHash;

            var sig = _crypto.SignExchangeHash(hHash);

            // KEXDH_REPLY: byte, string K_S, mpint f, string sig
            var rep = new SshWriter();
            rep.Byte(SSH_MSG_KEXDH_REPLY);
            rep.String(hostKey);
            rep.MPInt(f);
            rep.String(sig);
            SendPacket(rep.ToArray());

            // NEWKEYS
            SendPacket(new byte[] { SSH_MSG_NEWKEYS });
            var cliNewkeys = ReadPacket();
            if (cliNewkeys[0] != SSH_MSG_NEWKEYS) throw new IOException("expected NEWKEYS");

            // Derive keys and switch to encrypted
            var ivCS = SshCrypto.DeriveKey(kBytes, hHash, 'A', _sessionId, 16);
            var ivSC = SshCrypto.DeriveKey(kBytes, hHash, 'B', _sessionId, 16);
            var encCS = SshCrypto.DeriveKey(kBytes, hHash, 'C', _sessionId, 16);
            var encSC = SshCrypto.DeriveKey(kBytes, hHash, 'D', _sessionId, 16);
            var macCS = SshCrypto.DeriveKey(kBytes, hHash, 'E', _sessionId, 32);
            var macSC = SshCrypto.DeriveKey(kBytes, hHash, 'F', _sessionId, 32);

            _decCipher = SshCrypto.Aes128Ctr(encCS, ivCS, false); // client->server decrypt
            _encCipher = SshCrypto.Aes128Ctr(encSC, ivSC, true);  // server->client encrypt
            _macIn = SshCrypto.HmacSha256(macCS);
            _macOut = SshCrypto.HmacSha256(macSC);
            _encrypted = true;
            StartupTrace.Mark("sftp-kex-ok");
        }

        private byte[] BuildKexInit()
        {
            var w = new SshWriter();
            w.Byte(SSH_MSG_KEXINIT);
            var cookie = new byte[16]; SshCrypto.Random.NextBytes(cookie); w.Bytes(cookie);
            w.String("diffie-hellman-group14-sha256");  // kex
            w.String("ssh-rsa,rsa-sha2-256");            // host key algs
            w.String("aes128-ctr"); w.String("aes128-ctr"); // enc c2s / s2c
            w.String("hmac-sha2-256"); w.String("hmac-sha2-256"); // mac
            w.String("none"); w.String("none");          // compression
            w.String(""); w.String("");                  // languages
            w.Bool(false);                               // first_kex_packet_follows
            w.UInt32(0);                                 // reserved
            return w.ToArray();
        }

        // ---------- authentication ----------

        private void Authenticate()
        {
            // Expect SERVICE_REQUEST ssh-userauth
            var sr = ReadPacket();
            if (sr[0] == SSH_MSG_SERVICE_REQUEST)
            {
                var w = new SshWriter(); w.Byte(SSH_MSG_SERVICE_ACCEPT); w.String("ssh-userauth");
                SendPacket(w.ToArray());
            }

            while (true)
            {
                var p = ReadPacket();
                if (p[0] != SSH_MSG_USERAUTH_REQUEST) continue;
                var r = new SshReader(p, 1);
                var user = r.Utf8String();
                var service = r.Utf8String();
                var method = r.Utf8String();
                if (method == "password")
                {
                    r.Bool(); // FALSE
                    var pass = r.Utf8String();
                    if (user == _user && ConstantEquals(pass, _password))
                    {
                        SendPacket(new byte[] { SSH_MSG_USERAUTH_SUCCESS });
                        StartupTrace.Mark("sftp-auth-ok");
                        return;
                    }
                }
                // Fail -> offer password
                var f = new SshWriter();
                f.Byte(SSH_MSG_USERAUTH_FAILURE); f.String("password"); f.Bool(false);
                SendPacket(f.ToArray());
            }
        }

        // ---------- channels ----------

        private void ChannelLoop()
        {
            while (true)
            {
                var p = ReadPacket();
                switch (p[0])
                {
                    case SSH_MSG_GLOBAL_REQUEST:
                        // want_reply may be set; just refuse global requests.
                        break;
                    case SSH_MSG_CHANNEL_OPEN: HandleChannelOpen(p); break;
                    case SSH_MSG_CHANNEL_REQUEST: HandleChannelRequest(p); break;
                    case SSH_MSG_CHANNEL_DATA: HandleChannelData(p); break;
                    case SSH_MSG_CHANNEL_WINDOW_ADJUST:
                        {
                            var r = new SshReader(p, 1);
                            var ch = FindChannel(r.UInt32());
                            uint add = r.UInt32();
                            if (ch != null) ch.PeerWindow += add;
                        }
                        break;
                    case SSH_MSG_CHANNEL_EOF: break;
                    case SSH_MSG_CHANNEL_CLOSE:
                        {
                            var r = new SshReader(p, 1);
                            uint local = r.UInt32();
                            var ch = FindChannel(local);
                            if (ch != null)
                            {
                                var w = new SshWriter(); w.Byte(SSH_MSG_CHANNEL_CLOSE); w.UInt32(ch.Peer);
                                try { SendPacket(w.ToArray()); } catch { }
                                _channels.Remove(local);
                            }
                            // Closing one channel does NOT end the connection (gvfs keeps others open).
                        }
                        break;
                    case SSH_MSG_DISCONNECT: return;
                }
            }
        }

        private Channel FindChannel(uint local) =>
            _channels.TryGetValue(local, out var ch) ? ch : null;

        private void HandleChannelOpen(byte[] p)
        {
            var r = new SshReader(p, 1);
            var type = r.Utf8String();
            uint peer = r.UInt32();
            uint peerWindow = r.UInt32();
            r.UInt32(); // max packet
            if (type != "session")
            {
                var w = new SshWriter();
                w.Byte(SSH_MSG_CHANNEL_OPEN_FAILURE); w.UInt32(peer); w.UInt32(3);
                w.String("only session"); w.String("");
                SendPacket(w.ToArray());
                return;
            }
            uint local = _nextLocalChannel++;
            _channels[local] = new Channel { Peer = peer, PeerWindow = peerWindow, LocalWindowLeft = LocalWindow };
            var c = new SshWriter();
            c.Byte(SSH_MSG_CHANNEL_OPEN_CONFIRMATION);
            c.UInt32(peer); c.UInt32(local); c.UInt32(LocalWindow); c.UInt32(32768);
            SendPacket(c.ToArray());
            StartupTrace.Mark($"sftp-chan-open:{type} local={local} peer={peer}");
        }

        private void HandleChannelRequest(byte[] p)
        {
            var r = new SshReader(p, 1);
            uint local = r.UInt32(); // recipient (our local) channel
            var ch = FindChannel(local);
            var req = r.Utf8String();
            var wantReply = r.Bool();
            bool ok = false;
            if (req == "subsystem" && ch != null)
            {
                var name = r.Utf8String();
                StartupTrace.Mark($"sftp-chanreq:subsystem={name} local={local}");
                if (name == "sftp")
                {
                    ch.Sftp = new SftpSubsystem(_roots, data => SendChannelData(ch, data), _log);
                    ok = true;
                }
            }
            else StartupTrace.Mark($"sftp-chanreq:{req} local={local}");
            if (wantReply && ch != null)
                SendPacket(new byte[] { ok ? SSH_MSG_CHANNEL_SUCCESS : SSH_MSG_CHANNEL_FAILURE }
                    .PrependChannel(ch.Peer));
        }

        private void HandleChannelData(byte[] p)
        {
            var r = new SshReader(p, 1);
            var ch = FindChannel(r.UInt32());
            var data = r.String();
            if (ch == null) return;
            ch.LocalWindowLeft -= (uint)data.Length;
            ch.Sftp?.Feed(data);
            if (ch.LocalWindowLeft < LocalWindow / 2)
            {
                var w = new SshWriter();
                w.Byte(SSH_MSG_CHANNEL_WINDOW_ADJUST); w.UInt32(ch.Peer); w.UInt32(LocalWindow - ch.LocalWindowLeft);
                SendPacket(w.ToArray());
                ch.LocalWindowLeft = LocalWindow;
            }
        }

        private void SendChannelData(Channel ch, byte[] data)
        {
            int off = 0;
            while (off < data.Length)
            {
                int chunk = Math.Min(data.Length - off, 30000);
                var w = new SshWriter();
                w.Byte(SSH_MSG_CHANNEL_DATA); w.UInt32(ch.Peer); w.String(Slice(data, off, chunk));
                SendPacket(w.ToArray());
                off += chunk;
            }
        }

        // ---------- binary packet layer ----------

        private void SendPacket(byte[] payload)
        {
          lock (_sendLock)
          {
            int blockSize = _encrypted ? 16 : 8;
            int minLen = 4 + 1 + payload.Length; // packet_length field excluded from padding calc? no:
            // total (excl MAC) = 4(len) + 1(pad) + payload + padding must be multiple of blockSize, and
            // (1 + payload + padding) is packet_length. padding >= 4.
            int padLen = blockSize - ((5 + payload.Length) % blockSize);
            if (padLen < 4) padLen += blockSize;
            uint packetLen = (uint)(1 + payload.Length + padLen);

            var w = new SshWriter();
            w.UInt32(packetLen);
            w.Byte((byte)padLen);
            w.Bytes(payload);
            var pad = new byte[padLen]; SshCrypto.Random.NextBytes(pad); w.Bytes(pad);
            var packet = w.ToArray();

            byte[] mac = null;
            if (_encrypted)
            {
                var mw = new SshWriter(); mw.UInt32(_seqOut); mw.Bytes(packet);
                var mb = mw.ToArray();
                _macOut.BlockUpdate(mb, 0, mb.Length);
                mac = new byte[_macOut.GetMacSize()]; _macOut.DoFinal(mac, 0);
                packet = _encCipher.ProcessBytes(packet);
            }
            _out.Write(packet, 0, packet.Length);
            if (mac != null) _out.Write(mac, 0, mac.Length);
            _out.Flush();
            _seqOut++;
          }
        }

        private byte[] ReadPacket()
        {
            if (!_encrypted)
            {
                var first = ReadExact(4);
                uint pLen = (uint)((first[0] << 24) | (first[1] << 16) | (first[2] << 8) | first[3]);
                var rest = ReadExact((int)pLen);
                byte padLen = rest[0];
                var payload = Slice(rest, 1, rest.Length - 1 - padLen);
                _seqIn++;
                return payload;
            }
            else
            {
                // decrypt first block to get length
                var firstEnc = ReadExact(16);
                var firstDec = _decCipher.ProcessBytes(firstEnc);
                uint pLen = (uint)((firstDec[0] << 24) | (firstDec[1] << 16) | (firstDec[2] << 8) | firstDec[3]);
                int remaining = (int)pLen + 4 - 16; // total = 4+pLen bytes; already have 16
                var restEnc = ReadExact(remaining);
                var restDec = _decCipher.ProcessBytes(restEnc);
                var full = new byte[16 + restDec.Length];
                Array.Copy(firstDec, 0, full, 0, 16);
                Array.Copy(restDec, 0, full, 16, restDec.Length);
                var mac = ReadExact(32); // read + ignore verify (TOFU transport)
                byte padLen = full[4];
                var payload = Slice(full, 5, (int)pLen - 1 - padLen);
                _seqIn++;
                return payload;
            }
        }

        private byte[] ReadExact(int n)
        {
            var buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = _in.Read(buf, off, n - off);
                if (r <= 0) throw new IOException("eof");
                off += r;
            }
            return buf;
        }

        // ---------- helpers ----------

        private static byte[] MpintEncode(BigInteger v)
        {
            var w = new SshWriter(); w.MPInt(v); return w.ToArray();
        }

        private static byte[] Slice(byte[] a, int off, int len)
        {
            var o = new byte[len]; Array.Copy(a, off, o, 0, len); return o;
        }

        private static bool ConstantEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static string Trunc(string s) => s == null ? "" : (s.Length > 80 ? s.Substring(0, 80) : s);
    }

    internal static class SshServerExtensions
    {
        // CHANNEL_SUCCESS/FAILURE need the recipient channel appended after the message byte.
        public static byte[] PrependChannel(this byte[] msg, uint channel)
        {
            var w = new SshWriter(); w.Byte(msg[0]); w.UInt32(channel); return w.ToArray();
        }
    }
}
