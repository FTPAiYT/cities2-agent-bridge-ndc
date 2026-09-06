using System;
using System.Collections.Generic;
using System.Linq;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;

namespace CitiesIIAgentBridge
{
    public sealed partial class Mod
    {
        private JObject neighborhoodPlan;
        private DateTime neighborhoodExpires;
        private JObject NeighborhoodStatus(JObject args)
        {
            if(neighborhoodPlan==null || (string)args["id"]!=(string)neighborhoodPlan["id"])throw new ArgumentException("plan_not_found");
            var result=(JObject)neighborhoodPlan.DeepClone(); result["expired"]=DateTime.UtcNow>neighborhoodExpires;
            if(result["batchId"]!=null && (string)batch?["id"]==(string)result["batchId"])result["execution"]=batch.DeepClone();
            return result;
        }
        private JObject PlanNeighborhood(JObject args)
        {
            var w=RequireCity(); var em=w.EntityManager;
            var steps=new JArray(); var errors=new JArray(); var warnings=new JArray();
            long ceiling=(long?)args["maxTotalCost"]??0,reserve=(long?)args["reserve"]??0;
            if(ceiling<1 || ceiling>10000000 || reserve<0)throw new ArgumentException("positive_maxTotalCost_and_nonnegative_reserve_required");
            var roads=args["roads"] as JArray ?? new JArray(); var networks=args["utilities"] as JArray ?? new JArray(); var buildings=args["buildings"] as JArray ?? new JArray(); var zones=args["zones"] as JArray ?? new JArray();
            roads=(JArray)roads.DeepClone();zones=(JArray)zones.DeepClone();
            if(args["grid"] is JObject grid)
            {
                int columns=RequiredInt(grid,"columns"),rows=RequiredInt(grid,"rows");
                float width=RequiredFloat(grid,"blockWidth"),depth=RequiredFloat(grid,"blockDepth"),ox=RequiredFloat(grid,"x"),oz=RequiredFloat(grid,"z");
                if(columns<1||columns>4||rows<1||rows>4||width<64||width>200||depth<64||depth>200)throw new ArgumentException("grid_requires_1_to_4_blocks_of_64_to_200_metres");
                int road=RequiredInt(grid,"roadPrefabIndex"),roadVersion=RequiredInt(grid,"roadPrefabVersion"),zone=RequiredInt(grid,"zonePrefabIndex"),zoneVersion=RequiredInt(grid,"zonePrefabVersion");
                int cost=RequiredInt(grid,"maxCostPerRoad");
                for(int ix=0;ix<=columns;ix++)roads.Add(Segment(road,roadVersion,ox+ix*width,oz,ox+ix*width,oz+rows*depth,cost));
                for(int iz=0;iz<=rows;iz++)roads.Add(Segment(road,roadVersion,ox,oz+iz*depth,ox+columns*width,oz+iz*depth,cost));
                for(int ix=0;ix<columns;ix++)for(int iz=0;iz<rows;iz++)zones.Add(new JObject{["prefabIndex"]=zone,["prefabVersion"]=zoneVersion,["start"]=new JObject{["x"]=ox+ix*width+12,["z"]=oz+iz*depth+12},["end"]=new JObject{["x"]=ox+(ix+1)*width-12,["z"]=oz+(iz+1)*depth-12}});
            }
            foreach(var group in new[]{new{command="build_road",items=roads},new{command="build_network",items=networks},new{command="place_building",items=buildings},new{command="zone_rectangle",items=zones}})
                foreach(var item in group.items) { if(!(item is JObject))throw new ArgumentException("plan_items_must_be_objects"); steps.Add(new JObject{["command"]=group.command,["args"]=item.DeepClone()}); }
            if(steps.Count<1 || steps.Count>62)throw new ArgumentException("neighborhood_requires_1_to_62_steps");
            long upper=0,knownBuildingCost=0; var shapes=new List<Tuple<int,double[][]>>();
            var existing=NativeBuild.Buildings(em).Where(e=>em.HasComponent<Game.Objects.Transform>(e)&&!em.HasComponent<PrefabData>(e)).Select(e=>Footprint(w,e)).ToArray();
            for(int i=0;i<steps.Count;i++)
            {
                var step=(JObject)steps[i];var a=(JObject)step["args"];string command=(string)step["command"];
                try
                {
                    if(command=="zone_rectangle") { BuildPrefab<ZonePrefab>(w,a);var p=BuildPoint(w,(JObject)a["start"]);var q=BuildPoint(w,(JObject)a["end"]);if(math.distance(p.m_Position,q.m_Position)>500)throw new ArgumentException("zoning_diagonal_exceeds_500");continue; }
                    int max=RequiredInt(a,"maxCost");if(max<0 || max>1000000)throw new ArgumentException("invalid_step_budget");upper=checked(upper+max);
                    double[][] shape=null;
                    if(command=="place_building")
                    {
                        var prefab=BuildPrefab<BuildingPrefab>(w,a);var pe=w.GetExistingSystemManaged<PrefabSystem>().GetEntity(prefab);var p=BuildPoint(w,(JObject)a["position"]);
                        if(em.HasComponent<PlaceableObjectData>(pe)) {long cost=em.GetComponentData<PlaceableObjectData>(pe).m_ConstructionCost;knownBuildingCost+=cost;if(cost>max)throw new ArgumentException("building_cost_exceeds_step_budget");}
                        if(em.HasComponent<BuildingData>(pe))shape=Box(p.m_Position,quaternion.RotateY(math.radians((float?)a["rotation"]??0)),(float2)em.GetComponentData<BuildingData>(pe).m_LotSize*4);
                        if(a["candidates"]!=null)warnings.Add(new JObject{["step"]=i,["reason"]="only_primary_position_is_in_geometric_plan_preview"});
                    }
                    else
                    {
                        var prefab=BuildPrefab<NetPrefab>(w,a);var pe=w.GetExistingSystemManaged<PrefabSystem>().GetEntity(prefab);var p=BuildPoint(w,(JObject)a["start"]);var q=BuildPoint(w,(JObject)a["end"]);
                        float length=math.distance(p.m_Position,q.m_Position);if(length<8||length>1000)throw new ArgumentException("network_length_must_be_8_to_1000");
                        if(command=="build_road" && ((float?)a["elevation"]??0)==0)
                        {
                            float half=em.HasComponent<NetGeometryData>(pe)?em.GetComponentData<NetGeometryData>(pe).m_DefaultWidth*0.5f:8;
                            shape=PlanGeometry.Corridor(p.m_Position.x,p.m_Position.z,q.m_Position.x,q.m_Position.z,half);
                            if(a["control"]!=null)warnings.Add(new JObject{["step"]=i,["reason"]="curved_road_collision_preview_uses_chord_native_check_required"});
                        }
                    }
                    if(shape!=null)
                    {
                        foreach(var building in existing)
                        {
                            var polygon=((JArray)building["polygon"]).Select(p=>new[]{(double)p["x"],(double)p["z"]}).ToArray();
                            if(PlanGeometry.Overlap(shape,polygon))errors.Add(new JObject{["step"]=i,["reason"]="existing_building_overlap",["building"]=building.DeepClone()});
                        }
                        foreach(var other in shapes)if((command=="place_building" || (string)steps[other.Item1]["command"]=="place_building") && PlanGeometry.Overlap(shape,other.Item2))errors.Add(new JObject{["step"]=i,["otherStep"]=other.Item1,["reason"]="planned_building_overlap"});
                        shapes.Add(Tuple.Create(i,shape));
                    }
                }
                catch(Exception e){errors.Add(new JObject{["step"]=i,["reason"]=e.GetBaseException().Message});}
            }
            if(upper>ceiling)errors.Add(new JObject{["reason"]="step_spending_limits_exceed_plan_ceiling",["upperBound"]=upper});
            if((long)CityState()["money"]-upper<reserve)errors.Add(new JObject{["reason"]="insufficient_cash_for_spending_limits_and_reserve"});
            neighborhoodExpires=DateTime.UtcNow.AddMinutes(5);
            neighborhoodPlan=new JObject{["id"]=Guid.NewGuid().ToString("N"),["citySession"]=citySession,["createdFrame"]=CityState()["simulationFrame"],["expiresUtc"]=neighborhoodExpires.ToString("O"),["status"]=errors.Count==0?"ready":"blocked",["maxTotalCost"]=ceiling,["reserve"]=reserve,["spendingUpperBound"]=upper,["knownBuildingBaseCost"]=knownBuildingCost,["steps"]=steps,["errors"]=errors,["warnings"]=warnings,["validation"]="Static geometry and budget preflight. Native checks still run for every step; this is not an atomic transaction or a guarantee of placement.", ["request"]=args.DeepClone()};
            return (JObject)neighborhoodPlan.DeepClone();
        }
        private static JObject Segment(int prefab,int version,float ax,float az,float bx,float bz,int max)
            =>new JObject{["prefabIndex"]=prefab,["prefabVersion"]=version,["start"]=new JObject{["x"]=ax,["z"]=az},["end"]=new JObject{["x"]=bx,["z"]=bz},["maxCost"]=max};
        private JObject ExecuteNeighborhood(JObject args)
        {
            RequireControl();PauseAnalysis();var saved=NeighborhoodStatus(args);
            if((bool)saved["expired"] || (string)saved["citySession"]!=citySession)throw new InvalidOperationException("plan_stale_replan_required");
            if(saved["batchId"]!=null)throw new InvalidOperationException("plan_already_submitted");
            string id=(string)saved["id"];
            var fresh=PlanNeighborhood((JObject)saved["request"]); neighborhoodPlan["id"]=id;
            if((string)fresh["status"]!="ready")return (JObject)neighborhoodPlan.DeepClone();
            var steps=new JArray(new JObject{["command"]="save_checkpoint",["args"]=new JObject{["label"]="before-neighborhood"}});
            foreach(var s in (JArray)fresh["steps"])steps.Add(s.DeepClone());
            steps.Add(new JObject{["command"]="save_checkpoint",["args"]=new JObject{["label"]="after-neighborhood"}});
            var execution=StartBatch(new JObject{["steps"]=steps,["reserve"]=fresh["reserve"]});
            neighborhoodPlan["status"]="submitted";neighborhoodPlan["batchId"]=execution["id"];
            return new JObject{["planId"]=id,["batch"]=execution};
        }
    }
}
