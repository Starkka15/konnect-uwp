using System;
using System.Collections.Generic;
using System.Linq;

namespace ZorinConnect.Plugins
{
    /// <summary>
    /// Static registry of all plugin types (no reflection scan — .NET Native-safe explicit list).
    /// Add each new plugin's factory here. Capability sets are the union across all plugins,
    /// advertised in the identity packet (§V10 — only implemented types appear).
    /// </summary>
    public static class PluginRegistry
    {
        // Explicit factories — ⊥ reflection (Activator/Type scan) under .NET Native.
        private static readonly List<Func<IPlugin>> Factories = new List<Func<IPlugin>>
        {
            () => new PingPlugin(),
        };

        public static IEnumerable<IPlugin> CreateAll() => Factories.Select(f => f());

        /// <summary>Union of every plugin's supported (incoming) types.</summary>
        public static HashSet<string> AllIncomingCapabilities()
        {
            var set = new HashSet<string>();
            foreach (var p in CreateAll())
                foreach (var t in p.SupportedPacketTypes) set.Add(t);
            return set;
        }

        /// <summary>Union of every plugin's outgoing types.</summary>
        public static HashSet<string> AllOutgoingCapabilities()
        {
            var set = new HashSet<string>();
            foreach (var p in CreateAll())
                foreach (var t in p.OutgoingPacketTypes) set.Add(t);
            return set;
        }
    }
}
