using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.City;
using Game.Simulation;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.Zones;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Colossal.Collections;

namespace CitiesIIAgentBridge
{
    internal static class ConstructionAccess
    {
        internal static Func<bool> Allowed;
        internal static Dictionary<string, JObject> Results = new Dictionary<string, JObject>();
        internal static string Active;
        internal static string Begin(string kind)
        {
            if (Active != null) throw new InvalidOperationException("construction_busy");
            string id = Guid.NewGuid().ToString("N");
            Results[id] = new JObject { ["id"] = id, ["kind"] = kind, ["status"] = "queued" };
            Active = id;
            return id;
        }
        internal static JObject Status(string id) => id != null && Results.TryGetValue(id, out var r) ? (JObject)r.DeepClone() : throw new ArgumentException("operation_not_found");
        internal static void Finish(string id, string status, string error = null)
        {
            Results[id]["status"] = status;
            if (error != null) Results[id]["error"] = error;
            if(Active == id) Active = null;
        }
        internal static object Call(Type type, object target, string name, params object[] args)
        {
            var m = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, Array.ConvertAll(args, a => a.GetType()), null);
            if (m == null) throw new MissingMethodException(type.FullName, name);
            try { return m.Invoke(target, args); } catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }
        internal static void Field(Type type, object target, string name, object value) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        internal static T Read<T>(Type type, object target, string name) => (T)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        internal static JArray Errors(EntityManager em)
        {
            var result = new JArray();
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<Game.Tools.Error>()))
            using (var entities = q.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities)
                {
                    var row = NativeBuild.Id(entity);
                    if(em.HasComponent<PrefabRef>(entity))row["prefab"]=NativeBuild.Id(em.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if(em.HasComponent<Temp>(entity)){var t=em.GetComponentData<Temp>(entity);row["original"]=NativeBuild.Id(t.m_Original);row["flags"]=t.m_Flags.ToString();}
                    if(em.HasComponent<Game.Objects.Transform>(entity)){var p=em.GetComponentData<Game.Objects.Transform>(entity).m_Position;row["position"]=new JObject{["x"]=p.x,["y"]=p.y,["z"]=p.z};}
                    result.Add(row);
                }
            return result;
        }
        internal static HashSet<Entity> Roads(EntityManager em)
        {
            var result = new HashSet<Entity>();
            using (var q = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Game.Net.Edge>() }, None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() } }))
            using (var es = q.ToEntityArray(Allocator.Temp)) foreach (var e in es) result.Add(e);
            return result;
        }
    }

    // Inherit the game's generated network tool infrastructure; only the input sequence changes.
    public class BridgeRoadTool : NetToolSystem
    {
        public override string toolID => "CitiesIIAgentRoad";
        private string operation;
        private int stage;
        private int frames;
        private ControlPoint start, end;
        internal ControlPoint[] ExtraPoints; internal Mode RequestedMode = Mode.Straight;
        internal float RequestedElevation;
        private HashSet<Entity> before;
        private int maxCost;
        internal Entity Replacement;
        private Entity expectedPrefab;
        private DateTime deadline;
        public void Begin(NetPrefab road, ControlPoint a, ControlPoint b, int budget)
        {
            operation = ConstructionAccess.Begin("road");
            start = a; end = b; maxCost = budget; stage = 0; frames = 0;
            deadline = DateTime.UtcNow.AddSeconds(20);
            before = ConstructionAccess.Roads(EntityManager);
            Replacement = Entity.Null;
            expectedPrefab = World.GetExistingSystemManaged<PrefabSystem>().GetEntity(road);
            prefab = road; mode = RequestedMode; elevation = RequestedElevation; parallelCount = 0;
            World.GetExistingSystemManaged<ToolSystem>().activeTool = this;
        }
        protected override void OnStopRunning()
        {
            if (operation != null) { ConstructionAccess.Finish(operation, "interrupted", "active_tool_changed"); operation = null; }
            base.OnStopRunning();
        }
        protected override JobHandle OnUpdate(JobHandle deps)
        {
            if (operation == null) return deps;
            try
            {
                deps.Complete();
                if (ConstructionAccess.Allowed?.Invoke() != true || DateTime.UtcNow > deadline) throw new InvalidOperationException("construction_stopped_or_timed_out");
                if (stage == 0)
                {
                    var points = GetControlPoints(out var pointsDeps); pointsDeps.Complete(); points.Clear(); points.Add(start); if (ExtraPoints != null) foreach (var p in ExtraPoints) points.Add(p); points.Add(end);
                    ConstructionAccess.Field(typeof(NetToolSystem), this, "m_Prefab", prefab);
                    applyMode = ApplyMode.Clear;
                    deps = (JobHandle)ConstructionAccess.Call(typeof(NetToolSystem), this, "SnapControlPoints", deps, false);
                    deps = (JobHandle)ConstructionAccess.Call(typeof(NetToolSystem), this, "UpdateCourse", deps, false);
                    ConstructionAccess.Results[operation]["status"] = "validating"; stage = 1; return deps;
                }
                if (stage == 1)
                {
                    applyMode = ApplyMode.None;
                    if (++frames < 4) return deps;
                    var errors = ConstructionAccess.Errors(EntityManager);
                    if (errors.Count > 0) { ConstructionAccess.Results[operation]["placementErrors"] = errors; throw new InvalidOperationException("game_rejected_placement"); }
                    if (!GetAllowApply()) { if (frames < 30) return deps; throw new InvalidOperationException("no_valid_road_preview"); }
                    int cost = 0;
                    using (var q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>()))
                    using (var ts = q.ToComponentDataArray<Temp>(Allocator.Temp)) foreach (var t in ts) cost = checked(cost + Math.Max(0, t.m_Cost));
                    ConstructionAccess.Results[operation]["previewCost"] = cost;
                    if (cost > maxCost || cost > World.GetExistingSystemManaged<CitySystem>().moneyAmount) throw new InvalidOperationException("cost_exceeds_budget");
                    applyMode = ApplyMode.Apply; stage = 2; frames = 0;
                    ConstructionAccess.Results[operation]["status"] = "applying"; return deps;
                }
                if (stage == 2)
                {
                    applyMode = ApplyMode.Clear;
                    if (++frames < 4) return deps;
                    var created = new JArray();
                    foreach (var entity in ConstructionAccess.Roads(EntityManager))
                        if (!before.Contains(entity) && EntityManager.HasComponent<PrefabRef>(entity) && EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab == expectedPrefab) created.Add(new JObject { ["index"] = entity.Index, ["version"] = entity.Version });
                    ConstructionAccess.Results[operation]["createdRoads"] = created;
                    bool replaced = Replacement != Entity.Null && EntityManager.Exists(Replacement) && EntityManager.HasComponent<PrefabRef>(Replacement) && EntityManager.GetComponentData<PrefabRef>(Replacement).m_Prefab == expectedPrefab;
                    if (replaced) ConstructionAccess.Results[operation]["upgradedEntity"] = NativeBuild.Id(Replacement);
                    if (created.Count == 0 && !replaced) throw new InvalidOperationException("network_change_not_observed");
                    Finish("complete");
                }
            }
            catch (Exception e) { Finish("failed", e.Message); }
            return deps;
        }
        private void Finish(string status, string error = null)
        {
            applyMode = ApplyMode.Clear;
            ConstructionAccess.Finish(operation, status, error); operation = null;
            World.GetExistingSystemManaged<ToolSystem>().activeTool = World.GetExistingSystemManaged<DefaultToolSystem>();
        }
    }

    public class BridgeZoneTool : ZoneToolSystem
    {
        internal bool Dezone;
        public override string toolID => "CitiesIIAgentZone";
        private string operation;
        private int stage, frames;
        private ControlPoint start, end;
        
        private DateTime deadline;
        private Dictionary<string, ushort> before;
        public void Begin(ZonePrefab zone, ControlPoint a, ControlPoint b)
        {
            operation = ConstructionAccess.Begin("zoning"); stage = 0; frames = 0; start = a; end = b;
            deadline = DateTime.UtcNow.AddSeconds(20);
            prefab = zone; mode = Mode.Marquee; overwrite = false;
            before = Cells();
            World.GetExistingSystemManaged<ToolSystem>().activeTool = this;
        }
        protected override void OnStopRunning()
        {
            if (operation != null) { ConstructionAccess.Finish(operation, "interrupted", "active_tool_changed"); operation = null; }
            base.OnStopRunning();
        }
        private Dictionary<string, ushort> Cells()
        {
            var result = new Dictionary<string, ushort>();
            using (var q = EntityManager.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Block>(), ComponentType.ReadOnly<Cell>() }, None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() } }))
            using (var es = q.ToEntityArray(Allocator.Temp))
                foreach (var e in es) { var cells = EntityManager.GetBuffer<Cell>(e, true); for (int i = 0; i < cells.Length; i++) result[e.Index + ":" + e.Version + ":" + i] = cells[i].m_Zone.m_Index; }
            return result;
        }
        protected override JobHandle OnUpdate(JobHandle deps)
        {
            if (operation == null) return deps;
            try
            {
                deps.Complete();
                if (ConstructionAccess.Allowed?.Invoke() != true || DateTime.UtcNow > deadline) throw new InvalidOperationException("construction_stopped_or_timed_out");
                if (stage == 0)
                {
                    ConstructionAccess.Field(typeof(ZoneToolSystem), this, "m_RaycastPoint", end);
                    ConstructionAccess.Field(typeof(ZoneToolSystem), this, "m_StartPoint", start);
                    var stateField = typeof(ZoneToolSystem).GetField("m_State", BindingFlags.Instance | BindingFlags.NonPublic);
                    stateField.SetValue(this, Enum.Parse(stateField.FieldType, Dezone ? "Dezoning" : "Zoning"));
                    var snap = ConstructionAccess.Read<NativeValue<ControlPoint>>(typeof(ZoneToolSystem), this, "m_SnapPoint"); snap.value = end;
                    applyMode = ApplyMode.Clear;
                    deps = (JobHandle)ConstructionAccess.Call(typeof(ZoneToolSystem), this, "UpdateDefinitions", deps);
                    stage = 1; ConstructionAccess.Results[operation]["status"] = "validating"; return deps;
                }
                if (stage == 1)
                {
                    applyMode = ApplyMode.None; if (++frames < 4) return deps;
                    if (!GetAllowApply()) throw new InvalidOperationException("game_rejected_zoning");
                    using (var q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Block>(), ComponentType.ReadOnly<Temp>()))
                        if (q.IsEmptyIgnoreFilter) throw new InvalidOperationException("no_zone_cells_in_rectangle");
                    applyMode = ApplyMode.Apply; stage = 2; frames = 0; return deps;
                }
                applyMode = ApplyMode.Clear; if (++frames < 4) return deps;
                int changed = 0; foreach (var pair in Cells()) if (before.TryGetValue(pair.Key, out var old) && old != pair.Value) ++changed;
                ConstructionAccess.Results[operation]["changedCells"] = changed;
                if (changed == 0) throw new InvalidOperationException("no_zoning_change_observed");
                Finish("complete");
            }
            catch (Exception e) { Finish("failed", e.Message); }
            return deps;
        }
        private void Finish(string status, string error = null)
        {
            applyMode = ApplyMode.Clear; ConstructionAccess.Finish(operation, status, error); operation = null;
            World.GetExistingSystemManaged<ToolSystem>().activeTool = World.GetExistingSystemManaged<DefaultToolSystem>();
        }
    }
}



