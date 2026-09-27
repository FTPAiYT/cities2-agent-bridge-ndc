using System;
using Newtonsoft.Json.Linq;

namespace CitiesIIAgentBridge
{
    // Pure value/coordinate policy; native maps are read only by the main-thread adapter.
    internal static class TerrainSampleValues
    {
        internal static bool TryCellIndex(double x, double z, int mapSize, int resolution, out int index)
        {
            index = -1;
            if (mapSize <= 0 || resolution <= 0 || !Finite(x) || !Finite(z)) return false;
            double half = mapSize / 2.0;
            if (x < -half || x >= half || z < -half || z >= half) return false;
            int cx = (int)Math.Floor((x + half) * resolution / mapSize);
            int cz = (int)Math.Floor((z + half) * resolution / mapSize);
            long cell = (long)cz * resolution + cx;
            if (cx < 0 || cz < 0 || cx >= resolution || cz >= resolution || cell > int.MaxValue) return false;
            index = (int)cell;
            return true;
        }

        internal static JObject Resource(ushort total, ushort used) => new JObject {
            ["baseRaw"] = total, ["usedRaw"] = used, ["availableRaw"] = Math.Max(0, (int)total - used)
        };

        internal static JObject Groundwater(short amount, short maximum, short polluted) => new JObject {
            ["amountRaw"] = amount, ["maxRaw"] = maximum, ["pollutedRaw"] = polluted,
            // Empty water has no meaningful pollution fraction; preserve raw values for diagnosis.
            ["pollutionFraction"] = amount > 0 && polluted >= 0 && polluted <= amount
                ? (JToken)((double)polluted / amount) : JValue.CreateNull()
        };

        internal static void Water(JObject row, string status, double depth = 0, double pollution = 0, double vx = 0, double vz = 0)
        {
            if (status == "ok" && (!Finite(depth) || depth < 0 || !Finite(pollution) || pollution < 0 || !Finite(vx) || !Finite(vz)))
                status = "invalid";
            row["waterStatus"] = status;
            row["waterDepth"] = status == "ok" ? (JToken)depth : JValue.CreateNull();
            row["waterPollution"] = status == "ok" ? (JToken)pollution : JValue.CreateNull();
            row["waterVelocity"] = status == "ok" ? (JToken)new JArray(vx, vz) : JValue.CreateNull();
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
