using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZorinConnect.Core
{
    /// <summary>
    /// KDE Connect wire packet. Serialized form is SPEC §I.packet:
    /// single-line JSON {"id":ms,"type":"kdeconnect.X","body":{...}} + "\n", UTF-8,
    /// "\/" unescaped to "/" (QJson compat, matches Android NetworkPacket.serialize()).
    /// </summary>
    public sealed class NetworkPacket
    {
        public const string TypeIdentity = "kdeconnect.identity";
        public const string TypePair = "kdeconnect.pair";

        // Identity/UDP packets larger than this are dropped (SPEC §V5).
        public const int MaxIdentityPacketSize = 1024 * 512;

        public long Id { get; set; }
        public string Type { get; }
        public JObject Body { get; }

        // Payload (SPEC §I.payload). PayloadSize -1 = open-ended stream (not used by Android; kept for parse tolerance).
        public long PayloadSize { get; set; }
        public JObject PayloadTransferInfo { get; set; }
        public Func<System.IO.Stream> PayloadFactory { get; set; }

        public bool HasPayload => PayloadFactory != null && PayloadSize != 0;
        public bool HasPayloadTransferInfo => PayloadTransferInfo != null && PayloadTransferInfo.Count > 0;

        public NetworkPacket(string type)
        {
            Type = type;
            Body = new JObject();
            Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private NetworkPacket(long id, string type, JObject body, long payloadSize, JObject payloadTransferInfo)
        {
            Id = id;
            Type = type;
            Body = body ?? new JObject();
            PayloadSize = payloadSize;
            PayloadTransferInfo = payloadTransferInfo;
        }

        // ---- body accessors (Android NetworkPacket API shape) ----
        public string GetString(string key, string def = "") => Body.TryGetValue(key, out var t) ? t.Value<string>() ?? def : def;
        public int GetInt(string key, int def = 0) => Body.TryGetValue(key, out var t) ? t.Value<int>() : def;
        public long GetLong(string key, long def = 0) => Body.TryGetValue(key, out var t) ? t.Value<long>() : def;
        public bool GetBool(string key, bool def = false) => Body.TryGetValue(key, out var t) ? t.Value<bool>() : def;
        public double GetDouble(string key, double def = 0) => Body.TryGetValue(key, out var t) ? t.Value<double>() : def;
        public JArray GetArray(string key) => Body.TryGetValue(key, out var t) ? t as JArray : null;
        public bool Has(string key) => Body.ContainsKey(key);

        public NetworkPacket Set(string key, string v) { Body[key] = v; return this; }
        public NetworkPacket Set(string key, int v) { Body[key] = v; return this; }
        public NetworkPacket Set(string key, long v) { Body[key] = v; return this; }
        public NetworkPacket Set(string key, bool v) { Body[key] = v; return this; }
        public NetworkPacket Set(string key, double v) { Body[key] = v; return this; }
        public NetworkPacket Set(string key, JToken v) { Body[key] = v; return this; }

        /// <summary>Single line + trailing \n. Matches Android serialize(): id refreshed at send time by caller if desired.</summary>
        public string Serialize()
        {
            var root = new JObject
            {
                ["id"] = Id,
                ["type"] = Type,
                ["body"] = Body,
            };
            if (HasPayload)
            {
                root["payloadSize"] = PayloadSize;
                root["payloadTransferInfo"] = PayloadTransferInfo ?? new JObject();
            }
            var json = JsonConvert.SerializeObject(root, Formatting.None);
            // QJson compat: Android does jsonString.replace("\\/", "/"). Newtonsoft doesn't escape
            // forward slashes, but body values built from other JSON sources may carry the escape.
            json = json.Replace("\\/", "/");
            return json + "\n";
        }

        /// <summary>Parse one line (without or with trailing newline). Throws JsonException on malformed input.</summary>
        public static NetworkPacket Deserialize(string line)
        {
            var root = JObject.Parse(line);
            var id = root.TryGetValue("id", out var idTok) ? idTok.Value<long>() : 0; // id not correlated on receive
            var type = root.Value<string>("type");
            if (string.IsNullOrEmpty(type))
                throw new JsonException("packet has no type");
            var body = root["body"] as JObject;
            long payloadSize = root.TryGetValue("payloadSize", out var ps) ? ps.Value<long>() : 0;
            var pti = root["payloadTransferInfo"] as JObject;
            return new NetworkPacket(id, type, body, payloadSize, pti);
        }
    }
}
