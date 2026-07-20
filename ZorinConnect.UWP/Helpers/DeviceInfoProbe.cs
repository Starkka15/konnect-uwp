using System;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.System.Profile;
using ZorinConnect.Core;

namespace ZorinConnect.Helpers
{
    /// <summary>
    /// Diagnostic: enumerate every device-info source W10M exposes and log the raw values, so we
    /// pick the source that actually names the hardware instead of guessing/munging a model code.
    /// Results land in StartupTrace (readable via WDP).
    /// </summary>
    public static class DeviceInfoProbe
    {
        public static async Task ProbeAsync()
        {
            try
            {
                var eas = new EasClientDeviceInformation();
                StartupTrace.Mark($"eas.FriendlyName='{eas.FriendlyName}'");
                StartupTrace.Mark($"eas.SystemManufacturer='{eas.SystemManufacturer}'");
                StartupTrace.Mark($"eas.SystemProductName='{eas.SystemProductName}'");
                StartupTrace.Mark($"eas.SystemSku='{eas.SystemSku}'");
                StartupTrace.Mark($"eas.SystemHardwareVersion='{eas.SystemHardwareVersion}'");
                StartupTrace.Mark($"eas.OperatingSystem='{eas.OperatingSystem}'");
            }
            catch (Exception e) { StartupTrace.MarkError("eas", e); }

            try
            {
                StartupTrace.Mark($"AnalyticsInfo.DeviceForm='{AnalyticsInfo.DeviceForm}'");
                StartupTrace.Mark($"AnalyticsInfo.DeviceFamily='{AnalyticsInfo.VersionInfo.DeviceFamily}'");
            }
            catch (Exception e) { StartupTrace.MarkError("analytics", e); }

            // Device container of the root HAL device — this is where a real "model name" lives.
            try
            {
                string[] props =
                {
                    "System.Devices.ModelName",
                    "System.Devices.Manufacturer",
                    "System.ItemNameDisplay",
                    "System.Devices.DeviceInstanceId",
                };
                var root = await PnpDeviceRootAsync(props);
                StartupTrace.Mark(root == null ? "pnp.root=null" : $"pnp.root found");
                if (root != null)
                    foreach (var p in props)
                        StartupTrace.Mark($"pnp.{p}='{(root.Properties.TryGetValue(p, out var v) ? v : null)}'");
            }
            catch (Exception e) { StartupTrace.MarkError("pnp", e); }
        }

        private static async Task<DeviceInformation> PnpDeviceRootAsync(string[] props)
        {
            // HAL / root device container.
            var col = await DeviceInformation.FindAllAsync(
                "System.Devices.DevObjectType:=7", props);
            return col != null && col.Count > 0 ? col[0] : null;
        }
    }
}
