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
                _deviceName = SettingsStore.GetString(SettingsStore.App, KeyDeviceName);
                if (string.IsNullOrEmpty(_deviceName))
                {
                    var eas = new EasClientDeviceInformation();
                    var candidate = DeviceInfo.FilterName(eas.FriendlyName);
                    if (string.IsNullOrEmpty(candidate))
                        candidate = DeviceInfo.FilterName(eas.SystemProductName);
                    _deviceName = string.IsNullOrEmpty(candidate) ? "Windows Phone" : candidate;
                    SettingsStore.Set(SettingsStore.App, KeyDeviceName, _deviceName);
                }
                return _deviceName;
            }
            set
            {
                var filtered = DeviceInfo.FilterName(value);
                if (string.IsNullOrEmpty(filtered)) return;
                _deviceName = filtered;
                SettingsStore.Set(SettingsStore.App, KeyDeviceName, filtered);
            }
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
