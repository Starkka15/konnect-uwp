using System;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Security;
using ZorinConnect.Helpers;

namespace ZorinConnect.Core
{
    public enum PairState { NotPaired, Requested, RequestedByPeer, Paired }

    /// <summary>
    /// KDE Connect v8 pairing state machine (SPEC §V7-V9). Mirrors Android PairingHandler.kt.
    /// Sends/receives kdeconnect.pair over the established (TLS) link.
    /// </summary>
    public sealed class PairingHandler
    {
        private const int RequestTimeoutMs = 30000;        // our outgoing request
        private const int PeerRequestDisplayMs = 25000;    // peer will time out at 30s
        private const long AllowedTimestampDiffSec = 1800; // §V7 clock-skew window

        private readonly string _deviceId;
        private readonly Func<NetworkPacket, bool> _sendPacket;
        private readonly Func<Org.BouncyCastle.X509.X509Certificate> _peerCertProvider;

        private CancellationTokenSource _timeoutCts;

        public PairState State { get; private set; }
        public event Action<PairState> StateChanged;
        public event Action<string> PairingFailed; // reason string

        public PairingHandler(string deviceId, PairState initial,
                              Func<NetworkPacket, bool> sendPacket,
                              Func<Org.BouncyCastle.X509.X509Certificate> peerCertProvider)
        {
            _deviceId = deviceId;
            State = initial;
            _sendPacket = sendPacket;
            _peerCertProvider = peerCertProvider;
        }

        // ---- packet in ----

        public void PacketReceived(NetworkPacket np)
        {
            bool wantsPair = np.GetBool("pair");
            if (wantsPair) HandlePairRequest(np);
            else HandleUnpair();
        }

        private void HandlePairRequest(NetworkPacket np)
        {
            switch (State)
            {
                case PairState.Requested: // our request accepted by peer
                    CancelTimeout();
                    SetPaired();
                    break;

                case PairState.RequestedByPeer:
                    // duplicate request — ignore
                    break;

                case PairState.Paired:
                    // already paired; peer re-requesting -> treat as accepted (Android re-pairs)
                    break;

                default: // NotPaired -> incoming request
                    long theirTs = np.GetLong("timestamp", -1);
                    if (theirTs < 0)
                    {
                        PairingFailed?.Invoke("no timestamp (protocol < 8 not offered)");
                        return;
                    }
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (Math.Abs(theirTs - now) > AllowedTimestampDiffSec)
                    {
                        Reject();
                        PairingFailed?.Invoke("clocks do not match");
                        return;
                    }
                    _incomingTimestamp = theirTs;
                    SetState(PairState.RequestedByPeer);
                    StartTimeout(PeerRequestDisplayMs, () =>
                    {
                        if (State == PairState.RequestedByPeer)
                        {
                            SetState(PairState.NotPaired);
                            PairingFailed?.Invoke("timed out");
                        }
                    });
                    break;
            }
        }

        private void HandleUnpair()
        {
            CancelTimeout();
            switch (State)
            {
                case PairState.Requested:
                case PairState.RequestedByPeer:
                    SetState(PairState.NotPaired);
                    PairingFailed?.Invoke("canceled by peer");
                    break;
                case PairState.Paired:
                    SetState(PairState.NotPaired);
                    break;
            }
        }

        // ---- local actions ----

        private long _incomingTimestamp = -1;
        private long _outgoingTimestamp = -1;

        /// <summary>We request pairing (§V7): {pair:true, timestamp}.</summary>
        public void RequestPairing()
        {
            if (State == PairState.Paired) return;
            _outgoingTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var np = new NetworkPacket(NetworkPacket.TypePair)
                .Set("pair", true).Set("timestamp", _outgoingTimestamp);
            if (!_sendPacket(np)) { PairingFailed?.Invoke("not reachable"); return; }
            SetState(PairState.Requested);
            StartTimeout(RequestTimeoutMs, () =>
            {
                if (State == PairState.Requested)
                {
                    SetState(PairState.NotPaired);
                    PairingFailed?.Invoke("timed out");
                }
            });
        }

        /// <summary>Accept an incoming request (§V9): reply {pair:true} (NO timestamp) -> Paired.</summary>
        public void AcceptPairing()
        {
            if (State != PairState.RequestedByPeer) return;
            CancelTimeout();
            var np = new NetworkPacket(NetworkPacket.TypePair).Set("pair", true);
            if (!_sendPacket(np)) { PairingFailed?.Invoke("not reachable"); return; }
            SetPaired();
        }

        public void RejectPairing()
        {
            CancelTimeout();
            Reject();
            SetState(PairState.NotPaired);
        }

        public void Unpair()
        {
            CancelTimeout();
            Reject();
            SetState(PairState.NotPaired);
        }

        private void Reject()
        {
            var np = new NetworkPacket(NetworkPacket.TypePair).Set("pair", false);
            _sendPacket(np);
        }

        // ---- verification key (§V8) ----

        /// <summary>
        /// SHA-256( concat(pubkeyDER larger-first by unsigned compare) + ASCII-decimal(timestamp) ),
        /// first 8 hex UPPER. Available only during Requested / RequestedByPeer.
        /// </summary>
        public string VerificationKey()
        {
            if (State != PairState.Requested && State != PairState.RequestedByPeer) return null;
            var peer = _peerCertProvider();
            if (peer == null) return null;

            byte[] mine = SslHelper.Certificate.GetPublicKey() is Org.BouncyCastle.Crypto.AsymmetricKeyParameter
                ? Org.BouncyCastle.X509.SubjectPublicKeyInfoFactory
                    .CreateSubjectPublicKeyInfo(SslHelper.Certificate.GetPublicKey()).GetDerEncoded()
                : null;
            byte[] theirs = Org.BouncyCastle.X509.SubjectPublicKeyInfoFactory
                .CreateSubjectPublicKeyInfo(peer.GetPublicKey()).GetDerEncoded();
            if (mine == null) return null;

            byte[] concat = CompareUnsigned(mine, theirs) < 0 ? Concat(theirs, mine) : Concat(mine, theirs);
            long ts = State == PairState.Requested ? _outgoingTimestamp : _incomingTimestamp;
            byte[] tsBytes = System.Text.Encoding.ASCII.GetBytes(ts.ToString());
            byte[] input = Concat(concat, tsBytes);

            var digest = DigestUtilities.CalculateDigest("SHA-256", input);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 4; i++) sb.Append(digest[i].ToString("X2"));
            return sb.ToString();
        }

        private static int CompareUnsigned(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int d = (a[i] & 0xFF) - (b[i] & 0xFF);
                if (d != 0) return d;
            }
            return a.Length - b.Length;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            System.Buffer.BlockCopy(a, 0, r, 0, a.Length);
            System.Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        // ---- state plumbing ----

        private void SetPaired() => SetState(PairState.Paired);

        private void SetState(PairState s)
        {
            State = s;
            StateChanged?.Invoke(s);
        }

        private void StartTimeout(int ms, Action onTimeout)
        {
            CancelTimeout();
            _timeoutCts = new CancellationTokenSource();
            var token = _timeoutCts.Token;
            Task.Delay(ms, token).ContinueWith(t =>
            {
                if (!t.IsCanceled) onTimeout();
            }, TaskScheduler.Default);
        }

        private void CancelTimeout()
        {
            _timeoutCts?.Cancel();
            _timeoutCts = null;
        }
    }
}
