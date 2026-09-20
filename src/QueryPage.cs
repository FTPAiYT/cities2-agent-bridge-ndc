using System;
using Newtonsoft.Json.Linq;

namespace CitiesIIAgentBridge
{
    internal sealed class QueryPage
    {
        public readonly int Offset, Limit;
        public QueryPage(JObject args, int defaultLimit, int maximum)
        {
            Offset = (int?)args["offset"] ?? 0;
            Limit = (int?)args["limit"] ?? defaultLimit;
            if (Offset < 0 || Limit < 1 || Limit > maximum) throw new ArgumentException("invalid_offset_or_limit");
        }
        public bool Contains(int index) => index >= Offset && (long)index < (long)Offset + Limit;
        public JObject Result(string key, JArray rows, int total)
        {
            bool more = (long)Offset + rows.Count < total;
            return new JObject { [key] = rows, ["offset"] = Offset, ["limit"] = Limit, ["total"] = total,
                ["truncated"] = more, ["nextOffset"] = more ? (JToken)(Offset + rows.Count) : JValue.CreateNull() };
        }
    }
}
