using System;
using CitiesIIAgentBridge;
using Newtonsoft.Json.Linq;

static class TerrainSampleTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("Terrain sampling: " + label); checks++; }
        bool Cell(double x, double z, out int index) => TerrainSampleValues.TryCellIndex(x, z, 14336, 256, out index);
        Check(Cell(-7168, -7168, out int i) && i == 0, "negative map corner is first cell");
        Check(Cell(-0.1, -0.1, out i) && i == 127 * 256 + 127, "negative coordinates floor rather than truncate");
        Check(Cell(0, 0, out i) && i == 128 * 256 + 128, "map centre uses correct axes");
        Check(Cell(56, 0, out i) && i == 128 * 256 + 129, "x advances one column");
        Check(Cell(0, 56, out i) && i == 129 * 256 + 128, "z advances one row");
        Check(Cell(7167.99, 7167.99, out i) && i == 65535, "last cell stays in bounds");
        Check(!Cell(7168, 0, out i) && i == -1, "positive edge excluded, not clamped");
        Check(!Cell(-7168.01, 0, out i), "negative exterior excluded");
        Check(!Cell(0, -14000, out i) && !Cell(0, 14000, out i), "backdrop does not borrow edge resources");
        Check(!Cell(double.NaN, 0, out i) && !Cell(0, double.PositiveInfinity, out i), "non-finite coordinates rejected");
        Check(!TerrainSampleValues.TryCellIndex(0, 0, 0, 256, out i), "invalid map metadata rejected");
        var resource = TerrainSampleValues.Resource(8000, 2500);
        Check((int)resource["availableRaw"] == 5500 && (int)resource["usedRaw"] == 2500, "remaining subtracts usage");
        Check((int)TerrainSampleValues.Resource(10, 20)["availableRaw"] == 0, "overused resource never becomes negative or wraps");
        Check((int)TerrainSampleValues.Resource(0, 0)["availableRaw"] == 0, "real zero fertility remains zero");
        var groundwater = TerrainSampleValues.Groundwater(100, 200, 25);
        Check((double)groundwater["pollutionFraction"] == 0.25 && (int)groundwater["maxRaw"] == 200, "groundwater amount is distinct from capacity and pollution");
        Check(TerrainSampleValues.Groundwater(0, 200, 0)["pollutionFraction"].Type == JTokenType.Null, "empty groundwater is not reported as clean water");
        Check(TerrainSampleValues.Groundwater(10, 200, 20)["pollutionFraction"].Type == JTokenType.Null, "invalid polluted amount is not a credible ratio");
        var row = new JObject();
        TerrainSampleValues.Water(row, "ok", 12.5, 0.125, -2, 3);
        Check((double)row["waterDepth"] == 12.5 && (double)row["waterVelocity"][0] == -2, "water values and signed flow preserved");
        TerrainSampleValues.Water(row, "ok", 0, 0, 0, 0);
        Check((string)row["waterStatus"] == "ok" && (double)row["waterDepth"] == 0, "valid dry land is zero");
        TerrainSampleValues.Water(row, "unavailable");
        Check(row["waterDepth"].Type == JTokenType.Null && row["waterVelocity"].Type == JTokenType.Null, "missing water never masquerades as dry land");
        TerrainSampleValues.Water(row, "out_of_bounds");
        Check((string)row["waterStatus"] == "out_of_bounds" && row["waterPollution"].Type == JTokenType.Null, "out-of-map water stays unknown");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1.0 }) {
            TerrainSampleValues.Water(row, "ok", invalid);
            Check((string)row["waterStatus"] == "invalid" && row["waterDepth"].Type == JTokenType.Null, "invalid depth cannot escape as numeric data");
        }
        TerrainSampleValues.Water(row, "ok", 1, 0, double.NaN, 0);
        Check((string)row["waterStatus"] == "invalid", "invalid flow invalidates water sample");
        return checks;
    }
}
