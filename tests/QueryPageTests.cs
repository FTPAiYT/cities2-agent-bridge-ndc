using System;
using System.Linq;
using CitiesIIAgentBridge;
using Newtonsoft.Json.Linq;

static class QueryPageTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool ok) { if (!ok) throw new Exception("Pagination regression"); checks++; }
        var collected = new System.Collections.Generic.List<int>();
        int offset = 0;
        do {
            var page = new QueryPage(new JObject { ["offset"] = offset },512,4096);
            var rows = new JArray(Enumerable.Range(0,1739).Where(page.Contains));
            var result = page.Result("buildings",rows,1739);
            collected.AddRange(rows.Values<int>());
            Check((int)result["total"] == 1739);
            if (!(bool)result["truncated"]) break;
            Check((int)result["nextOffset"] > offset);
            offset = (int)result["nextOffset"];
        } while (true);
        Check(collected.SequenceEqual(Enumerable.Range(0,1739)));
        var beyond = new QueryPage(new JObject { ["offset"] = int.MaxValue },512,4096);
        Check(!beyond.Contains(1738) && !(bool)beyond.Result("rows",new JArray(),1739)["truncated"]);
        foreach(var args in new[] {new JObject { ["offset"]=-1 },new JObject { ["limit"]=0 },new JObject { ["limit"]=4097 }}) {
            bool failed=false; try {new QueryPage(args,512,4096);} catch(ArgumentException) {failed=true;} Check(failed);
        }
        return checks;
    }
}
