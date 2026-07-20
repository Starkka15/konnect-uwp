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

        /// <summary>Payload port the peer is serving on (0 if none). Explicit cast — no Value&lt;T&gt;.</summary>
        public int PayloadPort
        {
            get
            {
                if (PayloadTransferInfo != null && PayloadTransferInfo.TryGetValue("port", out var t)
                    && t.Type == JTokenType.Integer) return (int)(long)t;
                return 0;
            }
        }

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
        // Explicit JToken casts only — the generic JToken.Value<T>() path fast-fails under .NET
        // Native ARM (missing reflection metadata for the instantiations). See SPEC §B2/§V22.
        public string GetString(string key, string def = "")
        {
            var t = Body[key];
            return t == null || t.Type == JTokenType.Null ? def : t.ToString();
        }
        public int GetInt(string key, int def = 0) => (int)GetLongInternal(key, def);
        public long GetLong(string key, long def = 0) => GetLongInternal(key, def);
        public double GetDouble(string key, double def = 0)
        {
            var t = Body[key];
            if (t == null || t.Type == JTokenType.Null) return def;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return (double)t;
            return double.TryParse(t.ToString(), out var v) ? v : def;
        }
        public bool GetBool(string key, bool def = false)
        {
            var t = Body[key];
            if (t == null || t.Type == JTokenType.Null) return def;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            return bool.TryParse(t.ToString(), out var v) ? v : def;
        }
        public JArray GetArray(string key) => Body[key] as JArray;
        public bool Has(string key) => Body[key] != null;

        private long GetLongInternal(string key, long def)
        {
            var t = Body[key];
            if (t == null || t.Type == JTokenType.Null) return def;
            if (t.Type == JTokenType.Integer) return (long)t;
            return long.TryParse(t.ToString(), out var v) ? v : def;
        }

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
            if (PayloadSize != 0 && PayloadTransferInfo != null)
            {
                root["payloadSize"] = PayloadSize;
                root["payloadTransferInfo"] = PayloadTransferInfo;
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
            StartupTrace.Mark("deser-parse");
            var root = JObject.Parse(line);
            StartupTrace.Mark("deser-parsed");
            long id = ToLong(root["id"]);
            StartupTrace.Mark("deser-id");
            var type = (string)root["type"];
            StartupTrace.Mark($"deser-type:{type}");
            if (string.IsNullOrEmpty(type))
                throw new JsonException("packet has no type");
            var body = root["body"] as JObject;
            long payloadSize = ToLong(root["payloadSize"]);
            var pti = root["payloadTransferInfo"] as JObject;
            return new NetworkPacket(id, type, body, payloadSize, pti);
        }

        private static long ToLong(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return 0;
            if (t.Type == JTokenType.Integer) return (long)t;
            long.TryParse(t.ToString(), out var v);
            return v;
        }
    }
}
