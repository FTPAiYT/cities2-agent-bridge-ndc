using System;
using System.Linq;
using Game.Prefabs;
using Game.Tools;
using Game.Zones;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitiesIIAgentBridge
{
    public sealed partial class Mod
    {
        // Locked is enableable: unlocked prefabs retain the component with its bit disabled.
        private static bool IsPrefabLocked(EntityManager em, Entity entity) =>
            em.HasComponent<Locked>(entity) && em.IsComponentEnabled<Locked>(entity);

        private JObject BuildPrefabs(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var ps = w.GetExistingSystemManaged<PrefabSystem>(); var rows = new JArray();
            string filter = (string)args["filter"] ?? "";
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>()))
            using (var es = q.ToEntityArray(Allocator.Temp)) foreach (var e in es)
            {
                if (!ps.TryGetPrefab<PrefabBase>(e, out var p) || (!(p is NetPrefab) && !(p is ZonePrefab) && !(p is BuildingPrefab) && !(p is ServicePrefab))) continue;
                if (p.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                rows.Add(new JObject { ["index"] = e.Index, ["version"] = e.Version, ["name"] = p.name, ["kind"] = p is ZonePrefab ? "zone" : p is BuildingPrefab ? "building" : p is ServicePrefab ? "service" : "network", ["locked"] = IsPrefabLocked(em, e) });
            }
            return new JObject { ["prefabs"] = rows };
        }
        private T BuildPrefab<T>(World w, JObject args) where T : PrefabBase
        {
            var em = w.EntityManager; var e = new Entity { Index = RequiredInt(args, "prefabIndex"), Version = RequiredInt(args, "prefabVersion") };
            if (!em.Exists(e) || IsPrefabLocked(em, e)) throw new ArgumentException("prefab_stale_or_locked");
            if (!w.GetExistingSystemManaged<PrefabSystem>().TryGetPrefab<T>(e, out var p)) throw new ArgumentException("incorrect_prefab_type");
            return p;
        }
        private ControlPoint BuildPoint(World w, JObject args)
        {
            if (args == null) throw new ArgumentException("point_required");
            float3 pos = new float3(RequiredFloat(args, "x"), args["y"] == null ? 0 : RequiredFloat(args, "y"), RequiredFloat(args, "z"));
            if (math.any(math.abs(pos.xz) > 14000) || pos.y < -1000 || pos.y > 5000) throw new ArgumentException("point_out_of_bounds");
            if (args["y"] == null) { var terrain = w.GetExistingSystemManaged<Game.Simulation.TerrainSystem>().GetHeightData(); pos.y = Game.Simulation.TerrainUtils.SampleHeight(ref terrain,pos); }
            Entity entity = Entity.Null;
            float curvePosition = 0;
            if (args["index"] != null)
            {
                entity = new Entity { Index = RequiredInt(args, "index"), Version = RequiredInt(args, "version") };
                if (!w.EntityManager.Exists(entity)) throw new ArgumentException("stale_point_entity");
                if (w.EntityManager.HasComponent<Game.Net.Node>(entity)) pos = w.EntityManager.GetComponentData<Game.Net.Node>(entity).m_Position;
                else if (w.EntityManager.HasComponent<Game.Net.Curve>(entity))
                {
                    curvePosition = RequiredFloat(args,"curvePosition"); if (curvePosition < 0 || curvePosition > 1) throw new ArgumentException("curvePosition_must_be_0_to_1");
                    var c = w.EntityManager.GetComponentData<Game.Net.Curve>(entity).m_Bezier;
                    float t = curvePosition, u = 1-t; pos = u*u*u*c.a + 3*u*u*t*c.b + 3*u*t*t*c.c + t*t*t*c.d;
                }
                else if (!w.EntityManager.HasComponent<Block>(entity)) throw new ArgumentException("point_entity_must_be_node_edge_or_zone_block");
            }
            return new ControlPoint { m_Position = pos, m_HitPosition = pos, m_OriginalEntity = entity, m_CurvePosition = curvePosition, m_Rotation = quaternion.identity };
        }
        private void CheckBuildTool(World w)
        {
            var tools = w.GetExistingSystemManaged<ToolSystem>();
            if (!(tools.activeTool is DefaultToolSystem)) throw new InvalidOperationException("finish_or_cancel_current_tool_first");
            if (tools.ignoreErrors) throw new InvalidOperationException("disable_ignore_errors_before_building");
        }
        private JObject StartRoad(JObject args)
        {
            var w = RequireCity(); CheckBuildTool(w);
            var p = BuildPrefab<NetPrefab>(w, args); var a = BuildPoint(w, args["start"] as JObject); var b = BuildPoint(w, args["end"] as JObject);
            float length = math.distance(a.m_Position, b.m_Position);
            if (length < 8 || length > 1000) throw new ArgumentException("road_length_must_be_8_to_1000_metres");
            int budget = RequiredInt(args, "maxCost"); if (budget < 1 || budget > 1000000) throw new ArgumentException("invalid_budget");
            var roadTool = w.GetExistingSystemManaged<BridgeRoadTool>();
            roadTool.ExtraPoints = null; roadTool.RequestedMode = NetToolSystem.Mode.Straight;
            float elevation = args["elevation"] == null ? 0 : RequiredFloat(args,"elevation");
            if (elevation < -100 || elevation > 100) throw new ArgumentException("elevation_out_of_range");
            roadTool.RequestedElevation = elevation;
            a.m_Elevation = b.m_Elevation = elevation; a.m_HitPosition.y += elevation; b.m_HitPosition.y += elevation;
            a.m_Position.y += elevation; b.m_Position.y += elevation;
            if (args["control"] is JObject c) { roadTool.ExtraPoints = new[] { BuildPoint(w, c) }; roadTool.RequestedMode = NetToolSystem.Mode.SimpleCurve; }
            roadTool.Begin(p, a, b, budget);
            return ConstructionAccess.Status(ConstructionAccess.Active);
        }
        private JObject StartZone(JObject args)
        {
            var w = RequireCity(); CheckBuildTool(w);
            var p = BuildPrefab<ZonePrefab>(w, args); var a = BuildPoint(w, args["start"] as JObject); var b = BuildPoint(w, args["end"] as JObject);
            if (math.distance(a.m_Position, b.m_Position) > 500) throw new ArgumentException("zoning_rectangle_too_large");
            var zoneTool = w.GetExistingSystemManaged<BridgeZoneTool>(); zoneTool.Dezone = (bool?)args["dezone"] == true; zoneTool.Begin(p, a, b);
            return ConstructionAccess.Status(ConstructionAccess.Active);
        }
        private JObject Network(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var rows = new JArray();
            var center = new float3(RequiredFloat(args, "x"), 0, RequiredFloat(args, "z"));
            float radius = RequiredFloat(args, "radius"); if (radius <= 0 || radius > 1000) throw new ArgumentException("radius_must_be_0_to_1000");
            using (var q = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Game.Net.Node>() }, None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Common.Owner>() } }))
            using (var es = q.ToEntityArray(Allocator.Temp)) foreach (var e in es)
            {
                var n = em.GetComponentData<Game.Net.Node>(e); if (math.distance(n.m_Position.xz, center.xz) > radius) continue;
                var edges = new JArray();
                if (em.HasBuffer<Game.Net.ConnectedEdge>(e)) foreach (var edge in em.GetBuffer<Game.Net.ConnectedEdge>(e, true))
                    edges.Add(new JObject { ["index"] = edge.m_Edge.Index, ["version"] = edge.m_Edge.Version });
                rows.Add(new JObject { ["index"] = e.Index, ["version"] = e.Version, ["position"] = Vector(n.m_Position), ["edges"] = edges });
                if (rows.Count >= 512) break;
            }
            return new JObject { ["nodes"] = rows };
        }
        private JObject ZoneCells(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var rows = new JArray();
            int offset=(int?)args["offset"]??0,limit=(int?)args["limit"]??2048,total=0;
            if(offset<0||limit<1||limit>65536)throw new ArgumentException("invalid_zoning_page");
            float2 center = new float2(RequiredFloat(args, "x"), RequiredFloat(args, "z")); float radius = RequiredFloat(args, "radius");
            if (radius <= 0 || radius > 500) throw new ArgumentException("invalid_radius");
            using (var q = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Block>(), ComponentType.ReadOnly<Cell>() }, None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>() } }))
            using (var es = q.ToEntityArray(Allocator.Temp)) foreach (var e in es.ToArray().OrderBy(e=>e.Index))
            {
                var block = em.GetComponentData<Block>(e); var cells = em.GetBuffer<Cell>(e, true);
                for (int y = 0; y < block.m_Size.y; y++) for (int x = 0; x < block.m_Size.x; x++)
                {
                    float3 position = ZoneUtils.GetCellPosition(block, new int2(x, y));
                    if (math.distance(position.xz, center) > radius) continue;
                    int ordinal=total++; if(ordinal<offset||rows.Count>=limit)continue;
                    var cell = cells[y * block.m_Size.x + x];
                    rows.Add(new JObject { ["blockIndex"] = e.Index, ["blockVersion"] = e.Version, ["x"] = x, ["y"] = y, ["position"] = Vector(position), ["zone"] = cell.m_Zone.m_Index, ["flags"] = cell.m_State.ToString() });
                }
            }
            return new JObject { ["cells"] = rows, ["limit"] = limit, ["offset"] = offset,["total"] = total,["truncated"] = offset+rows.Count<total,["nextOffset"] = offset+rows.Count<total ? (int?)(offset+rows.Count):null };
        }
    }
}

