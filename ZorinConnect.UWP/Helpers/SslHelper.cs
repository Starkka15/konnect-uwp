using System;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using ZorinConnect.Core;

namespace ZorinConnect.Helpers
{
    /// <summary>
    /// Own keypair + self-signed cert (SPEC §I.cert): X.509v3, CN=deviceId OU=KDE Connect O=KDE,
    /// serial 1, notBefore now-1y, notAfter now+10y, EC secp256r1, SHA512withECDSA.
    /// Stored b64 in SettingsStore.App ("privateKey" PKCS#8, "publicKey" SPKI, "certificate" DER).
    /// </summary>
    public static class SslHelper
    {
        private const string KeyPrivate = "privateKey";
        private const string KeyPublic = "publicKey";
        private const string KeyCertificate = "certificate";

        private static AsymmetricCipherKeyPair _keyPair;
        private static X509Certificate _certificate;

        public static AsymmetricCipherKeyPair KeyPair
        {
            get { EnsureInitialized(); return _keyPair; }
        }

        public static X509Certificate Certificate
        {
            get { EnsureInitialized(); return _certificate; }
        }

        public static byte[] CertificateDer => Certificate.GetEncoded();

        public static void EnsureInitialized()
        {
            if (_certificate != null) return;

            var app = SettingsStore.App;
            var privB64 = SettingsStore.GetString(app, KeyPrivate);
            var certB64 = SettingsStore.GetString(app, KeyCertificate);

            if (privB64 != null && certB64 != null)
            {
                try
                {
                    var priv = PrivateKeyFactory.CreateKey(Convert.FromBase64String(privB64));
                    var cert = new X509CertificateParser().ReadCertificate(Convert.FromBase64String(certB64));

                    // SPEC §V11 regen conditions: CN != deviceId, expired, not yet valid.
                    var cn = GetCn(cert);
                    var now = DateTime.UtcNow;
                    if (cn == DeviceHelper.DeviceId && now >= cert.NotBefore && now <= cert.NotAfter)
                    {
                        _keyPair = new AsymmetricCipherKeyPair(cert.GetPublicKey(), priv);
                        _certificate = cert;
                        return;
                    }
                }
                catch
                {
                    // corrupt stored material -> regenerate below
                }
                // SPEC §V11: regeneration wipes remembered devices
                SettingsStore.WipeAllDevices();
            }

            Generate();
        }

        private static void Generate()
        {
            var random = new SecureRandom();

            // EC secp256r1 named curve (Android RsaHelper.kt API>=23 path). Named-curve OID in the
            // SPKI is required — explicit curve params would break peer-side key parsing.
            var ecGen = new ECKeyPairGenerator();
            ecGen.Init(new ECKeyGenerationParameters(Org.BouncyCastle.Asn1.Sec.SecObjectIdentifiers.SecP256r1, random));
            var keyPair = ecGen.GenerateKeyPair();

            var name = new X509Name($"CN={DeviceHelper.DeviceId}, OU=KDE Connect, O=KDE");
            var gen = new X509V3CertificateGenerator();
            gen.SetSerialNumber(BigInteger.One);
            gen.SetSubjectDN(name);
            gen.SetIssuerDN(name);
            gen.SetNotBefore(DateTime.UtcNow.Date.AddYears(-1));
            gen.SetNotAfter(DateTime.UtcNow.Date.AddYears(10));
            gen.SetPublicKey(keyPair.Public);

            var signer = new Asn1SignatureFactory("SHA512WITHECDSA", keyPair.Private, random);
            var cert = gen.Generate(signer);

            var app = SettingsStore.App;
            SettingsStore.Set(app, KeyPrivate, Convert.ToBase64String(
                PrivateKeyInfoFactory.CreatePrivateKeyInfo(keyPair.Private).GetDerEncoded()));
            SettingsStore.Set(app, KeyPublic, Convert.ToBase64String(
                Org.BouncyCastle.X509.SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(keyPair.Public).GetDerEncoded()));
            SettingsStore.Set(app, KeyCertificate, Convert.ToBase64String(cert.GetEncoded()));

            _keyPair = keyPair;
            _certificate = cert;
        }

        private static string GetCn(X509Certificate cert)
        {
            var values = cert.SubjectDN.GetValueList(X509Name.CN);
            return values.Count > 0 ? values[0]?.ToString() : null;
        }

        // ---- peer cert TOFU store (SPEC §I.storage per-device "certificate") ----

        public static void StorePeerCertificate(string deviceId, X509Certificate cert)
        {
            SettingsStore.Set(SettingsStore.ForDevice(deviceId), "certificate", Convert.ToBase64String(cert.GetEncoded()));
        }

        public static X509Certificate LoadPeerCertificate(string deviceId)
        {
            var b64 = SettingsStore.GetString(SettingsStore.ForDevice(deviceId), "certificate");
            if (b64 == null) return null;
            try { return new X509CertificateParser().ReadCertificate(Convert.FromBase64String(b64)); }
            catch { return null; }
        }

        /// <summary>Display fingerprint: SHA-256 hex bytes ':'-separated with trailing colon (Android SslHelper.java:259-271).</summary>
        public static string CertificateHash(X509Certificate cert)
        {
            var digest = DigestUtilities.CalculateDigest("SHA-256", cert.GetEncoded());
            var sb = new System.Text.StringBuilder();
            foreach (var b in digest) sb.Append(b.ToString("x2")).Append(':');
            return sb.ToString();
        }
    }
}
