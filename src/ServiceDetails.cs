using System;
using System.Reflection;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;

namespace CitiesIIAgentBridge
{
    public sealed partial class Mod
    {
        // Explicit read-only component allowlist. No user-provided type names or setters.
        private static readonly string[] DetailComponents = {
            "Game.Buildings.ElectricityConsumer", "Game.Buildings.ElectricityProducer", "Game.Buildings.WaterConsumer", "Game.Buildings.WaterPumpingStation", "Game.Buildings.SewageOutlet",
            "Game.Buildings.School", "Game.Buildings.Hospital", "Game.Buildings.FireStation", "Game.Buildings.PoliceStation", "Game.Buildings.GarbageFacility", "Game.Buildings.DeathcareFacility", "Game.Buildings.PostFacility", "Game.Buildings.ServiceUsage", "Game.Companies.WorkProvider",
            "Game.Prefabs.BuildingData", "Game.Prefabs.PlaceableObjectData", "Game.Prefabs.PlaceableNetData", "Game.Prefabs.NetGeometryData", "Game.Prefabs.ServiceObjectData",
            "Game.Prefabs.PowerPlantData", "Game.Prefabs.WaterPumpingStationData", "Game.Prefabs.SewageOutletData", "Game.Prefabs.SchoolData", "Game.Prefabs.HospitalData", "Game.Prefabs.FireStationData", "Game.Prefabs.PoliceStationData", "Game.Prefabs.GarbageFacilityData", "Game.Prefabs.DeathcareFacilityData"
        };
        private static JObject RawFields(object value, int depth = 0)
        {
            var result = new JObject(); if (value == null || depth > 2) return result;
            foreach (var f in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                object v = f.GetValue(value); if (v == null) continue;
                if (v is Entity e) result[f.Name] = NativeBuild.Id(e);
                else if (v is float3 a) result[f.Name] = Vector(a);
                else if (v is float2 b) result[f.Name] = new JArray(b.x,b.y);
                else if (v is float4 c) result[f.Name] = new JArray(c.x,c.y,c.z,c.w);
                else if (v.GetType().IsEnum) result[f.Name] = v.ToString();
                else if (v.GetType().IsPrimitive || v is string || v is decimal) result[f.Name] = JToken.FromObject(v);
                else if (v.GetType().IsValueType && depth < 2) result[f.Name] = RawFields(v,depth+1);
            }
            return result;
        }
        private static JObject ReadDetail<T>(EntityManager em, Entity entity) where T : unmanaged, IComponentData
            => em.HasComponent<T>(entity) ? RawFields(em.GetComponentData<T>(entity)) : null;
        private static JObject Details(World world, Entity entity)
        {
            var result = new JObject(); var read = typeof(Mod).GetMethod(nameof(ReadDetail),BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var name in DetailComponents)
            {
                var type = typeof(PrefabSystem).Assembly.GetType(name); if (type == null || !typeof(IComponentData).IsAssignableFrom(type)) continue;
                var fields = (JObject)read.MakeGenericMethod(type).Invoke(null,new object[] { world.EntityManager,entity });
                if (fields != null) result[name] = fields;
            }
            return result;
        }
        private JObject PrefabDetails(JObject args)
        {
            var w = RequireCity(); var e = new Entity { Index = RequiredInt(args,"index"), Version = RequiredInt(args,"version") };
            if (!w.EntityManager.Exists(e) || !w.EntityManager.HasComponent<PrefabData>(e)) throw new ArgumentException("live_prefab_entity_required");
            return new JObject { ["name"] = w.GetExistingSystemManaged<PrefabSystem>().GetPrefabName(e), ["rawData"] = Details(w,e), ["locked"] = IsPrefabLocked(w.EntityManager,e) };
        }
    }
}
