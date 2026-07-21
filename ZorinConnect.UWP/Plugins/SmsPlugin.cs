using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.ApplicationModel.Chat;
using Windows.Foundation.Metadata;
using ZorinConnect.Core;

namespace ZorinConnect.Plugins
{
    /// <summary>
    /// SMS / Messaging (SPEC T32). Mirrors the phone's texts to the desktop via ChatMessageStore
    /// (chatSystem cap, confirmed by T31). Implements the KDE Connect sms.messages v2 protocol:
    ///  - request_conversations -> one sms.messages per thread (its newest message)
    ///  - request_conversation {threadID,...} -> that thread's messages
    ///  - live updates via ChatMessageStore.MessageChanged (after the desktop has requested once)
    ///  - sms.request (send) -> SmsDevice2 (best-effort; needs modem access)
    /// </summary>
    public sealed class SmsPlugin : IPlugin
    {
        private const string TypeMessages = "kdeconnect.sms.messages";
        private const string TypeAttachmentFile = "kdeconnect.sms.attachment_file";
        private const string TypeRequest = "kdeconnect.sms.request";
        private const string TypeRequestConversations = "kdeconnect.sms.request_conversations";
        private const string TypeRequestConversation = "kdeconnect.sms.request_conversation";
        private const string TypeRequestAttachment = "kdeconnect.sms.request_attachment";

        private PluginContext _ctx;
        private ChatMessageStore _store;
        private bool _haveRequested;
        // threadId (long, from hashed ConversationId) -> ConversationId (string)
        private readonly Dictionary<long, string> _threadMap = new Dictionary<long, string>();

        public string Key => "SmsPlugin";
        public string DisplayName => "SMS";
        public bool EnabledByDefault => true;
        public IEnumerable<string> SupportedPacketTypes => new[]
            { TypeRequest, TypeRequestConversations, TypeRequestConversation, TypeRequestAttachment };
        public IEnumerable<string> OutgoingPacketTypes => new[] { TypeMessages, TypeAttachmentFile };

        public void OnCreate(PluginContext context)
        {
            _ctx = context;
            var _ = InitStoreAsync();
        }

        public void OnDestroy()
        {
            try { if (_store != null) _store.MessageChanged -= OnMessageChanged; } catch { }
            _store = null;
        }

        private async Task InitStoreAsync()
        {
            try
            {
                if (!ApiInformation.IsMethodPresent("Windows.ApplicationModel.Chat.ChatMessageManager", "RequestStoreAsync"))
                    return;
                _store = await ChatMessageManager.RequestStoreAsync();
                if (_store != null)
                {
                    try { _store.ChangeTracker.Enable(); } catch { }
                    _store.MessageChanged += OnMessageChanged;
                }
            }
            catch (Exception e) { _ctx?.Log?.Invoke($"sms store init failed: {e.Message}"); }
        }

        public bool OnPacketReceived(NetworkPacket np)
        {
            switch (np.Type)
            {
                case TypeRequestConversations: StartupTrace.Mark("sms-req-convs"); var _ = SendConversationsAsync(); return true;
                case TypeRequestConversation: StartupTrace.Mark($"sms-req-conv:{np.GetLong("threadID")}"); var __ = SendConversationAsync(np); return true;
                case TypeRequest: var ___ = SendSmsAsync(np); return true;
                case TypeRequestAttachment: return true; // MMS attachments: TODO
                default: return false;
            }
        }

        // ---- request_conversations: newest message of every thread ----

        private async Task SendConversationsAsync()
        {
            if (_store == null) await InitStoreAsync();
            if (_store == null) return;
            _haveRequested = true;
            try
            {
                // ONE digest packet with the newest message of EVERY thread. GSConnect treats a
                // packet with multiple thread_ids as a digest (-> requests each full conversation);
                // separate single-message packets are misread as one-message threads (no history).
                var reader = _store.GetConversationReader();
                var digest = new JArray();
                while (true)
                {
                    var batch = await reader.ReadBatchAsync();
                    if (batch == null || batch.Count == 0) break;
                    foreach (var conv in batch)
                    {
                        try
                        {
                            var msg = await _store.GetMessageAsync(conv.MostRecentMessageId);
                            if (msg == null) continue;
                            Remember(conv.Id);
                            digest.Add(MessageToJson(msg, conv));
                        }
                        catch { }
                    }
                }
                StartupTrace.Mark($"sms-convs-sent:{digest.Count}");
                SendMessagesPacket(digest);
            }
            catch (Exception e) { _ctx?.Log?.Invoke($"sms conversations failed: {e.Message}"); }
        }

        // ---- request_conversation: a thread's messages ----

        private async Task SendConversationAsync(NetworkPacket np)
        {
            if (_store == null) return;
            long threadId = np.GetLong("threadID");
            if (threadId == 0) threadId = np.GetLong("thread_id");
            if (!_threadMap.TryGetValue(threadId, out var convId)) return;

            long rangeStart = np.GetLong("rangeStartTimestamp", -1);
            int limit = np.GetInt("numberToRequest", 0);
            try
            {
                var conv = await _store.GetConversationAsync(convId);
                if (conv == null) { StartupTrace.Mark($"sms-conv-notfound:{convId}"); return; }
                var reader = conv.GetMessageReader();
                var arr = new JArray();
                int count = 0;
                while (true)
                {
                    var batch = await reader.ReadBatchAsync();
                    if (batch == null || batch.Count == 0) break;
                    foreach (var m in batch)
                    {
                        long date = m.LocalTimestamp.ToUnixTimeMilliseconds();
                        if (rangeStart >= 0 && date < rangeStart) continue;
                        arr.Add(MessageToJson(m, conv));
                        if (limit > 0 && ++count >= limit) break;
                    }
                    if (limit > 0 && count >= limit) break;
                }
                StartupTrace.Mark($"sms-conv-sent:{arr.Count}");
                SendMessagesPacket(arr);
            }
            catch (Exception e) { StartupTrace.MarkError("sms-conv", e); _ctx?.Log?.Invoke($"sms conversation failed: {e.Message}"); }
        }

        // ---- live updates ----

        private void OnMessageChanged(ChatMessageStore sender, ChatMessageChangedEventArgs args)
        {
            if (!_haveRequested) return;
            var _ = HandleChangeAsync();
        }

        private async Task HandleChangeAsync()
        {
            try
            {
                var tracker = _store.ChangeTracker;
                var reader = tracker.GetChangeReader();
                var changes = await reader.ReadBatchAsync();
                foreach (var change in changes)
                {
                    if (change.ChangeType == ChatMessageChangeType.MessageCreated ||
                        change.ChangeType == ChatMessageChangeType.MessageModified)
                    {
                        var m = change.Message;
                        if (m != null)
                        {
                            Remember(m.ThreadingInfo?.ConversationId);
                            SendMessagesPacket(new JArray { MessageToJson(m, null) });
                        }
                    }
                }
                reader.AcceptChanges();
            }
            catch (Exception e) { _ctx?.Log?.Invoke($"sms change failed: {e.Message}"); }
        }

        // ---- send ----

        private async Task SendSmsAsync(NetworkPacket np)
        {
            string body = np.GetString("messageBody");
            if (string.IsNullOrEmpty(body)) return;
            var recipients = new List<string>();
            var addresses = np.GetArray("addresses");
            if (addresses != null)
                foreach (var a in addresses)
                {
                    var addr = (a as JObject)?["address"]?.ToString();
                    if (!string.IsNullOrEmpty(addr)) recipients.Add(addr);
                }
            if (recipients.Count == 0 && np.Has("phoneNumber")) recipients.Add(np.GetString("phoneNumber"));
            if (recipients.Count == 0) return;

            // Silent send via SmsDevice2. On W10M this is privileged (OEM/carrier/default-messaging
            // app only) -> access-denied for a sideloaded app. No composer popup (bad UX); just log.
            try
            {
                var dev = Windows.Devices.Sms.SmsDevice2.GetDefault();
                if (dev != null)
                {
                    var msg = new Windows.Devices.Sms.SmsTextMessage2 { Body = body, To = recipients[0] };
                    await dev.SendMessageAndGetResultAsync(msg);
                    StartupTrace.Mark("sms-sent");
                    _ctx?.Log?.Invoke($"sms sent to {recipients[0]}");
                    return;
                }
                _ctx?.Log?.Invoke("sms send: no SMS device");
            }
            catch (Exception e)
            {
                StartupTrace.Mark($"sms-send-fail:{Trunc(e.Message)}");
                _ctx?.Log?.Invoke($"sms send failed (W10M restricts SMS send to privileged apps): {e.Message}");
            }
        }

        // ---- helpers ----

        private void SendMessagesPacket(JArray messages)
        {
            if (messages == null || messages.Count == 0) return;
            var np = new NetworkPacket(TypeMessages).Set("version", 2).Set("messages", messages);
            _ctx?.SendPacket(np);
        }

        private JObject MessageToJson(ChatMessage m, ChatConversation conv)
        {
            var addresses = new JArray();
            string party = m.IsIncoming
                ? (string.IsNullOrEmpty(m.From) ? FirstParticipant(conv) : m.From)
                : (m.Recipients != null && m.Recipients.Count > 0 ? m.Recipients[0] : FirstParticipant(conv));
            addresses.Add(new JObject { ["address"] = party ?? "" });

            var convId = m.ThreadingInfo?.ConversationId ?? conv?.Id;
            return new JObject
            {
                ["addresses"] = addresses,
                ["body"] = m.Body ?? "",
                ["date"] = m.LocalTimestamp.ToUnixTimeMilliseconds(),
                ["type"] = m.IsIncoming ? 1 : 2,          // MESSAGE_TYPE_INBOX / SENT
                ["read"] = m.IsRead ? 1 : 0,
                ["thread_id"] = ThreadId(convId),
                ["_id"] = StableHash(m.Id),
                ["sub_id"] = 0,
                ["event"] = 1,                             // 0x1 = text
            };
        }

        private static string FirstParticipant(ChatConversation conv)
        {
            if (conv?.Participants != null && conv.Participants.Count > 0) return conv.Participants[0];
            return "";
        }

        private void Remember(string convId)
        {
            if (string.IsNullOrEmpty(convId)) return;
            _threadMap[ThreadId(convId)] = convId;
        }

        private long ThreadId(string convId) => convId == null ? 0 : StableHash(convId);

        private static string Trunc(string s) => s == null ? "" : (s.Length > 60 ? s.Substring(0, 60) : s);

        /// <summary>
        /// Stable positive hash of a string (FNV-1a), masked to 53 bits. GSConnect is JavaScript
        /// (float64), which only holds integers exactly up to 2^53 — a wider value gets rounded and
        /// the round-tripped threadID no longer matches our map (full history never loads).
        /// </summary>
        private static long StableHash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            ulong h = 1469598103934665603UL;
            foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
            return (long)(h & 0x1FFFFFFFFFFFFFUL); // 53 bits
        }
    }
}
