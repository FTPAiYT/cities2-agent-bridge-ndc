using System;
using System.Collections.Generic;
using System.Linq;
using Game.Areas;
using Game.Citizens;
using Game.Common;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;

namespace CitiesIIAgentBridge
{
    public sealed partial class Mod
    {
        private JObject districtAtlas;
        private static bool AtlasLive(EntityManager em, Entity e) => e != Entity.Null && em.Exists(e) &&
            !em.HasComponent<Deleted>(e) && !em.HasComponent<Game.Tools.Temp>(e);
        private static string AtlasKey(Entity e) => e == Entity.Null ? "unassigned" : e.Index + ":" + e.Version;
        private Entity[] AtlasEntities(EntityManager em, params ComponentType[] all)
        {
            using (var q = em.CreateEntityQuery(new EntityQueryDesc { All = all, None = new[] {
                ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<PrefabData>() } }))
            using (var es = q.ToEntityArray(Allocator.Temp)) return es.ToArray().OrderBy(e => e.Index).ToArray();
        }
        private static JArray AtlasAssignments(EntityManager em, Entity e)
        {
            if (!em.HasBuffer<ServiceDistrict>(e)) return null;
            var rows = new JArray();
            foreach (var district in em.GetBuffer<ServiceDistrict>(e, true)) rows.Add(NativeBuild.Id(district.m_District));
            return rows;
        }
        private JObject DistrictAtlasPage(JObject args)
        {
            string layer = (string)args["layer"] ?? "districts";
            if (!new[] { "districts", "buildings", "roads" }.Contains(layer)) throw new ArgumentException("invalid_atlas_layer");
            var page = new QueryPage(args, 256, 1024);
            string id = (string)args["snapshotId"];
            if (id == null)
            {
                if (page.Offset != 0) throw new ArgumentException("snapshot_id_required_for_continuation");
                districtAtlas = CaptureDistrictAtlas();
            }
            else if (districtAtlas == null || (string)districtAtlas["snapshotId"] != id || (string)districtAtlas["citySession"] != citySession)
                throw new InvalidOperationException("atlas_snapshot_expired_start_new_export");
            var all = (JArray)districtAtlas[layer];
            var result = page.Result("items", new JArray(all.Skip(page.Offset).Take(page.Limit)), all.Count);
            foreach (var property in districtAtlas.Properties().Where(p => !new[] { "districts", "buildings", "roads" }.Contains(p.Name)))
                result[property.Name] = property.Value.DeepClone();
            result["layer"] = layer;
            result["simulationFrameChangedSinceCapture"] = !JToken.DeepEquals(districtAtlas["simulationFrame"], CityState()["simulationFrame"]);
            return result;
        }
        private JObject CaptureDistrictAtlas()
        {
            var w = RequireCity(); var em = w.EntityManager;
            if (ConstructionAccess.Active != null || (string)batch?["status"] == "running")
                throw new InvalidOperationException("wait_for_construction_before_atlas_capture");
            em.CompleteAllTrackedJobs();
            var state = CityState();
            var names = w.GetExistingSystemManaged<Game.UI.NameSystem>();
            var prefabs = w.GetExistingSystemManaged<PrefabSystem>();
            var districts = new JArray(); var buildings = new JArray(); var roads = new JArray();
            var census = new Dictionary<string, DistrictCensus>();
            var districtIds = new HashSet<Entity>(AtlasEntities(em, ComponentType.ReadOnly<District>()));
            var errors = new JArray();
            foreach (var e in districtIds.OrderBy(e => e.Index))
            {
                var row = NativeBuild.Id(e); row["key"] = AtlasKey(e); row["name"] = names.GetRenderedLabelName(e);
                var polygon = new JArray();
                if (em.HasBuffer<Game.Areas.Node>(e)) foreach (var node in em.GetBuffer<Game.Areas.Node>(e, true)) polygon.Add(Vector(node.m_Position));
                if (polygon.Count < 3) errors.Add("district_missing_polygon:" + AtlasKey(e));
                row["polygon"] = polygon;
                if (em.HasComponent<Game.Areas.Geometry>(e)) row["areaSquareMetres"] = em.GetComponentData<Game.Areas.Geometry>(e).m_SurfaceArea;
                districts.Add(row); census[AtlasKey(e)] = new DistrictCensus();
            }
            districts.Add(new JObject { ["key"] = "unassigned", ["name"] = "Outside districts", ["polygon"] = new JArray() });
            districts.Add(new JObject { ["key"] = "unlocated", ["name"] = "Home location unavailable", ["polygon"] = new JArray() });
            census["unassigned"] = new DistrictCensus(); census["unlocated"] = new DistrictCensus();
            string DistrictOf(Entity e)
            {
                if (!AtlasLive(em, e)) return "unlocated";
                if (!em.HasComponent<CurrentDistrict>(e)) return "unlocated";
                var district = em.GetComponentData<CurrentDistrict>(e).m_District;
                return district == Entity.Null ? "unassigned" : districtIds.Contains(district) ? AtlasKey(district) : "unlocated";
            }
            int visitors = 0;
            foreach (var e in AtlasEntities(em, ComponentType.ReadOnly<Citizen>()))
            {
                var citizen = em.GetComponentData<Citizen>(e);
                if ((citizen.m_State & (CitizenFlags.Tourist | CitizenFlags.Commuter)) != 0) { visitors++; continue; }
                Entity home = Entity.Null; bool homeless = (citizen.m_State & CitizenFlags.Homeless) != 0;
                if (em.HasComponent<HouseholdMember>(e))
                {
                    var household = em.GetComponentData<HouseholdMember>(e).m_Household;
                    if (AtlasLive(em, household))
                    {
                        if (em.HasComponent<Game.Buildings.PropertyRenter>(household)) home = em.GetComponentData<Game.Buildings.PropertyRenter>(household).m_Property;
                        if (em.HasComponent<HomelessHousehold>(household)) { homeless = true; home = em.GetComponentData<HomelessHousehold>(household).m_TempHome; }
                    }
                }
                var health = em.HasComponent<HealthProblem>(e) ? em.GetComponentData<HealthProblem>(e).m_Flags : HealthProblemFlags.None;
                int? student = em.HasComponent<Game.Citizens.Student>(e) ? (int?)em.GetComponentData<Game.Citizens.Student>(e).m_Level : null;
                census[DistrictOf(home)].Add((int)citizen.GetAge(), citizen.GetEducationLevel(), student, homeless,
                    (health & HealthProblemFlags.Sick) != 0, (health & HealthProblemFlags.Injured) != 0, (health & HealthProblemFlags.Dead) != 0);
            }
            foreach (JObject district in districts) district["census"] = census[(string)district["key"]].Json();
            foreach (var e in AtlasEntities(em, ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.ReadOnly<PrefabRef>()))
            {
                var row = Footprint(w, e); var pe = em.GetComponentData<PrefabRef>(e).m_Prefab;
                row["key"] = AtlasKey(e); row["name"] = names.GetRenderedLabelName(e); row["districtKey"] = DistrictOf(e);
                row["buildingFlags"] = em.GetComponentData<Game.Buildings.Building>(e).m_Flags.ToString();
                row["serviceDistricts"] = AtlasAssignments(em, e);
                row["assignmentSupported"] = em.HasBuffer<ServiceDistrict>(e);
                var kinds = new JArray(); var service = new JObject();
                bool upgrade = em.HasComponent<ServiceUpgradeData>(pe);
                row["isServiceUpgrade"] = upgrade;
                if (!upgrade && em.HasComponent<Game.Buildings.School>(e))
                {
                    kinds.Add("school");
                    if (UpgradeUtils.TryGetCombinedComponent<SchoolData>(em, e, pe, out var data))
                        service["school"] = new JObject { ["educationLevel"] = data.m_EducationLevel, ["nominalCapacity"] = data.m_StudentCapacity,
                            ["enrolled"] = em.HasBuffer<Game.Buildings.Student>(e) ? (JToken)em.GetBuffer<Game.Buildings.Student>(e, true).Length : JValue.CreateNull() };
                }
                if (!upgrade && em.HasComponent<Game.Buildings.Hospital>(e))
                {
                    kinds.Add("healthcare");
                    if (UpgradeUtils.TryGetCombinedComponent<HospitalData>(em, e, pe, out var data)) service["healthcare"] = RawFields(data);
                }
                if (!upgrade && em.HasComponent<Game.Buildings.FireStation>(e))
                {
                    kinds.Add("fire");
                    if (UpgradeUtils.TryGetCombinedComponent<FireStationData>(em, e, pe, out var data)) service["fire"] = RawFields(data);
                }
                if (!upgrade && em.HasComponent<Game.Buildings.PoliceStation>(e))
                {
                    kinds.Add("police");
                    if (UpgradeUtils.TryGetCombinedComponent<PoliceStationData>(em, e, pe, out var data)) service["police"] = RawFields(data);
                }
                if (!upgrade && em.HasComponent<Game.Buildings.DeathcareFacility>(e))
                {
                    kinds.Add("deathcare");
                    if (UpgradeUtils.TryGetCombinedComponent<DeathcareFacilityData>(em, e, pe, out var data)) service["deathcare"] = RawFields(data);
                }
                if (!upgrade && em.HasComponent<Game.Buildings.GarbageFacility>(e))
                {
                    kinds.Add("garbage");
                    if (UpgradeUtils.TryGetCombinedComponent<GarbageFacilityData>(em, e, pe, out var data)) service["garbage"] = RawFields(data);
                }
                row["serviceKinds"] = kinds; row["serviceCapacity"] = service;
                if (kinds.Count > 0) {
                    row["serviceStateRaw"] = Details(w, e);
                    row["patientBufferCount"] = em.HasBuffer<Game.Buildings.Patient>(e)
                        ? (JToken)em.GetBuffer<Game.Buildings.Patient>(e, true).Length : JValue.CreateNull();
                    row["occupantBufferCount"] = em.HasBuffer<Game.Buildings.Occupant>(e)
                        ? (JToken)em.GetBuffer<Game.Buildings.Occupant>(e, true).Length : JValue.CreateNull();
                    row["efficiency"] = em.HasBuffer<Game.Buildings.Efficiency>(e)
                        ? (JToken)Game.Buildings.BuildingUtils.GetEfficiency(em.GetBuffer<Game.Buildings.Efficiency>(e, true)) : JValue.CreateNull();
                }
                buildings.Add(row);
            }
            foreach (var e in AtlasEntities(em, ComponentType.ReadOnly<Game.Net.Road>(), ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.ReadOnly<Game.Net.Curve>(), ComponentType.ReadOnly<PrefabRef>()))
            {
                if (em.HasComponent<Owner>(e)) continue;
                var curve = em.GetComponentData<Game.Net.Curve>(e); var edge = em.GetComponentData<Game.Net.Edge>(e);
                var row = NativeBuild.Id(e); row["name"] = names.GetRenderedLabelName(e);
                row["prefab"] = prefabs.GetPrefabName(em.GetComponentData<PrefabRef>(e).m_Prefab);
                row["startNode"] = NativeBuild.Id(edge.m_Start); row["endNode"] = NativeBuild.Id(edge.m_End);
                row["length"] = curve.m_Length;
                row["curve"] = new JArray(Vector(curve.m_Bezier.a), Vector(curve.m_Bezier.b), Vector(curve.m_Bezier.c), Vector(curve.m_Bezier.d));
                roads.Add(row);
            }
            return new JObject {
                ["schemaVersion"] = 1, ["snapshotId"] = Guid.NewGuid().ToString("N"), ["citySession"] = citySession,
                ["capturedUtc"] = DateTime.UtcNow.ToString("O"), ["simulationFrame"] = state["simulationFrame"],
                ["cityName"] = state["cityName"], ["cityPopulationReported"] = state["population"], ["paused"] = true,
                ["coordinateSystem"] = "local game-world metres; x,z horizontal; not longitude/latitude",
                ["complete"] = errors.Count == 0, ["errors"] = errors, ["touristsAndCommutersExcluded"] = visitors,
                ["notes"] = new JArray("Census counts non-visitor citizen entities by household home, with homeless temporary homes when available. Dead residents excluded. Unlocated residents remain explicit; totals may differ from the HUD.",
                    "Native age bands are Child, Teen, Adult, Elderly; no invented young-adult threshold.",
                    "School enrollment is observed, not a count of all eligible applicants. Nominal capacities include active upgrades, not staffing/budget efficiency.",
                    "Null serviceDistricts means unsupported; an empty list means citywide. Multiple districts share a building's capacity; never count the whole capacity for each district.",
                    "Building footprints are zoning-lot rectangles. Roads preserve cubic curves. No vehicle travel-time, passive coverage or exact additional-building requirement is inferred."),
                ["districts"] = districts, ["buildings"] = buildings, ["roads"] = roads
            };
        }
        private JObject SetServiceDistricts(JObject args)
        {
            RequireControl(); var w = RequireCity(); CheckBuildTool(w); var em = w.EntityManager;
            if (ConstructionAccess.Active != null || (string)batch?["status"] == "running")
                throw new InvalidOperationException("wait_for_construction_before_district_assignment");
            em.CompleteAllTrackedJobs();
            var e = new Entity { Index = RequiredInt(args, "index"), Version = RequiredInt(args, "version") };
            if (!AtlasLive(em, e) || !em.HasComponent<Game.Buildings.Building>(e) || !em.HasBuffer<ServiceDistrict>(e))
                throw new ArgumentException("live_building_with_service_district_buffer_required");
            var desired = args["districts"] as JArray; DistrictAssignment.Keys(desired);
            var before = AtlasAssignments(em, e); DistrictAssignment.CheckExpected(before, args["expectedDistricts"]);
            foreach (JObject row in desired)
            {
                var d = new Entity { Index = (int)row["index"], Version = (int)row["version"] };
                if (!AtlasLive(em, d) || !em.HasComponent<District>(d)) throw new ArgumentException("live_district_required");
            }
            bool apply = (bool?)args["apply"] == true;
            if (apply)
            {
                var buffer = em.GetBuffer<ServiceDistrict>(e); buffer.Clear();
                foreach (JObject row in desired) buffer.Add(new ServiceDistrict(new Entity { Index = (int)row["index"], Version = (int)row["version"] }));
                // Matches SelectionToolSystem.UpdateServiceDistrictsJob's notification.
                if (!em.HasComponent<Updated>(e)) em.AddComponent<Updated>(e);
                districtAtlas = null;
                w.GetExistingSystemManaged<Game.UI.InGame.SelectedInfoUISystem>()?.RequestUpdate();
            }
            var after = AtlasAssignments(em, e);
            if (apply) DistrictAssignment.CheckExpected(after, desired);
            return new JObject { ["building"] = NativeBuild.Id(e), ["before"] = before, ["requested"] = desired,
                ["after"] = after, ["applied"] = apply, ["verified"] = apply,
                ["verification"] = apply ? "native_buffer_readback; simulation_service_effects_not_verified" : "preview_only" };
        }
    }
}
