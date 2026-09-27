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
            var w = RequireCity(); var em = w.EntityManager; var system = w.GetExistingSystemManaged<CityServiceBudgetSystem>(); var ps = w.GetExistingSystemManaged<PrefabSystem>(); var rows = new JArray(); var errors = new JArray();
            if (system == null || ps == null) throw new InvalidOperationException("service_system_not_ready");
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>()))
            using (var es = q.ToEntityArray(Allocator.Temp)) foreach (var e in es)
            {
                if (!ps.TryGetPrefab<PrefabBase>(e, out var candidate) || !(candidate is ServicePrefab p)) continue;
                try {
                var workers = system.GetWorkersAndWorkplaces(e);
                var buildings = system.GetServiceBuildings(e);
                rows.Add(new JObject { ["index"] = e.Index, ["version"] = e.Version, ["name"] = p.name, ["budget"] = system.GetServiceBudget(e), ["buildings"] = buildings == null ? new JArray() : new JArray(buildings.Select(NativeBuild.Id)), ["workers"] = workers.x, ["workplaces"] = workers.y });
                } catch (Exception error) {
                    log.Warn("Service read failed for " + p.name + ": " + error);
                    errors.Add(new JObject { ["index"] = e.Index, ["version"] = e.Version, ["name"] = p.name, ["error"] = error.GetType().Name + ": " + error.Message });
                }
            }
            return new JObject { ["services"] = rows, ["errors"] = errors, ["complete"] = errors.Count == 0, ["total"] = rows.Count + errors.Count };
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
            // The full-precision surface contains depth, pollution and velocity. The separate
            // downscaled flow reader is not a depth/pollution surface (and uses a half-float texture).
            var water = w.GetExistingSystemManaged<WaterSystem>().GetSurfaceData(out var waterReady); waterReady.Complete();
            var pollution = w.GetExistingSystemManaged<GroundPollutionSystem>().GetMap(true, out var pollutionReady); pollutionReady.Complete();
            var air = w.GetExistingSystemManaged<AirPollutionSystem>().GetMap(true,out var airReady); airReady.Complete();
            var noise = w.GetExistingSystemManaged<NoisePollutionSystem>().GetMap(true,out var noiseReady); noiseReady.Complete();
            var resourceSystem = w.GetExistingSystemManaged<NaturalResourceSystem>();
            var groundwaterSystem = w.GetExistingSystemManaged<GroundWaterSystem>();
            NativeArray<NaturalResourceCell> resources = default;
            NativeArray<GroundWater> groundwater = default;
            if (resourceSystem != null) { resources = resourceSystem.GetMap(true, out var ready); ready.Complete(); }
            if (groundwaterSystem != null) { groundwater = groundwaterSystem.GetMap(true, out var ready); ready.Complete(); }
            int resourceSize = NaturalResourceSystem.kTextureSize, groundwaterSize = GroundWaterSystem.kTextureSize;
            var rows = new JArray();
            foreach (JObject p in points)
            {
                var position = new float3(RequiredFloat(p, "x"), 0, RequiredFloat(p, "z"));
                if (math.any(math.abs(position.xz) > 14000)) throw new ArgumentException("point_out_of_bounds");
                position.y = TerrainUtils.SampleHeight(ref terrain, position, out var normal);
                var row = new JObject { ["position"] = Vector(position), ["normal"] = Vector(normal), ["groundPollutionRaw"] = GroundPollutionSystem.GetPollution(position,pollution).m_Pollution, ["airPollutionRaw"] = AirPollutionSystem.GetPollution(position,air).m_Pollution, ["noisePollutionRaw"] = NoisePollutionSystem.GetPollution(position,noise).m_Pollution };
                string waterStatus = !water.isCreated || !water.hasDepths || water.resolution.x < 2 || water.resolution.z < 2 || water.depths.Length < (long)water.resolution.x * water.resolution.z ? "unavailable" : "ok";
                if (waterStatus == "ok") {
                    var surfacePosition = WaterUtils.ToSurfaceSpace(ref water, position);
                    if (surfacePosition.x < 0 || surfacePosition.z < 0 || surfacePosition.x >= water.resolution.x || surfacePosition.z >= water.resolution.z) waterStatus = "out_of_bounds";
                }
                if (waterStatus == "ok") {
                    var velocity = WaterUtils.SampleVelocity(ref water, position);
                    TerrainSampleValues.Water(row, waterStatus, WaterUtils.SampleDepth(ref water, position), WaterUtils.SamplePolluted(ref water, position), velocity.x, velocity.y);
                } else TerrainSampleValues.Water(row, waterStatus);

                bool resourceInBounds = TerrainSampleValues.TryCellIndex(position.x, position.z, CellMapSystem<NaturalResourceCell>.kMapSize, resourceSize, out int resourceIndex);
                bool resourceReady = resources.IsCreated && resources.Length == (long)resourceSize * resourceSize;
                row["naturalResourcesStatus"] = !resourceInBounds ? "out_of_bounds" : !resourceReady ? "unavailable" : "ok";
                row["naturalResources"] = JValue.CreateNull();
                if (resourceInBounds && resourceReady) {
                    var cell = resources[resourceIndex];
                    row["naturalResources"] = new JObject { ["cellIndex"] = resourceIndex,
                        ["fertility"] = TerrainSampleValues.Resource(cell.m_Fertility.m_Base, cell.m_Fertility.m_Used),
                        ["ore"] = TerrainSampleValues.Resource(cell.m_Ore.m_Base, cell.m_Ore.m_Used),
                        ["oil"] = TerrainSampleValues.Resource(cell.m_Oil.m_Base, cell.m_Oil.m_Used),
                        ["fish"] = TerrainSampleValues.Resource(cell.m_Fish.m_Base, cell.m_Fish.m_Used) };
                }
                bool groundwaterInBounds = TerrainSampleValues.TryCellIndex(position.x, position.z, CellMapSystem<GroundWater>.kMapSize, groundwaterSize, out int groundwaterIndex);
                bool groundwaterReady = groundwater.IsCreated && groundwater.Length == (long)groundwaterSize * groundwaterSize;
                row["groundwaterStatus"] = !groundwaterInBounds ? "out_of_bounds" : !groundwaterReady ? "unavailable" : "ok";
                row["groundwater"] = JValue.CreateNull();
                if (groundwaterInBounds && groundwaterReady) {
                    var cell = groundwater[groundwaterIndex];
                    row["groundwater"] = TerrainSampleValues.Groundwater(cell.m_Amount, cell.m_Max, cell.m_Polluted);
                    row["groundwater"]["cellIndex"] = groundwaterIndex;
                    if (cell.m_Amount < 0 || cell.m_Max < 0 || cell.m_Polluted < 0 || cell.m_Polluted > cell.m_Amount) row["groundwaterStatus"] = "invalid";
                }
                rows.Add(row);
            }
            return new JObject { ["samples"] = rows, ["citySession"] = citySession,
                ["sampling"] = new JObject {
                    ["waterSource"] = "WaterSystem.GetSurfaceData", ["waterDepthUnits"] = "metres",
                    ["waterPollutionUnits"] = "native sampled value; not a percentage", ["waterVelocityUnits"] = "native world-space flow; not verified metres per second",
                    ["resourceSampling"] = "containing_cell", ["resourceUnits"] = "native raw amounts; not percentages or guaranteed production",
                    ["resourceCellSizeMetres"] = (double)CellMapSystem<NaturalResourceCell>.kMapSize / resourceSize,
                    ["resourceMapSizeMetres"] = CellMapSystem<NaturalResourceCell>.kMapSize,
                    ["groundwaterCellSizeMetres"] = (double)CellMapSystem<GroundWater>.kMapSize / groundwaterSize,
                    ["groundwaterMapSizeMetres"] = CellMapSystem<GroundWater>.kMapSize,
                    ["freshness"] = "Latest completed native CPU data; GPU water readback is asynchronous. No new simulation or GPU readback is forced."
                } };
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
            var page = new QueryPage(args, 512, 4096); int total = 0;
            bool spatial = args["radius"] != null || args["x"] != null || args["z"] != null;
            float2 center = spatial ? new float2(RequiredFloat(args,"x"), RequiredFloat(args,"z")) : default;
            float radius = spatial ? RequiredFloat(args,"radius") : 0;
            if (spatial && (radius <= 0 || radius > 14000)) throw new ArgumentException("radius_must_be_0_to_14000");
            foreach (var e in NativeBuild.Buildings(em).OrderBy(e=>e.Index).ThenBy(e=>e.Version))
            {
                if (spatial && (!em.HasComponent<Game.Objects.Transform>(e) || math.distance(em.GetComponentData<Game.Objects.Transform>(e).m_Position.xz,center)>radius)) continue;
                var info = Inspect(w, e); if (((string)info["prefab"] ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var b = em.GetComponentData<Game.Buildings.Building>(e); info["roadEdge"] = NativeBuild.Id(b.m_RoadEdge); info["buildingFlags"] = b.m_Flags.ToString();
                var issues = new JArray();
                Entity prefab = em.HasComponent<PrefabRef>(e) ? em.GetComponentData<PrefabRef>(e).m_Prefab : Entity.Null;
                bool requiresRoad = prefab != Entity.Null && em.HasComponent<BuildingData>(prefab) && (em.GetComponentData<BuildingData>(prefab).m_Flags & BuildingFlags.RequireRoad) != 0;
                info["requiresRoad"] = requiresRoad;
                if (requiresRoad && (b.m_RoadEdge == Entity.Null || !em.Exists(b.m_RoadEdge))) issues.Add("no_road_connection");
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
                if (page.Contains(total++)) { info["serviceDataRaw"] = Details(w,e); rows.Add(info); }
            }
            var result = page.Result("buildings", rows, total);
            result["citySession"] = citySession;
            return result;
        }
    }
}
