using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CitiesIIAgentBridge
{
    // Independent of Unity: the native adapter supplies home-district membership and age.
    internal sealed class DistrictCensus
    {
        private int residents, homeless, sick, injured, dead;
        private readonly int[] ages = new int[4], education = new int[5], students = new int[5];
        internal void Add(int age, int level, int? studentLevel, bool isHomeless, bool isSick, bool isInjured, bool isDead)
        {
            if (isDead) { dead++; return; }
            if (age < 0 || age >= ages.Length || level < 0 || level >= education.Length ||
                (studentLevel.HasValue && (studentLevel < 1 || studentLevel > 4)))
                throw new ArgumentException("unsupported_native_census_value");
            residents++; ages[age]++; education[level]++;
            if (studentLevel.HasValue) students[studentLevel.Value]++;
            if (isHomeless) homeless++;
            if (isSick) sick++;
            if (isInjured) injured++;
        }
        internal JObject Json() => new JObject {
            ["residents"] = residents, ["children"] = ages[0], ["teens"] = ages[1],
            ["adults"] = ages[2], ["seniors"] = ages[3], ["homelessResidents"] = homeless,
            ["sickResidents"] = sick, ["injuredResidents"] = injured, ["deadExcluded"] = dead,
            ["educationLevels0To4"] = new JArray(education), ["enrolledByLevel0To4"] = new JArray(students)
        };
    }

    internal static class DistrictAssignment
    {
        internal static string[] Keys(JToken value)
        {
            if (!(value is JArray rows) || rows.Count > 64) throw new ArgumentException("district_list_required_max_64");
            var keys = new List<string>();
            foreach (var row in rows)
            {
                if (!(row is JObject) || row["index"]?.Type != JTokenType.Integer || row["version"]?.Type != JTokenType.Integer ||
                    (int)row["index"] <= 0 || (int)row["version"] <= 0) throw new ArgumentException("district_entity_id_required");
                keys.Add((int)row["index"] + ":" + (int)row["version"]);
            }
            if (keys.Distinct().Count() != keys.Count) throw new ArgumentException("duplicate_district");
            return keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        }
        internal static void CheckExpected(JArray current, JToken expected)
        {
            if (!Keys(current).SequenceEqual(Keys(expected))) throw new InvalidOperationException("district_assignments_changed_refresh_before_retry");
        }
    }
}
