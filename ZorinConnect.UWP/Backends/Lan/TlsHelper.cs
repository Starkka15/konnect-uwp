using System;
using System.Collections;
using System.IO;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Tls;
using Org.BouncyCastle.Security;
using ZorinConnect.Core;
using ZorinConnect.Helpers;
using BcCertificate = Org.BouncyCastle.Crypto.Tls.Certificate;
using BcX509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace ZorinConnect.Backends.Lan
{
    /// <summary>
    /// BC TLS 1.2 glue (SPEC §I.tls, §V2, §V3). Legacy Org.BouncyCastle.Crypto.Tls API (BC 1.8.1).
    /// Role rule: TCP connector = TLS server, TCP accepter = TLS client.
    /// Paired peer -> pin: handshake aborts unless presented cert byte-equals stored cert.
    /// Unpaired -> accept any cert (TOFU at pair time).
    /// </summary>
    public sealed class TlsConnectionResult
    {
        public Stream Stream;                    // application data stream (post-handshake)
        public BcX509Certificate PeerCertificate; // captured leaf, null only if peer sent none (unpaired client)
        public TlsProtocol Protocol;
    }

    public static class TlsHelper
    {
        public static readonly SecureRandom Random = new SecureRandom();

        /// <summary>We dialed TCP -> we are TLS SERVER (SPEC §V2).</summary>
        public static TlsConnectionResult AsServer(Stream input, Stream output, BcX509Certificate pinnedPeer)
        {
            var protocol = new TlsServerProtocol(input, output, Random);
            var server = new ZcTlsServer(pinnedPeer);
            protocol.Accept(server); // blocks until handshake done or throws
            if (pinnedPeer != null && server.PeerCertificate == null)
                throw new IOException("paired peer sent no client certificate");
            return new TlsConnectionResult { Stream = protocol.Stream, PeerCertificate = server.PeerCertificate, Protocol = protocol };
        }

        /// <summary>We accepted TCP -> we are TLS CLIENT (SPEC §V2).</summary>
        public static TlsConnectionResult AsClient(Stream input, Stream output, BcX509Certificate pinnedPeer)
        {
            var protocol = new TlsClientProtocol(input, output, Random);
            var client = new ZcTlsClient(pinnedPeer);
            protocol.Connect(client); // blocks until handshake done or throws
            return new TlsConnectionResult { Stream = protocol.Stream, PeerCertificate = client.PeerCertificate, Protocol = protocol };
        }

        internal static BcCertificate OwnTlsCertificate()
        {
            return new BcCertificate(new[] { SslHelper.Certificate.CertificateStructure });
        }

        internal static void CheckPin(BcX509Certificate presented, BcX509Certificate pinned)
        {
            if (pinned == null) return; // unpaired: trust-all (SPEC §V3)
            if (presented == null || !Org.BouncyCastle.Utilities.Arrays.AreEqual(presented.GetEncoded(), pinned.GetEncoded()))
                throw new TlsFatalAlert(AlertDescription.certificate_unknown); // §V3 drop link
        }

        internal static BcX509Certificate Leaf(BcCertificate chain)
        {
            if (chain == null || chain.IsEmpty) return null;
            return new BcX509Certificate(chain.GetCertificateAt(0));
        }
    }

    internal sealed class ZcTlsServer : DefaultTlsServer
    {
        private readonly BcX509Certificate _pinnedPeer;
        public BcX509Certificate PeerCertificate { get; private set; }

        public ZcTlsServer(BcX509Certificate pinnedPeer) { _pinnedPeer = pinnedPeer; }

        protected override ProtocolVersion MinimumVersion => ProtocolVersion.TLSv12;
        protected override ProtocolVersion MaximumVersion => ProtocolVersion.TLSv12;

        protected override int[] GetCipherSuites()
        {
            // Our credentials are ECDSA (secp256r1 cert) -> ECDHE_ECDSA suites only.
            return new[]
            {
                CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
                CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
                CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA384,
                CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256,
                CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA,
                CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA,
            };
        }

        protected override TlsSignerCredentials GetECDsaSignerCredentials()
        {
            return new DefaultTlsSignerCredentials(mContext, TlsHelper.OwnTlsCertificate(), SslHelper.KeyPair.Private,
                new SignatureAndHashAlgorithm(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));
        }

        public override CertificateRequest GetCertificateRequest()
        {
            // Paired -> needClientAuth; unpaired -> wantClientAuth. Both expressed as a request;
            // enforcement of "need" happens in TlsHelper.AsServer (null cert + pin -> abort).
            var sigAlgs = new ArrayList
            {
                new SignatureAndHashAlgorithm(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa),
                new SignatureAndHashAlgorithm(HashAlgorithm.sha384, SignatureAlgorithm.ecdsa),
                new SignatureAndHashAlgorithm(HashAlgorithm.sha512, SignatureAlgorithm.ecdsa),
                new SignatureAndHashAlgorithm(HashAlgorithm.sha256, SignatureAlgorithm.rsa),
                new SignatureAndHashAlgorithm(HashAlgorithm.sha384, SignatureAlgorithm.rsa),
                new SignatureAndHashAlgorithm(HashAlgorithm.sha512, SignatureAlgorithm.rsa),
                new SignatureAndHashAlgorithm(HashAlgorithm.sha1, SignatureAlgorithm.ecdsa),
                new SignatureAndHashAlgorithm(HashAlgorithm.sha1, SignatureAlgorithm.rsa),
            };
            return new CertificateRequest(
                new[] { ClientCertificateType.ecdsa_sign, ClientCertificateType.rsa_sign },
                sigAlgs, null);
        }

        public override void NotifyClientCertificate(BcCertificate clientCertificate)
        {
            PeerCertificate = TlsHelper.Leaf(clientCertificate);
            TlsHelper.CheckPin(PeerCertificate, _pinnedPeer);
        }
    }

    internal sealed class ZcTlsClient : DefaultTlsClient
    {
        private readonly BcX509Certificate _pinnedPeer;
        public BcX509Certificate PeerCertificate { get; private set; }

        public ZcTlsClient(BcX509Certificate pinnedPeer) { _pinnedPeer = pinnedPeer; }

        public override ProtocolVersion MinimumVersion => ProtocolVersion.TLSv12;
        public override ProtocolVersion ClientVersion => ProtocolVersion.TLSv12;

        // Offer only ECDHE_ECDSA suites (GSConnect's cert is EC secp256r1) so the ServerKeyExchange
        // is signed with ECDSA — avoids RSA-PSS / other schemes whose hash byte BC 1.8.1 can't map.
        public override int[] GetCipherSuites() => new[]
        {
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA384,
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256,
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA,
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA,
        };

        // Advertise only hashes BC 1.8.1 can create (sha256/384/512). The default list can include
        // a hash the server then uses in its signature which BC's CreateHash rejects ("unknown
        // HashAlgorithm") — the actual payload-handshake failure.
        public override System.Collections.IDictionary GetClientExtensions()
        {
            var ext = base.GetClientExtensions() ?? new System.Collections.Hashtable();
            var sigAlgs = new System.Collections.ArrayList();
            foreach (byte hash in new[] { HashAlgorithm.sha256, HashAlgorithm.sha384, HashAlgorithm.sha512 })
            {
                sigAlgs.Add(new SignatureAndHashAlgorithm(hash, SignatureAlgorithm.ecdsa));
                sigAlgs.Add(new SignatureAndHashAlgorithm(hash, SignatureAlgorithm.rsa));
            }
            TlsUtilities.AddSignatureAlgorithmsExtension(ext, sigAlgs);
            StartupTrace.Mark($"client-ext-sigalgs:{sigAlgs.Count}");
            return ext;
        }

        public override TlsAuthentication GetAuthentication()
        {
            return new ZcTlsAuthentication(this);
        }

        private sealed class ZcTlsAuthentication : TlsAuthentication
        {
            private readonly ZcTlsClient _outer;
            public ZcTlsAuthentication(ZcTlsClient outer) { _outer = outer; }

            public void NotifyServerCertificate(BcCertificate serverCertificate)
            {
                _outer.PeerCertificate = TlsHelper.Leaf(serverCertificate);
                TlsHelper.CheckPin(_outer.PeerCertificate, _outer._pinnedPeer);
            }

            public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
            {
                // Pick an ECDSA sig-alg that the server actually advertised — hardcoding
                // (sha256,ecdsa) makes DefaultTlsSignerCredentials throw when the request lists a
                // different set, and BC then sends an EMPTY cert (GSConnect: "peer did not send a
                // certificate"). Our key is EC secp256r1, so any *_ecdsa the server lists works.
                SignatureAndHashAlgorithm chosen = null;
                var supported = certificateRequest?.SupportedSignatureAlgorithms;
                if (supported != null)
                {
                    foreach (SignatureAndHashAlgorithm sa in supported)
                    {
                        if (sa.Signature == SignatureAlgorithm.ecdsa)
                        {
                            if (sa.Hash == HashAlgorithm.sha256) { chosen = sa; break; } // prefer sha256
                            if (chosen == null) chosen = sa;
                        }
                    }
                }
                if (chosen == null) chosen = new SignatureAndHashAlgorithm(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa);
                StartupTrace.Mark($"payload-clientcreds:{chosen.Hash}/{chosen.Signature}");
                return new DefaultTlsSignerCredentials(_outer.mContext, TlsHelper.OwnTlsCertificate(),
                    SslHelper.KeyPair.Private, chosen);
            }
        }
    }
}
