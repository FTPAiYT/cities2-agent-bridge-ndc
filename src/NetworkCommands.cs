using System;
using System.Collections.Generic;
using System.Linq;
using Game.Common;
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
        private JObject NetworkEdges(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var ps = w.GetExistingSystemManaged<PrefabSystem>(); var rows = new JArray();
            float2 center = new float2(RequiredFloat(args, "x"), RequiredFloat(args, "z")); float radius = RequiredFloat(args, "radius");
            if (radius <= 0 || radius > 2000) throw new ArgumentException("invalid_radius");
            using (var q = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.ReadOnly<Game.Net.Curve>() }, None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Owner>() } }))
            using (var es = q.ToEntityArray(Allocator.Temp)) foreach (var e in es)
            {
                var curve = em.GetComponentData<Game.Net.Curve>(e); if (math.distance((curve.m_Bezier.a.xz + curve.m_Bezier.d.xz) * 0.5f, center) > radius + curve.m_Length * 0.5f) continue;
                var edge = em.GetComponentData<Game.Net.Edge>(e); var info = NativeBuild.Id(e);
                info["prefab"] = ps.GetPrefabName(em.GetComponentData<PrefabRef>(e).m_Prefab);
                info["startNode"] = NativeBuild.Id(edge.m_Start); info["endNode"] = NativeBuild.Id(edge.m_End); info["length"] = curve.m_Length;
                info["curve"] = new JArray(Vector(curve.m_Bezier.a), Vector(curve.m_Bezier.b), Vector(curve.m_Bezier.c), Vector(curve.m_Bezier.d));
                if (em.HasComponent<Game.Net.Road>(e))
                {
                    var r = em.GetComponentData<Game.Net.Road>(e);
                    info["trafficDistanceRaw"] = new JArray(r.m_TrafficFlowDistance0.x,r.m_TrafficFlowDistance0.y,r.m_TrafficFlowDistance0.z,r.m_TrafficFlowDistance0.w,r.m_TrafficFlowDistance1.x,r.m_TrafficFlowDistance1.y,r.m_TrafficFlowDistance1.z,r.m_TrafficFlowDistance1.w);
                    info["trafficDurationRaw"] = new JArray(r.m_TrafficFlowDuration0.x,r.m_TrafficFlowDuration0.y,r.m_TrafficFlowDuration0.z,r.m_TrafficFlowDuration0.w,r.m_TrafficFlowDuration1.x,r.m_TrafficFlowDuration1.y,r.m_TrafficFlowDuration1.z,r.m_TrafficFlowDuration1.w);
                }
                if(em.HasBuffer<Game.Net.ServiceCoverage>(e))
                {
                    var coverage=new JArray();int slot=0;
                    foreach(var c in em.GetBuffer<Game.Net.ServiceCoverage>(e,true))coverage.Add(new JObject{["nativeSlot"]=slot++,["start"]=c.m_Coverage.x,["end"]=c.m_Coverage.y});
                    info["serviceCoverageNative"]=coverage;
                }
                rows.Add(info); if (rows.Count >= 2048) break;
            }
            return new JObject { ["edges"] = rows, ["limit"] = 2048,["possiblyTruncated"]=rows.Count>=2048 };
        }
        private JObject NetworkPath(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager;
            var from = new Entity { Index = RequiredInt(args, "fromIndex"), Version = RequiredInt(args, "fromVersion") };
            var to = new Entity { Index = RequiredInt(args, "toIndex"), Version = RequiredInt(args, "toVersion") };
            if (!em.HasComponent<Game.Net.Node>(from) || !em.HasComponent<Game.Net.Node>(to)) throw new ArgumentException("endpoints_must_be_live_network_nodes");
            var queue = new Queue<Entity>(); var seen = new Dictionary<Entity, Entity>(); var via = new Dictionary<Entity,Entity>(); queue.Enqueue(from); seen[from] = Entity.Null;
            while (queue.Count > 0 && seen.Count <= 100000)
            {
                var node = queue.Dequeue(); if (node == to) break;
                if (!em.HasBuffer<Game.Net.ConnectedEdge>(node)) continue;
                foreach (var connected in em.GetBuffer<Game.Net.ConnectedEdge>(node,true))
                {
                    var edge = connected.m_Edge; if (!em.Exists(edge) || em.HasComponent<Temp>(edge) || em.HasComponent<Deleted>(edge) || !em.HasComponent<Game.Net.Edge>(edge)) continue;
                    var data = em.GetComponentData<Game.Net.Edge>(edge); var next = data.m_Start == node ? data.m_End : data.m_Start;
                    if (seen.ContainsKey(next)) continue; seen[next] = node; via[next] = edge; queue.Enqueue(next);
                }
            }
            var path = new List<Entity>(); if (seen.ContainsKey(to)) for (var n = to; n != from; n = seen[n]) path.Add(via[n]); path.Reverse();
            return new JObject { ["connected"] = seen.ContainsKey(to), ["visited"] = seen.Count, ["edges"] = new JArray(path.Select(NativeBuild.Id)), ["meaning"] = "Physical network adjacency; not a vehicle route or proof of utility capacity." };
        }
        private JObject UpgradeNetwork(JObject args)
        {
            var w = RequireCity(); RequireControl(); CheckBuildTool(w); var em = w.EntityManager;
            var target = new Entity { Index = RequiredInt(args,"index"), Version = RequiredInt(args,"version") };
            if (!em.Exists(target) || !em.HasComponent<Game.Net.Edge>(target) || !em.HasComponent<Game.Net.Curve>(target)) throw new ArgumentException("live_network_edge_required");
            var prefab = BuildPrefab<NetPrefab>(w,args); var curve = em.GetComponentData<Game.Net.Curve>(target).m_Bezier;
            var a = new ControlPoint { m_OriginalEntity = target, m_Position = curve.a, m_HitPosition = curve.a, m_CurvePosition = 0, m_Rotation = quaternion.identity };
            var b = new ControlPoint { m_OriginalEntity = target, m_Position = curve.d, m_HitPosition = curve.d, m_CurvePosition = 1, m_Rotation = quaternion.identity };
            int budget = RequiredInt(args,"maxCost"); if (budget < 0 || budget > 1000000) throw new ArgumentException("invalid_budget");
            var tool = w.GetExistingSystemManaged<BridgeRoadTool>(); tool.ExtraPoints = null; tool.RequestedMode = NetToolSystem.Mode.Replace;
            tool.RequestedElevation = 0;
            tool.Begin(prefab,a,b,budget); tool.Replacement = target;
            return ConstructionAccess.Status(ConstructionAccess.Active);
        }
        private Entity[] pendingTiles;
        private string tileOperation;
        private int tileBudget;
        private DateTime tileDeadline;
        private JObject PurchaseTiles(JObject args)
        {
            var w = RequireCity(); RequireControl(); CheckBuildTool(w); var em = w.EntityManager;
            var input = args["tiles"] as JArray; if (input == null || input.Count < 1 || input.Count > 64) throw new ArgumentException("select_1_to_64_tiles");
            var tiles = new HashSet<Entity>(); foreach (JObject t in input)
            {
                var e = new Entity { Index = RequiredInt(t,"index"), Version = RequiredInt(t,"version") };
                if (!em.Exists(e) || !em.HasComponent<Game.Areas.MapTile>(e) || !em.HasComponent<Native>(e)) throw new ArgumentException("tile_stale_or_already_owned"); tiles.Add(e);
            }
            int budget = RequiredInt(args,"maxCost"); if (budget < 0) throw new ArgumentException("invalid_budget");
            tileOperation = ConstructionAccess.Begin("purchase_tiles"); pendingTiles = tiles.ToArray(); tileBudget = budget; tileDeadline = DateTime.UtcNow.AddSeconds(10);
            w.GetExistingSystemManaged<MapTilePurchaseSystem>().selecting = true;
            return ConstructionAccess.Status(tileOperation);
        }
        private void TileTick()
        {
            if (tileOperation == null) return;
            var world = RequireCity(); var system = world.GetExistingSystemManaged<MapTilePurchaseSystem>(); var em = world.EntityManager;
            try
            {
                RequireControl(); if (!system.selecting || DateTime.UtcNow > tileDeadline) throw new InvalidOperationException("tile_purchase_interrupted");
                using (var q = em.CreateEntityQuery(ComponentType.ReadWrite<SelectionElement>()))
                {
                    if (q.IsEmptyIgnoreFilter) return;
                    var selected = em.GetBuffer<SelectionElement>(q.GetSingletonEntity()); selected.Clear(); foreach (var tile in pendingTiles) selected.Add(new SelectionElement { m_Entity = tile });
                    ConstructionAccess.Call(typeof(MapTilePurchaseSystem),system,"UpdateStatus");
                    if (system.status != TilePurchaseErrorFlags.None) throw new InvalidOperationException("tile_purchase_rejected: " + system.status);
                    if (system.cost > tileBudget) throw new InvalidOperationException("tile_cost_exceeds_budget");
                    ConstructionAccess.Results[tileOperation]["cost"] = system.cost; system.PurchaseSelection();
                    foreach (var tile in pendingTiles) if (em.HasComponent<Native>(tile)) throw new InvalidOperationException("tile_purchase_not_observed");
                    ConstructionAccess.Results[tileOperation]["tiles"] = new JArray(pendingTiles.Select(NativeBuild.Id));
                    ConstructionAccess.Finish(tileOperation,"complete");
                }
            }
            catch (Exception e) { ConstructionAccess.Finish(tileOperation,"failed",e.Message); }
            system.selecting = false; tileOperation = null; pendingTiles = null;
        }
    }
}
