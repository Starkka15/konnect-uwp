using System;
using Windows.Security.ExchangeActiveSyncProvisioning;
using ZorinConnect.Core;

namespace ZorinConnect.Helpers
{
    public static class DeviceHelper
    {
        public const int ProtocolVersion = 8; // Android DeviceHelper.kt:31

        private const string KeyDeviceId = "deviceId";
        private const string KeyDeviceName = "deviceName";
        private const string KeyDeviceNameUserSet = "deviceNameUserSet";

        private static string _deviceId;
        private static string _deviceName;

        /// <summary>32-hex UUID (Android DeviceHelper.kt:130-138), generated once.</summary>
        public static string DeviceId
        {
            get
            {
                if (_deviceId != null) return _deviceId;
                _deviceId = SettingsStore.GetString(SettingsStore.App, KeyDeviceId);
                if (string.IsNullOrEmpty(_deviceId))
                {
                    _deviceId = Guid.NewGuid().ToString("N");
                    SettingsStore.Set(SettingsStore.App, KeyDeviceId, _deviceId);
                }
                return _deviceId;
            }
        }

        public static string DeviceName
        {
            get
            {
                if (_deviceName != null) return _deviceName;

                // Only honor a cached name if it was explicitly set by the user (rename); a
                // previously auto-derived generic value should be recomputed with better logic.
                var cached = SettingsStore.GetString(SettingsStore.App, KeyDeviceName);
                bool userSet = SettingsStore.GetBool(SettingsStore.App, KeyDeviceNameUserSet);
                if (userSet && !string.IsNullOrEmpty(cached)) { _deviceName = cached; return _deviceName; }

                _deviceName = DeriveDeviceName();
                SettingsStore.Set(SettingsStore.App, KeyDeviceName, _deviceName);
                return _deviceName;
            }
            set
            {
                var filtered = DeviceInfo.FilterName(value);
                if (string.IsNullOrEmpty(filtered)) return;
                _deviceName = filtered;
                SettingsStore.Set(SettingsStore.App, KeyDeviceName, filtered);
                SettingsStore.Set(SettingsStore.App, KeyDeviceNameUserSet, true);
            }
        }

        /// <summary>
        /// Best device name from what W10M exposes. Prefer the user-configured phone name
        /// (Settings > System > About > Device name); else manufacturer + model. All raw fields
        /// are logged to StartupTrace so we can SEE what the device actually reports.
        /// </summary>
        private static string DeriveDeviceName()
        {
            string friendly = "", mfr = "", product = "", sku = "";
            try
            {
                var eas = new EasClientDeviceInformation();
                friendly = eas.FriendlyName ?? "";
                mfr = eas.SystemManufacturer ?? "";
                product = eas.SystemProductName ?? "";
                sku = eas.SystemSku ?? "";
            }
            catch { }
            Core.StartupTrace.Mark($"eas friendly='{friendly}' mfr='{mfr}' product='{product}' sku='{sku}'");

            // A user-set friendly name is the best. Treat obviously-generic values as absent.
            var f = DeviceInfo.FilterName(friendly);
            if (!IsGeneric(f)) return f;

            // Fall back to a cleaned manufacturer + model, e.g. "MicrosoftMDG" + "RM-1116_11258"
            // -> "Microsoft RM-1116".
            var cleanMfr = CleanManufacturer(mfr);
            var cleanModel = product;
            int us = cleanModel.IndexOf('_');
            if (us > 0) cleanModel = cleanModel.Substring(0, us); // drop the "_11258" build suffix
            var composed = DeviceInfo.FilterName($"{cleanMfr} {cleanModel}".Trim());
            if (!IsGeneric(composed)) return composed;

            var p = DeviceInfo.FilterName(product);
            if (!IsGeneric(p)) return p;

            return "Windows Phone";
        }

        private static string CleanManufacturer(string mfr)
        {
            if (string.IsNullOrEmpty(mfr)) return "";
            var l = mfr.ToLowerInvariant();
            if (l.Contains("microsoft")) return "Microsoft";
            if (l.Contains("nokia")) return "Nokia";
            if (l.Contains("hp")) return "HP";
            return mfr;
        }

        private static bool IsGeneric(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            var l = s.ToLowerInvariant();
            return l == "windows phone" || l == "windowsphone" || l == "unknown" || l.Length < 2;
        }

        public static DeviceInfo LocalDeviceInfo(System.Collections.Generic.HashSet<string> incoming,
                                                 System.Collections.Generic.HashSet<string> outgoing)
        {
            return new DeviceInfo(DeviceId)
            {
                Name = DeviceName,
                Type = DeviceType.Phone,
                ProtocolVersion = ProtocolVersion,
                IncomingCapabilities = incoming,
                OutgoingCapabilities = outgoing,
            };
        }
    }
}
