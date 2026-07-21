using System;
using System.Collections.Generic;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Windows.Storage;

namespace ZorinConnect.Backends.Sftp
{
    /// <summary>
    /// SSH crypto for our minimal server: RSA host key, DH group-14 (SHA-256) KEX, RFC 4253 key
    /// derivation, AES-128-CTR cipher + HMAC-SHA256. All on BouncyCastle (pure managed, .NET Native).
    /// </summary>
    internal sealed class SshCrypto
    {
        public static readonly SecureRandom Random = new SecureRandom();

        // diffie-hellman-group14 prime (2048-bit MODP), generator 2.
        public static readonly BigInteger DhP = new BigInteger(
            "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
            "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
            "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
            "83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
            "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA0510" +
            "15728E5A8AACAA68FFFFFFFFFFFFFFFF", 16);
        public static readonly BigInteger DhG = BigInteger.Two;

        private readonly RsaKeyParameters _hostPub;
        private readonly RsaKeyParameters _hostPriv;

        private const string KeyModulus = "sftp_hostkey_n";
        private const string KeyPrivExp = "sftp_hostkey_d";
        private const string KeyPubExp  = "sftp_hostkey_e";

        public SshCrypto()
        {
            // STABLE host key: like the reference server ("reuse device keys"), persist ONE RSA host
            // key in LocalSettings and reuse it across every mount. Regenerating per-mount churns the
            // desktop's known_hosts and breaks re-mounting.
            var s = ApplicationData.Current.LocalSettings.Values;
            if (s[KeyModulus] is string nHex && s[KeyPrivExp] is string dHex && s[KeyPubExp] is string eHex)
            {
                var n = new BigInteger(nHex, 16);
                var d = new BigInteger(dHex, 16);
                var e = new BigInteger(eHex, 16);
                _hostPub = new RsaKeyParameters(false, n, e);
                _hostPriv = new RsaKeyParameters(true, n, d);
            }
            else
            {
                var gen = new RsaKeyPairGenerator();
                gen.Init(new RsaKeyGenerationParameters(BigInteger.ValueOf(0x10001), Random, 2048, 100));
                var kp = gen.GenerateKeyPair();
                _hostPub = (RsaKeyParameters)kp.Public;
                _hostPriv = (RsaKeyParameters)kp.Private;
                s[KeyModulus] = _hostPub.Modulus.ToString(16);
                s[KeyPrivExp] = _hostPriv.Exponent.ToString(16);
                s[KeyPubExp]  = _hostPub.Exponent.ToString(16);
            }
        }

        /// <summary>SSH host-key blob: string("ssh-rsa"), mpint(e), mpint(n).</summary>
        public byte[] HostKeyBlob()
        {
            var w = new SshWriter();
            w.String("ssh-rsa");
            w.MPInt(_hostPub.Exponent);
            w.MPInt(_hostPub.Modulus);
            return w.ToArray();
        }

        /// <summary>Sign the exchange hash H with rsa-sha2-256; blob = string("rsa-sha2-256"), string(sig).</summary>
        public byte[] SignExchangeHash(byte[] h)
        {
            var signer = new RsaDigestSigner(new Sha256Digest());
            signer.Init(true, _hostPriv);
            signer.BlockUpdate(h, 0, h.Length);
            var sig = signer.GenerateSignature();
            var w = new SshWriter();
            w.String("rsa-sha2-256");
            w.String(sig);
            return w.ToArray();
        }

        public static byte[] Sha256(byte[] data)
        {
            var d = new Sha256Digest();
            d.BlockUpdate(data, 0, data.Length);
            var o = new byte[d.GetDigestSize()];
            d.DoFinal(o, 0);
            return o;
        }

        /// <summary>DH: pick y, output f = g^y mod p and the secret y.</summary>
        public static void DhServer(out BigInteger f, out BigInteger y)
        {
            y = new BigInteger(256, Random);
            f = DhG.ModPow(y, DhP);
        }

        /// <summary>RFC 4253 §7.2 key material: HASH(K||H||X||session_id), extended to `len` bytes.</summary>
        public static byte[] DeriveKey(byte[] kEncoded, byte[] h, char x, byte[] sessionId, int len)
        {
            var w = new SshWriter();
            w.Bytes(kEncoded);   // K already encoded as mpint
            w.Bytes(h);
            w.Byte((byte)x);
            w.Bytes(sessionId);
            var k1 = Sha256(w.ToArray());
            var result = new List<byte>(k1);
            while (result.Count < len)
            {
                var w2 = new SshWriter();
                w2.Bytes(kEncoded);
                w2.Bytes(h);
                w2.Bytes(result.ToArray());
                result.AddRange(Sha256(w2.ToArray()));
            }
            return result.GetRange(0, len).ToArray();
        }

        /// <summary>AES-128-CTR stream cipher (SSH uses CTR as a keystream over each packet).</summary>
        public static IBufferedCipher Aes128Ctr(byte[] key, byte[] iv, bool forEncryption)
        {
            var cipher = new BufferedBlockCipher(new SicBlockCipher(new AesEngine()));
            cipher.Init(forEncryption, new ParametersWithIV(new KeyParameter(key, 0, 16), iv, 0, 16));
            return cipher;
        }

        public static HMac HmacSha256(byte[] key)
        {
            var mac = new HMac(new Sha256Digest());
            mac.Init(new KeyParameter(key, 0, 32));
            return mac;
        }
    }
}
