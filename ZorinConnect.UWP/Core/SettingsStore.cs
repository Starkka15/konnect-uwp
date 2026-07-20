using System.Collections.Generic;
using System.Linq;
using Windows.Storage;

namespace ZorinConnect.Core
{
    /// <summary>
    /// SPEC §I.storage — LocalSettings containers mirroring Android SharedPreferences:
    ///   "app"             deviceId, deviceName, privateKey b64, certificate b64
    ///   "trusted_devices" deviceId -> bool
    ///   "<deviceId>"      per-device: certificate, deviceName, deviceType, protocolVersion, plugin enables
    /// </summary>
    public static class SettingsStore
    {
        private static ApplicationDataContainer Root => ApplicationData.Current.LocalSettings;

        public static ApplicationDataContainer App => Container("app");
        public static ApplicationDataContainer TrustedDevices => Container("trusted_devices");
        public static ApplicationDataContainer ForDevice(string deviceId) => Container(deviceId);

        private static ApplicationDataContainer Container(string name) =>
            Root.CreateContainer(name, ApplicationDataCreateDisposition.Always);

        public static string GetString(ApplicationDataContainer c, string key, string def = null) =>
            c.Values.TryGetValue(key, out var v) ? v as string ?? def : def;

        public static void Set(ApplicationDataContainer c, string key, object value) => c.Values[key] = value;

        public static bool GetBool(ApplicationDataContainer c, string key, bool def = false) =>
            c.Values.TryGetValue(key, out var v) && v is bool b ? b : def;

        public static int GetInt(ApplicationDataContainer c, string key, int def = 0) =>
            c.Values.TryGetValue(key, out var v) && v is int i ? i : def;

        public static IEnumerable<string> TrustedDeviceIds() =>
            TrustedDevices.Values.Where(kv => kv.Value is bool b && b).Select(kv => kv.Key).ToList();

        public static bool IsTrusted(string deviceId) => GetBool(TrustedDevices, deviceId);

        public static void SetTrusted(string deviceId, bool trusted)
        {
            if (trusted) TrustedDevices.Values[deviceId] = true;
            else TrustedDevices.Values.Remove(deviceId);
        }

        /// <summary>SPEC §V18: unpair clears the whole per-device container.</summary>
        public static void WipeDevice(string deviceId)
        {
            SetTrusted(deviceId, false);
            Root.DeleteContainer(deviceId);
        }

        /// <summary>SPEC §V11: cert regen wipes all remembered devices.</summary>
        public static void WipeAllDevices()
        {
            foreach (var id in TrustedDeviceIds())
                Root.DeleteContainer(id);
            foreach (var key in TrustedDevices.Values.Keys.ToList())
                TrustedDevices.Values.Remove(key);
        }
    }
}
