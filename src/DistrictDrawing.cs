using System;
using System.Collections.Generic;
using System.Linq;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CitiesIIAgentBridge
{
    public sealed partial class Mod
    {
        private JObject DrawDistrict(JObject args, bool editing)
        {
            RequireControl(); var w=RequireCity(); CheckBuildTool(w); var em=w.EntityManager;
            if(ConstructionAccess.Active!=null || (string)batch?["status"]=="running") throw new InvalidOperationException("wait_for_construction_before_district_drawing");
            em.CompleteAllTrackedJobs();
            var polygon=DistrictGeometry.Read(args["polygon"]); Entity target=Entity.Null, pe;
            if(editing)
            {
                target=new Entity { Index=RequiredInt(args,"index"),Version=RequiredInt(args,"version") };
                if(!AtlasLive(em,target) || !em.HasComponent<District>(target) || em.HasComponent<Owner>(target) || !em.HasBuffer<Game.Areas.Node>(target)) throw new ArgumentException("live_root_district_required");
                if(!DistrictGeometry.Equivalent(DistrictGeometry.Read(args["expectedPolygon"]),BridgeDistrictTool.Polygon(em,target))) throw new InvalidOperationException("district_boundary_changed_refresh_before_retry");
                pe=em.GetComponentData<PrefabRef>(target).m_Prefab;
            }
            else
            {
                using(var q=em.CreateEntityQuery(ComponentType.ReadOnly<AreasConfigurationData>()))
                {
                    if(q.CalculateEntityCount()!=1)throw new InvalidOperationException("district_configuration_unavailable");
                    pe=q.GetSingleton<AreasConfigurationData>().m_DefaultDistrictPrefab;
                }
            }
            var ps=w.GetExistingSystemManaged<PrefabSystem>();
            if(!em.Exists(pe) || IsPrefabLocked(em,pe) || !ps.TryGetPrefab<DistrictPrefab>(pe,out var prefab))throw new ArgumentException("district_prefab_unavailable_or_locked");
            var geometry=em.GetComponentData<AreaGeometryData>(pe);
            double minimum=Math.Max(1,AreaUtils.GetMinNodeDistance(geometry));
            double area=DistrictGeometry.Validate(polygon,minimum);
            string name=(string)args["name"];
            if(name!=null && (string.IsNullOrWhiteSpace(name) || name.Length>80 || name.Any(char.IsControl)))throw new ArgumentException("district_name_must_be_1_to_80_printable_characters");
            bool apply=(bool?)args["apply"]==true;
            var terrain=w.GetExistingSystemManaged<Game.Simulation.TerrainSystem>().GetHeightData();
            var points=polygon.Select(p=>new float3((float)p[0],0,(float)p[1])).ToArray();
            for(int i=0;i<points.Length;i++)points[i].y=Game.Simulation.TerrainUtils.SampleHeight(ref terrain,points[i]);
            var tool=w.GetExistingSystemManaged<BridgeDistrictTool>();
            tool.Begin(prefab,target,points,name,apply);
            districtAtlas=null;
            var result=ConstructionAccess.Results[ConstructionAccess.Active];
            result["requestedPolygon"]=DistrictGeometry.Json(polygon); result["areaSquareMetres"]=area;
            result["minimumEdgeMetres"]=minimum; result["applyRequested"]=apply;
            return ConstructionAccess.Status(ConstructionAccess.Active);
        }
    }

    // Drives native area definitions and validation, never writes a live district's node buffer.
    public class BridgeDistrictTool : AreaToolSystem
    {
        public override string toolID=>"CitiesIIAgentDistrict";
        private string operation,requestedName;
        private int stage,frames;
        private bool commit,submitted;
        private Entity target,expectedPrefab;
        private float3[] requested;
        private double[][] requestedXZ;
        private Dictionary<Entity,double[][]> before;
        private DateTime deadline;

        internal static double[][] Polygon(EntityManager em,Entity entity)
        {
            if(!em.Exists(entity) || !em.HasBuffer<Game.Areas.Node>(entity))return Array.Empty<double[]>();
            var result=new List<double[]>();
            foreach(var node in em.GetBuffer<Game.Areas.Node>(entity,true))result.Add(new[]{(double)node.m_Position.x,(double)node.m_Position.z});
            return result.ToArray();
        }
        private Dictionary<Entity,double[][]> Districts()
        {
            var result=new Dictionary<Entity,double[][]>();
            using(var q=EntityManager.CreateEntityQuery(new EntityQueryDesc { All=new[]{ComponentType.ReadOnly<District>()},None=new[]{ComponentType.ReadOnly<Temp>(),ComponentType.ReadOnly<Deleted>()} }))
            using(var es=q.ToEntityArray(Allocator.Temp))foreach(var e in es)result[e]=Polygon(EntityManager,e);
            return result;
        }
        internal void Begin(DistrictPrefab selected,Entity original,float3[] points,string name,bool apply)
        {
            before=Districts(); requested=points; requestedXZ=points.Select(p=>new[]{(double)p.x,(double)p.z}).ToArray();
            requestedName=name;target=original;expectedPrefab=World.GetExistingSystemManaged<PrefabSystem>().GetEntity(selected);
            commit=apply;submitted=false;stage=0;frames=0;deadline=DateTime.UtcNow.AddSeconds(30);
            operation=ConstructionAccess.Begin(original==Entity.Null?"create_district":"edit_district");
            try { prefab=selected;mode=Mode.Edit;recreate=Entity.Null;
                World.GetExistingSystemManaged<ToolSystem>().activeTool=this;
            } catch(Exception e) { Finish("failed",e.Message); throw; }
        }
        protected override void OnStopRunning()
        {
            applyMode=ApplyMode.Clear;recreate=Entity.Null;
            if(operation!=null)FinishRecord("interrupted","active_tool_changed");
            base.OnStopRunning();
        }
        private bool OriginalsUnchanged(bool includeTarget)
        {
            var now=Districts();
            return before.All(p=>(!includeTarget && p.Key==target) || (now.TryGetValue(p.Key,out var polygon) && DistrictGeometry.Equivalent(p.Value,polygon)));
        }
        private string PreviewCheck(out JArray preview)
        {
            preview=new JArray();var entries=new List<DistrictPreviewEntry>();
            using(var q=EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>()))
            using(var es=q.ToEntityArray(Allocator.Temp))foreach(var e in es)
            {
                var temp=EntityManager.GetComponentData<Temp>(e);
                if((temp.m_Flags & TempFlags.Cancel)!=0)continue;
                bool area=EntityManager.HasComponent<Area>(e);
                if(!area)
                {
                    if((temp.m_Flags & (TempFlags.Create|TempFlags.Modify|TempFlags.Delete|TempFlags.Replace))!=0 &&
                        (EntityManager.HasComponent<Game.Buildings.Building>(e) || EntityManager.HasComponent<Game.Net.Edge>(e) || EntityManager.HasComponent<Game.Net.Node>(e)))
                        return "district_preview_changes_physical_infrastructure";
                    continue;
                }
                var polygon=Polygon(EntityManager,e);var row=NativeBuild.Id(e);
                row["original"]=NativeBuild.Id(temp.m_Original);row["flags"]=temp.m_Flags.ToString();row["polygon"]=DistrictGeometry.Json(polygon);preview.Add(row);
                entries.Add(new DistrictPreviewEntry {
                    District=EntityManager.HasComponent<District>(e),PrefabMatches=EntityManager.HasComponent<PrefabRef>(e) && EntityManager.GetComponentData<PrefabRef>(e).m_Prefab==expectedPrefab,
                    HasOriginal=temp.m_Original!=Entity.Null,OriginalMatches=temp.m_Original==target,
                    Create=(temp.m_Flags & TempFlags.Create)!=0,Modify=(temp.m_Flags & TempFlags.Modify)!=0,
                    Delete=(temp.m_Flags & (TempFlags.Delete|TempFlags.Replace))!=0,
                    Cancel=(temp.m_Flags & TempFlags.Cancel)!=0,
                    Complete=(EntityManager.GetComponentData<Area>(e).m_Flags & AreaFlags.Complete)!=0,
                    GeometryMatches=DistrictGeometry.Equivalent(requestedXZ,polygon)
                });
            }
            return DistrictPreviewSafety.Validate(entries,target!=Entity.Null);
        }
        protected override JobHandle OnUpdate(JobHandle deps)
        {
            if(operation==null)return deps;
            try
            {
                deps.Complete();
                if(ConstructionAccess.Active!=operation || ConstructionAccess.Allowed?.Invoke()!=true) { Finish("interrupted","district_operation_stopped");return deps; }
                if(DateTime.UtcNow>deadline)throw new InvalidOperationException("district_operation_timed_out");
                if(stage==0)
                {
                    var points=GetControlPoints(out var moveStarts,out var ready);ready.Complete();points.Clear();moveStarts.Clear();
                    // Reproduce the native Create transition after OnStartRunning resets state.
                    mode=Mode.Edit;recreate=target;parentMeshOverrideEnabled=false;underground=false;
                    ConstructionAccess.Field(typeof(AreaToolSystem),this,"m_State",State.Create);
                    ConstructionAccess.Field(typeof(AreaToolSystem),this,"m_AllowCreateArea",true);
                    ConstructionAccess.Field(typeof(AreaToolSystem),this,"m_ControlPointsMoved",false);
                    ConstructionAccess.Field(typeof(AreaToolSystem),this,"m_ForceCancel",false);
                    ConstructionAccess.Field(typeof(AreaToolSystem),this,"m_ApplyBlocked",false);
                    requireAreas=AreaUtils.GetTypeMask(Game.Areas.AreaType.District);requireZones=false;requireNet=Game.Net.Layer.None;requireUnderground=false;
                    foreach(var p in requested)points.Add(new ControlPoint { m_Position=p,m_HitPosition=p,m_ElementIndex=new int2(-1,-1),m_Rotation=quaternion.identity });
                    points.Add(points[0]); // Native completeness requires exact first/last equality.
                    applyMode=ApplyMode.Clear;
                    deps=(JobHandle)ConstructionAccess.Call(typeof(AreaToolSystem),this,"UpdateDefinitions",deps,default(NativeArray<Entity>),default(NativeArray<Entity>));
                    ConstructionAccess.Results[operation]["status"]="validating";stage=1;return deps;
                }
                if(stage==1)
                {
                    applyMode=ApplyMode.None;if(++frames<4)return deps;
                    var errors=ConstructionAccess.Errors(EntityManager);
                    if(errors.Count>0) { ConstructionAccess.Results[operation]["placementErrors"]=errors;throw new InvalidOperationException("game_rejected_district_polygon"); }
                    if(!GetAllowApply()) { if(frames<60)return deps;throw new InvalidOperationException("no_valid_district_preview"); }
                    string problem=PreviewCheck(out var preview);ConstructionAccess.Results[operation]["preview"]=preview;
                    if(problem!=null)throw new InvalidOperationException(problem);
                    if(!OriginalsUnchanged(true) || Districts().Count!=before.Count)throw new InvalidOperationException("districts_changed_during_preview");
                    if(!commit) { ConstructionAccess.Results[operation]["applied"]=false;Finish("complete");return deps; }
                    applyMode=ApplyMode.Apply;submitted=true;ConstructionAccess.Results[operation]["applicationMayHaveOccurred"]=true;
                    ConstructionAccess.Results[operation]["status"]="applying";stage=2;frames=0;return deps;
                }
                applyMode=ApplyMode.Clear;if(++frames<4)return deps;
                var after=Districts();var added=after.Keys.Where(e=>!before.ContainsKey(e)).ToArray();
                var result=ConstructionAccess.Results[operation];result["observedNewDistricts"]=new JArray(added.Select(NativeBuild.Id));
                if(!OriginalsUnchanged(false))throw new InvalidOperationException("unexpected_neighbor_district_change");
                var changed=target==Entity.Null ? (added.Length==1?added[0]:Entity.Null) : target;
                if((target==Entity.Null ? added.Length!=1 || after.Count!=before.Count+1 : added.Length!=0 || after.Count!=before.Count) ||
                    changed==Entity.Null || !after.TryGetValue(changed,out var actual) || !DistrictGeometry.Equivalent(requestedXZ,actual))
                { if(frames<60)return deps;throw new InvalidOperationException("district_boundary_change_not_verified"); }
                if(!EntityManager.HasComponent<PrefabRef>(changed) || EntityManager.GetComponentData<PrefabRef>(changed).m_Prefab!=expectedPrefab)throw new InvalidOperationException("district_result_prefab_mismatch");
                result["district"]=NativeBuild.Id(changed);result["polygon"]=DistrictGeometry.Json(actual);
                var names=World.GetExistingSystemManaged<Game.UI.NameSystem>();
                if(requestedName!=null) { names.SetCustomName(changed,requestedName);if(!names.TryGetCustomName(changed,out var actualName)||actualName!=requestedName)throw new InvalidOperationException("district_name_not_verified"); }
                result["district"]=NativeBuild.Id(changed);result["polygon"]=DistrictGeometry.Json(actual);
                result["name"]=names.GetRenderedLabelName(changed);result["applied"]=true;
                result["verification"]="native_entity_and_polygon_readback; census_refresh_and_simulation_effects_not_verified";
                Finish("complete");
            }
            catch(Exception e) { Finish("failed",e.Message); }
            return deps;
        }
        private void FinishRecord(string status,string error)
        {
            if(operation==null)return;
            var row=ConstructionAccess.Results[operation];row["applicationMayHaveOccurred"]=submitted;
            string prior=(string)row["status"];
            if(prior=="queued" || prior=="validating" || prior=="applying")ConstructionAccess.Finish(operation,status,error);
            operation=null;
        }
        private void Finish(string status,string error=null)
        {
            applyMode=ApplyMode.Clear;recreate=Entity.Null;FinishRecord(status,error);
            var tools=World.GetExistingSystemManaged<ToolSystem>();
            if(tools.activeTool==this)tools.activeTool=World.GetExistingSystemManaged<DefaultToolSystem>();
        }
    }
}
