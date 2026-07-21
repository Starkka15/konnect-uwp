using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Networking;
using Windows.Networking.Connectivity;

namespace ZorinConnect.Backends.Lan
{
    public static class NetworkHelper
    {
        /// <summary>
        /// Subnet-directed broadcast addresses for every active IPv4 adapter (e.g. 192.168.5.255).
        /// W10M fast-fails on the limited broadcast 255.255.255.255; the directed broadcast is
        /// delivered normally and still reaches KDE Connect peers on the same subnet.
        /// </summary>
        /// <summary>The phone's own IPv4 on the active adapter (for advertising the SFTP host).</summary>
        public static string LocalIPv4()
        {
            foreach (var host in NetworkInformation.GetHostNames())
            {
                if (host.Type != HostNameType.Ipv4) continue;
                if (host.IPInformation?.PrefixLength == null) continue;
                int prefix = (int)host.IPInformation.PrefixLength.Value;
                if (prefix > 0 && prefix < 32) return host.CanonicalName;
            }
            return null;
        }

        public static List<string> BroadcastAddresses()
        {
            var result = new List<string>();
            foreach (var host in NetworkInformation.GetHostNames())
            {
                if (host.Type != HostNameType.Ipv4) continue;
                var info = host.IPInformation;
                if (info?.PrefixLength == null) continue;
                if (!TryParseIpv4(host.CanonicalName, out var ip)) continue;

                int prefix = (int)info.PrefixLength.Value;
                if (prefix <= 0 || prefix >= 32) continue; // skip /32 loopbacks etc.
                uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
                uint bcast = ip | ~mask;
                result.Add(FormatIpv4(bcast));
            }
            return result.Distinct().ToList();
        }

        private static bool TryParseIpv4(string s, out uint value)
        {
            value = 0;
            var parts = s.Split('.');
            if (parts.Length != 4) return false;
            uint v = 0;
            foreach (var p in parts)
            {
                if (!byte.TryParse(p, out var b)) return false;
                v = (v << 8) | b;
            }
            value = v;
            return true;
        }

        private static string FormatIpv4(uint v) =>
            $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";
    }
}
