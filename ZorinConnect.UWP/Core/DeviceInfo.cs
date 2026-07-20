using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.X509;

namespace ZorinConnect.Core
{
    public enum DeviceType { Phone, Tablet, Desktop, Laptop, Tv }

    public static class DeviceTypeExtensions
    {
        public static string ToProtocolString(this DeviceType t)
        {
            switch (t)
            {
                case DeviceType.Phone: return "phone";
                case DeviceType.Tablet: return "tablet";
                case DeviceType.Laptop: return "laptop";
                case DeviceType.Tv: return "tv";
                default: return "desktop";
            }
        }

        public static DeviceType FromProtocolString(string s)
        {
            switch (s)
            {
                case "phone": return DeviceType.Phone;
                case "tablet": return DeviceType.Tablet;
                case "laptop": return DeviceType.Laptop;
                case "tv": return DeviceType.Tv;
                default: return DeviceType.Desktop;
            }
        }
    }

    /// <summary>Peer device identity. Mirrors Android DeviceInfo (SPEC §I.identity).</summary>
    public sealed class DeviceInfo
    {
        // SPEC §I: inbound deviceId regex
        private static readonly Regex IdRegex = new Regex("^[a-zA-Z0-9_-]{32,38}$", RegexOptions.Compiled);
        // SPEC §I: deviceName filter — strip "',;:.!?()[]<>  max 32
        private static readonly Regex NameFilterRegex = new Regex("[\"',;:.!?()\\[\\]<>]", RegexOptions.Compiled);

        public string Id { get; }
        public string Name { get; set; }
        public DeviceType Type { get; set; }
        public int ProtocolVersion { get; set; }
        public X509Certificate Certificate { get; set; }   // peer cert (TOFU); null until TLS handshake
        public HashSet<string> IncomingCapabilities { get; set; }
        public HashSet<string> OutgoingCapabilities { get; set; }

        public DeviceInfo(string id)
        {
            Id = id;
        }

        public static string FilterName(string name)
        {
            if (name == null) return "";
            var filtered = NameFilterRegex.Replace(name, "").Trim();
            return filtered.Length > 32 ? filtered.Substring(0, 32) : filtered;
        }

        public static bool IsValidId(string id) => id != null && IdRegex.IsMatch(id);

        /// <summary>SPEC §V5: identity invalid unless id matches regex and filtered name non-blank.</summary>
        public static DeviceInfo FromIdentityPacket(NetworkPacket np)
        {
            if (np.Type != NetworkPacket.TypeIdentity) return null;
            var id = np.GetString("deviceId");
            if (!IsValidId(id)) return null;
            var name = FilterName(np.GetString("deviceName"));
            if (string.IsNullOrEmpty(name)) return null;

            var info = new DeviceInfo(id)
            {
                Name = name,
                Type = DeviceTypeExtensions.FromProtocolString(np.GetString("deviceType", "desktop")),
                ProtocolVersion = np.GetInt("protocolVersion", 7),
                IncomingCapabilities = ToSet(np.GetArray("incomingCapabilities")),
                OutgoingCapabilities = ToSet(np.GetArray("outgoingCapabilities")),
            };
            return info;
        }

        private static HashSet<string> ToSet(JArray arr)
        {
            if (arr == null) return null;
            var set = new HashSet<string>();
            foreach (var tok in arr)
            {
                if (tok == null || tok.Type == JTokenType.Null) continue;
                var s = tok.ToString();
                if (!string.IsNullOrEmpty(s)) set.Add(s);
            }
            return set;
        }

        public void FillIdentityPacket(NetworkPacket np)
        {
            np.Set("deviceId", Id);
            np.Set("deviceName", Name);
            np.Set("protocolVersion", ProtocolVersion);
            np.Set("deviceType", Type.ToProtocolString());
            np.Set("incomingCapabilities", new JArray((IncomingCapabilities ?? new HashSet<string>()).OrderBy(s => s)));
            np.Set("outgoingCapabilities", new JArray((OutgoingCapabilities ?? new HashSet<string>()).OrderBy(s => s)));
        }
    }
}
