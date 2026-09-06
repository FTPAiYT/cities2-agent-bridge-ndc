using System;
using System.Collections.Generic;
using System.Linq;
using Game.City;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitiesIIAgentBridge
{
    public sealed partial class Mod
    {
        private JObject PlaceBuilding(JObject args)
        {
            var w = RequireCity(); RequireControl(); CheckBuildTool(w);
            var prefab = BuildPrefab<BuildingPrefab>(w, args);
            var point = BuildPoint(w, args["position"] as JObject);
            float rotation = args["rotation"] == null ? 0 : RequiredFloat(args, "rotation");
            point.m_Rotation = quaternion.RotateY(math.radians(rotation));
            int budget = RequiredInt(args, "maxCost"); if (budget < 0 || budget > 1000000) throw new ArgumentException("invalid_budget");
            Entity move = Entity.Null;
            if (args["moveIndex"] != null) { move = new Entity { Index = RequiredInt(args, "moveIndex"), Version = RequiredInt(args, "moveVersion") }; if (!w.EntityManager.Exists(move) || !w.EntityManager.HasComponent<Game.Buildings.Building>(move)) throw new ArgumentException("invalid_building_to_move"); }
            ControlPoint[] candidates = null;
            if(args["candidates"] is JArray list)
            {
                if(list.Count<1 || list.Count>16) throw new ArgumentException("candidates_must_contain_1_to_16_positions");
                candidates=list.Cast<JObject>().Select(c=> {var cp=BuildPoint(w,c["position"] as JObject);cp.m_Rotation=quaternion.RotateY(math.radians(RequiredFloat(c,"rotation")));return cp;}).ToArray();
            }
            float snapDistance=args["maxSnapDistance"]==null?32:RequiredFloat(args,"maxSnapDistance");
            if(snapDistance<0 || snapDistance>128)throw new ArgumentException("maxSnapDistance_must_be_0_to_128");
            w.GetExistingSystemManaged<BridgeObjectTool>().Begin(prefab, point, budget, move,(bool?)args["previewOnly"]==true,candidates,(bool?)args["allowDemolition"]==true,snapDistance);
            return ConstructionAccess.Status(ConstructionAccess.Active);
        }
        private JObject Demolish(JObject args)
        {
            var w = RequireCity(); RequireControl(); CheckBuildTool(w); var em = w.EntityManager;
            var entity = new Entity { Index = RequiredInt(args, "index"), Version = RequiredInt(args, "version") };
            if (!em.Exists(entity) || (!em.HasComponent<Game.Buildings.Building>(entity) && !em.HasComponent<Game.Net.Edge>(entity))) throw new ArgumentException("target_must_be_building_or_network_edge");
            float3 pos;
            if (em.HasComponent<Game.Objects.Transform>(entity)) pos = em.GetComponentData<Game.Objects.Transform>(entity).m_Position;
            else pos = em.GetComponentData<Game.Net.Curve>(entity).m_Bezier.a;
            var point = new ControlPoint { m_OriginalEntity = entity, m_Position = pos, m_HitPosition = pos, m_Rotation = quaternion.identity };
            w.GetExistingSystemManaged<BridgeBulldozeTool>().Begin(entity, point);
            return ConstructionAccess.Status(ConstructionAccess.Active);
        }
        private JObject CityManagement()
        {
            var w = RequireCity(); var taxes = w.GetExistingSystemManaged<TaxSystem>(); var budget = w.GetExistingSystemManaged<CityServiceBudgetSystem>();
            var residential = w.GetExistingSystemManaged<ResidentialDemandSystem>(); var commercial = w.GetExistingSystemManaged<CommercialDemandSystem>(); var industrial = w.GetExistingSystemManaged<IndustrialDemandSystem>();
            var rates = new JObject(); foreach (TaxAreaType type in Enum.GetValues(typeof(TaxAreaType))) if (type != TaxAreaType.None) rates[type.ToString()] = taxes.GetTaxRate(type);
            int3 demand = residential.buildingDemand;
            var income = new JObject(); foreach (IncomeSource source in Enum.GetValues(typeof(IncomeSource))) if (source != IncomeSource.Count) income[source.ToString()] = budget.GetIncome(source);
            var expense = new JObject(); foreach (ExpenseSource source in Enum.GetValues(typeof(ExpenseSource))) if (source != ExpenseSource.Count) expense[source.ToString()] = budget.GetExpense(source);
            return new JObject {
                ["city"] = CityState(), ["taxes"] = rates,
                ["demand"] = new JObject { ["residential"] = new JArray(demand.x,demand.y,demand.z), ["commercial"] = commercial.buildingDemand, ["industrial"] = industrial.industrialBuildingDemand, ["office"] = industrial.officeBuildingDemand, ["storage"] = industrial.storageBuildingDemand },
                ["budget"] = new JObject { ["balanceRaw"] = budget.GetBalance(), ["incomeRaw"] = budget.GetTotalIncome(), ["expensesRaw"] = budget.GetTotalExpenses(), ["incomeBySourceRaw"] = income, ["expenseBySourceRaw"] = expense },
                ["households"] = w.GetExistingSystemManaged<BudgetSystem>().GetHouseholdCount()
            };
        }
        private JObject SetTax(JObject args)
        {
            var w = RequireCity(); RequireControl();
            if (!Enum.TryParse<TaxAreaType>((string)args["area"], true, out var area) || area == TaxAreaType.None || !Enum.IsDefined(typeof(TaxAreaType), area)) throw new ArgumentException("invalid_tax_area");
            int rate = RequiredInt(args, "rate"); if (rate < -10 || rate > 30) throw new ArgumentException("tax_rate_out_of_range");
            var system = w.GetExistingSystemManaged<TaxSystem>(); int before = system.GetTaxRate(area); system.SetTaxRate(area, rate);
            return new JObject { ["before"] = before, ["after"] = system.GetTaxRate(area) };
        }
        private JObject Services(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var system = w.GetExistingSystemManaged<CityServiceBudgetSystem>(); var ps = w.GetExistingSystemManaged<PrefabSystem>(); var rows = new JArray();
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>()))
            using (var es = q.ToEntityArray(Allocator.Temp)) foreach (var e in es)
            {
                if (!ps.TryGetPrefab<ServicePrefab>(e, out var p)) continue;
                var workers = system.GetWorkersAndWorkplaces(e);
                var buildings = system.GetServiceBuildings(e);
                rows.Add(new JObject { ["index"] = e.Index, ["version"] = e.Version, ["name"] = p.name, ["budget"] = system.GetServiceBudget(e), ["buildings"] = buildings == null ? new JArray() : new JArray(buildings.Select(NativeBuild.Id)), ["workers"] = workers.x, ["workplaces"] = workers.y });
            }
            return new JObject { ["services"] = rows };
        }
        private JObject SetServiceBudget(JObject args)
        {
            var w = RequireCity(); RequireControl(); var p = BuildPrefab<ServicePrefab>(w, args); var e = w.GetExistingSystemManaged<PrefabSystem>().GetEntity(p);
            int amount = RequiredInt(args, "budget"); if (amount < 50 || amount > 150) throw new ArgumentException("service_budget_must_be_50_to_150");
            var budget = w.GetExistingSystemManaged<CityServiceBudgetSystem>(); int before = budget.GetServiceBudget(e); budget.SetServiceBudget(e, amount);
            return new JObject { ["before"] = before, ["after"] = budget.GetServiceBudget(e) };
        }
        private JObject Terrain(JObject args)
        {
            var w = RequireCity(); var points = args["points"] as JArray;
            if (points == null || points.Count < 1 || points.Count > 1024) throw new ArgumentException("points_must_contain_1_to_1024_locations");
            var terrain = w.GetExistingSystemManaged<TerrainSystem>().GetHeightData();
            var water = w.GetExistingSystemManaged<WaterSystem>().GetVelocitiesSurfaceData(out var waterReady); waterReady.Complete();
            var pollution = w.GetExistingSystemManaged<GroundPollutionSystem>().GetMap(true, out var pollutionReady); pollutionReady.Complete();
            var air = w.GetExistingSystemManaged<AirPollutionSystem>().GetMap(true,out var airReady); airReady.Complete();
            var noise = w.GetExistingSystemManaged<NoisePollutionSystem>().GetMap(true,out var noiseReady); noiseReady.Complete();
            var rows = new JArray();
            foreach (JObject p in points)
            {
                var position = new float3(RequiredFloat(p, "x"), 0, RequiredFloat(p, "z"));
                if (math.any(math.abs(position.xz) > 14000)) throw new ArgumentException("point_out_of_bounds");
                position.y = TerrainUtils.SampleHeight(ref terrain, position, out var normal);
                var velocity = WaterUtils.SampleVelocity(ref water, position);
                rows.Add(new JObject { ["position"] = Vector(position), ["normal"] = Vector(normal), ["waterDepth"] = WaterUtils.SampleDepth(ref water, position), ["waterPollution"] = WaterUtils.SamplePolluted(ref water, position), ["waterVelocity"] = new JArray(velocity.x,velocity.y), ["groundPollutionRaw"] = GroundPollutionSystem.GetPollution(position,pollution).m_Pollution, ["airPollutionRaw"] = AirPollutionSystem.GetPollution(position,air).m_Pollution, ["noisePollutionRaw"] = NoisePollutionSystem.GetPollution(position,noise).m_Pollution });
            }
            return new JObject { ["samples"] = rows };
        }
        private JObject Tiles()
        {
            var w = RequireCity(); var em = w.EntityManager; var rows = new JArray();
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<Game.Areas.MapTile>()))
            using (var es = q.ToEntityArray(Allocator.Temp)) foreach (var e in es)
            {
                var polygon = new JArray(); if (em.HasBuffer<Game.Areas.Node>(e)) foreach (var n in em.GetBuffer<Game.Areas.Node>(e, true)) polygon.Add(Vector(n.m_Position));
                rows.Add(new JObject { ["index"] = e.Index, ["version"] = e.Version, ["purchased"] = !em.HasComponent<Game.Common.Native>(e), ["polygon"] = polygon });
            }
            return new JObject { ["tiles"] = rows, ["availablePurchases"] = w.GetExistingSystemManaged<MapTilePurchaseSystem>().GetAvailableTiles() };
        }
        private JObject Buildings(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var rows = new JArray(); string filter = (string)args["filter"] ?? "";
            foreach (var e in NativeBuild.Buildings(em))
            {
                var info = Inspect(w, e); if (((string)info["prefab"] ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                info["serviceDataRaw"] = Details(w,e);
                var b = em.GetComponentData<Game.Buildings.Building>(e); info["roadEdge"] = NativeBuild.Id(b.m_RoadEdge); info["buildingFlags"] = b.m_Flags.ToString();
                var issues = new JArray(); if (b.m_RoadEdge == Entity.Null || !em.Exists(b.m_RoadEdge)) issues.Add("no_road_connection");
                if (em.HasComponent<WaterPipeBuildingConnection>(e))
                {
                    var c = em.GetComponentData<WaterPipeBuildingConnection>(e);
                    info["waterConnections"] = new JObject { ["producer"] = NativeBuild.Id(c.m_ProducerEdge), ["consumer"] = NativeBuild.Id(c.m_ConsumerEdge) };
                    if (c.m_ProducerEdge == Entity.Null && c.m_ConsumerEdge == Entity.Null) issues.Add("no_water_network_connection");
                }
                if (em.HasComponent<ElectricityBuildingConnection>(e))
                {
                    var c = em.GetComponentData<ElectricityBuildingConnection>(e);
                    info["electricityConnections"] = new JObject { ["producer"] = NativeBuild.Id(c.m_ProducerEdge), ["consumer"] = NativeBuild.Id(c.m_ConsumerEdge), ["transformer"] = NativeBuild.Id(c.m_TransformerNode) };
                    if (c.m_ProducerEdge == Entity.Null && c.m_ConsumerEdge == Entity.Null && c.m_TransformerNode == Entity.Null) issues.Add("no_electricity_network_connection");
                }
                if (em.HasComponent<Game.Buildings.WaterConsumer>(e)) { var c = em.GetComponentData<Game.Buildings.WaterConsumer>(e); if (c.m_FulfilledFresh < c.m_WantedConsumption) issues.Add("fresh_water_shortfall"); if (c.m_FulfilledSewage < c.m_WantedConsumption) issues.Add("sewage_shortfall"); }
                if (em.HasComponent<Game.Buildings.ElectricityConsumer>(e)) { var c = em.GetComponentData<Game.Buildings.ElectricityConsumer>(e); if (c.m_FulfilledConsumption < c.m_WantedConsumption) issues.Add("electricity_shortfall"); }
                info["issues"] = issues; if ((bool?)args["problemsOnly"] == true && issues.Count == 0) continue;
                rows.Add(info); if (rows.Count >= 512) break;
            }
            return new JObject { ["buildings"] = rows, ["limit"] = 512 };
        }
    }
}



