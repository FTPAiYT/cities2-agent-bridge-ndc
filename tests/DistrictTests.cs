using System;
using CitiesIIAgentBridge;
using Newtonsoft.Json.Linq;

internal static class DistrictTests
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool yes) { if (!yes) throw new Exception("District regression"); count++; }
        void Reject(Action action) { try { action(); } catch (ArgumentException) { count++; return; } catch (InvalidOperationException) { count++; return; } throw new Exception("Expected district rejection"); }
        var census = new DistrictCensus();
        census.Add(0, 0, 1, false, false, false, false);
        census.Add(1, 1, 2, false, false, false, false);
        census.Add(2, 3, 4, true, true, false, false);
        census.Add(3, 4, null, false, false, true, false);
        census.Add(3, 2, null, false, false, false, true);
        var result = census.Json();
        Check((int)result["residents"] == 4 && (int)result["deadExcluded"] == 1);
        foreach (var key in new[] { "children", "teens", "adults", "seniors", "homelessResidents", "sickResidents", "injuredResidents" }) Check((int)result[key] == 1);
        Check((int)result["enrolledByLevel0To4"][1] == 1 && (int)result["enrolledByLevel0To4"][4] == 1 && (int)result["enrolledByLevel0To4"][3] == 0);
        Reject(() => census.Add(4,0,null,false,false,false,false));
        Reject(() => census.Add(0,5,null,false,false,false,false));
        Reject(() => census.Add(0,0,0,false,false,false,false));
        Check((int)census.Json()["residents"] == 4); // Invalid reads cannot partially increment counts.
        JArray Ids(int version = 1) => new JArray(new JObject { ["index"]=10, ["version"]=version }, new JObject { ["index"]=20, ["version"]=1 });
        var current = Ids(); var reversed = new JArray(current[1].DeepClone(), current[0].DeepClone());
        DistrictAssignment.CheckExpected(current,reversed); count++;
        Reject(() => DistrictAssignment.CheckExpected(current,Ids(2)));
        Reject(() => DistrictAssignment.CheckExpected(current,new JArray()));
        DistrictAssignment.CheckExpected(new JArray(),new JArray()); count++;
        Reject(() => DistrictAssignment.Keys(null));
        Reject(() => DistrictAssignment.Keys(new JArray(current[0].DeepClone(),current[0].DeepClone())));
        Reject(() => DistrictAssignment.Keys(new JArray(new JObject { ["index"]=0,["version"]=1 })));
        Reject(() => DistrictAssignment.Keys(new JArray(new JObject { ["index"]="10",["version"]=1 })));
        return count;
    }
}
