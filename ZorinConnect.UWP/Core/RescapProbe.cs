using System;
using System.Threading.Tasks;
using Windows.Foundation.Metadata;

namespace ZorinConnect.Core
{
    /// <summary>
    /// Restricted-capability probe (SPEC T31). Tries each privileged API on the sideloaded device
    /// and records allowed/denied to StartupTrace, so we know which parity plugins (Remote Input,
    /// Messaging, Telephony) are actually reachable before building them.
    /// </summary>
    public static class RescapProbe
    {
        public static async Task RunAsync()
        {
            StartupTrace.Mark("rescap-probe-start");

            // 1. inputInjectionBrokered -> InputInjector (Remote Input / MouseReceiver)
            try
            {
                if (ApiInformation.IsTypePresent("Windows.UI.Input.Preview.Injection.InputInjector"))
                {
                    var inj = Windows.UI.Input.Preview.Injection.InputInjector.TryCreate();
                    StartupTrace.Mark($"rescap-inputinjector:{(inj != null ? "OK" : "null")}");
                }
                else StartupTrace.Mark("rescap-inputinjector:type-absent");
            }
            catch (Exception e) { StartupTrace.MarkError("rescap-inputinjector", e); }

            // 2. chatSystem/chat -> ChatMessageStore (Messaging / SMS read). Time-boxed: this can
            // block indefinitely (consent prompt / never-returns) on some W10M builds.
            await Probe("chatstore", async () =>
            {
                if (!ApiInformation.IsMethodPresent("Windows.ApplicationModel.Chat.ChatMessageManager", "RequestStoreAsync"))
                    return "method-absent";
                var store = await Windows.ApplicationModel.Chat.ChatMessageManager.RequestStoreAsync();
                if (store == null) return "null";
                long count = -1;
                try { var b = await store.GetMessageReader().ReadBatchAsync(); count = b.Count; } catch { }
                return $"OK:msgs={count}";
            });

            // 3. phoneCallHistorySystem -> PhoneCallHistoryStore (Telephony call log)
            await Probe("callhistory", async () =>
            {
                if (!ApiInformation.IsTypePresent("Windows.ApplicationModel.Calls.PhoneCallHistoryManager"))
                    return "type-absent";
                var store = await Windows.ApplicationModel.Calls.PhoneCallHistoryManager.RequestStoreAsync(
                    Windows.ApplicationModel.Calls.PhoneCallHistoryStoreAccessType.AllEntriesLimitedReadWrite);
                return store != null ? "OK" : "null";
            });

            // 4. phoneCall -> PhoneLine enumeration (Telephony call state / mute)
            await Probe("phoneline", async () =>
            {
                if (!ApiInformation.IsTypePresent("Windows.ApplicationModel.Calls.PhoneCallStore"))
                    return "type-absent";
                var store = await Windows.ApplicationModel.Calls.PhoneCallManager.RequestStoreAsync();
                var lineId = await store.GetDefaultLineAsync();
                return lineId != Guid.Empty ? "OK" : "empty";
            });

            StartupTrace.Mark("rescap-probe-done");
        }

        private static async Task Probe(string name, Func<Task<string>> body)
        {
            try
            {
                var task = body();
                var done = await Task.WhenAny(task, Task.Delay(6000));
                if (done != task) { StartupTrace.Mark($"rescap-{name}:TIMEOUT"); return; }
                StartupTrace.Mark($"rescap-{name}:{task.Result}");
            }
            catch (Exception e) { StartupTrace.MarkError($"rescap-{name}", e); }
        }
    }
}
