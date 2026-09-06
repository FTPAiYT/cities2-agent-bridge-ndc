using System;
using System.Linq;
using Game.Buildings;
using Game.Prefabs;
using Game.Simulation;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CitiesIIAgentBridge
{
    public sealed partial class Mod
    {
        private static JArray Shortages(EntityManager em, Entity e)
        {
            var rows = new JArray();
            if (em.HasComponent<WaterConsumer>(e))
            {
                var c = em.GetComponentData<WaterConsumer>(e);
                if (c.m_WantedConsumption > 0 && c.m_FulfilledFresh < c.m_WantedConsumption && c.m_FreshCooldownCounter > 0)
                    rows.Add(new JObject { ["type"] = "water", ["wanted"] = c.m_WantedConsumption, ["supplied"] = c.m_FulfilledFresh, ["cooldown"] = c.m_FreshCooldownCounter });
                if (c.m_WantedConsumption > 0 && c.m_FulfilledSewage < c.m_WantedConsumption && c.m_SewageCooldownCounter > 0)
                    rows.Add(new JObject { ["type"] = "sewage", ["wanted"] = c.m_WantedConsumption, ["supplied"] = c.m_FulfilledSewage, ["cooldown"] = c.m_SewageCooldownCounter });
            }
            if (em.HasComponent<ElectricityConsumer>(e))
            {
                var c = em.GetComponentData<ElectricityConsumer>(e);
                if (c.m_WantedConsumption > 0 && c.m_FulfilledConsumption < c.m_WantedConsumption && c.m_CooldownCounter > 0)
                    rows.Add(new JObject { ["type"] = "electricity", ["wanted"] = c.m_WantedConsumption, ["supplied"] = c.m_FulfilledConsumption, ["cooldown"] = c.m_CooldownCounter });
            }
            return rows;
        }
        private JObject Observation()
        {
            var w = RequireCity(); var em = w.EntityManager; var management = CityManagement();
            int buildingCount = 0, construction = 0, shortages = 0;
            var shortageEntities=new JArray();var constructionEntities=new JArray();
            foreach (var e in NativeBuild.Buildings(em))
            {
                if (!em.HasComponent<Game.Objects.Transform>(e) || em.HasComponent<PrefabData>(e) || em.HasComponent<Game.Common.Native>(e)) continue;
                ++buildingCount; if (em.HasComponent<Game.Objects.UnderConstruction>(e)) {++construction;constructionEntities.Add(NativeBuild.Id(e));}
                if (Shortages(em,e).Count > 0) {++shortages;shortageEntities.Add(e.Index+":"+e.Version);}
            }
            return new JObject { ["city"] = management["city"].DeepClone(), ["demand"] = management["demand"].DeepClone(), ["balanceRaw"] = management["budget"]["balanceRaw"], ["buildings"] = buildingCount, ["underConstruction"] = construction, ["persistentShortages"] = shortages,["shortageEntities"]=shortageEntities,["constructionEntities"]=constructionEntities };
        }
        private static JObject Factors(NativeArray<int> values, JobHandle ready)
        {
            ready.Complete(); var rows = new JObject();
            for (int i=0;i<values.Length;i++) rows[Enum.GetName(typeof(DemandFactor),i) ?? ("factor_"+i)] = values[i];
            return rows;
        }
        private JObject Diagnostics(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var result = CityManagement();
            var h = w.GetExistingSystemManaged<CountHouseholdDataSystem>();
            result["populationDetails"] = new JObject {
                ["dataReady"] = !h.IsCountDataNotReady(), ["unemploymentRateNative"] = h.UnemploymentRate,
                ["homelessnessRateNative"] = h.HomelessnessRate, ["homelessCitizens"] = h.HomelessCitizenCount,
                ["workers"] = h.CityWorkerCount, ["workingAgeEligible"] = h.WorkableCitizenCount,
                ["movingInCitizens"] = h.MovingInCitizenCount, ["students"] = h.StudentCount,
                ["educationCounts"] = new JArray(h.UneducatedCount,h.PoorlyEducatedCount,h.EducatedCount,h.WellEducatedCount,h.HighlyEducatedCount)
            };
            var jobs = w.GetExistingSystemManaged<CountWorkplacesSystem>();
            result["workplaces"] = new JObject { ["totalByEducation"] = RawFields(jobs.GetTotalWorkplaces()), ["vacantByEducation"] = RawFields(jobs.GetFreeWorkplaces()) };
            var residential = w.GetExistingSystemManaged<ResidentialDemandSystem>(); var industrial = w.GetExistingSystemManaged<IndustrialDemandSystem>();
            var demand = new JObject(); JobHandle ready;
            demand["residentialLow"] = Factors(residential.GetLowDensityDemandFactors(out ready),ready);
            demand["residentialMedium"] = Factors(residential.GetMediumDensityDemandFactors(out ready),ready);
            demand["residentialHigh"] = Factors(residential.GetHighDensityDemandFactors(out ready),ready);
            demand["commercial"] = Factors(w.GetExistingSystemManaged<CommercialDemandSystem>().GetDemandFactors(out ready),ready);
            demand["industrial"] = Factors(industrial.GetIndustrialDemandFactors(out ready),ready);
            demand["office"] = Factors(industrial.GetOfficeDemandFactors(out ready),ready);
            result["demandFactorsNative"] = demand;
            result["happinessFactors"] = HappinessFactors(w);
            var services = new JArray(); var shortages = new JArray(); var efficiency = new JArray();
            long electricityCapacity=0,electricityProduced=0,waterCapacity=0,waterProduced=0,sewageCapacity=0,sewageProcessed=0;
            int count=0,construction=0;
            foreach (var e in NativeBuild.Buildings(em))
            {
                if (!em.HasComponent<Game.Objects.Transform>(e) || em.HasComponent<PrefabData>(e) || em.HasComponent<Game.Common.Native>(e)) continue;
                ++count; if(em.HasComponent<Game.Objects.UnderConstruction>(e)) ++construction;
                var row = NativeBuild.Id(e); var pe = em.GetComponentData<PrefabRef>(e).m_Prefab;
                row["prefab"] = w.GetExistingSystemManaged<PrefabSystem>().GetPrefabName(pe);
                row["position"] = Vector(em.GetComponentData<Game.Objects.Transform>(e).m_Position);
                var issues = Shortages(em,e); if (issues.Count>0) { var issue = (JObject)row.DeepClone(); issue["shortages"] = issues; shortages.Add(issue); }
                if(em.HasComponent<ElectricityProducer>(e)) { var c=em.GetComponentData<ElectricityProducer>(e); electricityCapacity+=c.m_Capacity; electricityProduced+=c.m_LastProduction; }
                if(em.HasComponent<Game.Buildings.WaterPumpingStation>(e)) { var c=em.GetComponentData<Game.Buildings.WaterPumpingStation>(e); waterCapacity+=c.m_Capacity; waterProduced+=c.m_LastProduction; }
                if(em.HasComponent<Game.Buildings.SewageOutlet>(e)) { var c=em.GetComponentData<Game.Buildings.SewageOutlet>(e); sewageCapacity+=c.m_Capacity; sewageProcessed+=c.m_LastProcessed; }
                if(em.HasComponent<ServiceObjectData>(pe))
                {
                    row["actual"] = Details(w,e); row["design"] = Details(w,pe);
                    row["students"] = em.HasBuffer<Student>(e) ? em.GetBuffer<Student>(e,true).Length : (int?)null;
                    row["patients"] = em.HasBuffer<Patient>(e) ? em.GetBuffer<Patient>(e,true).Length : (int?)null;
                    row["staff"] = em.HasBuffer<Game.Companies.Employee>(e) ? em.GetBuffer<Game.Companies.Employee>(e,true).Length : (int?)null;
                    row["roadEdge"] = NativeBuild.Id(em.GetComponentData<Building>(e).m_RoadEdge);
                    services.Add(row);
                }
                if (em.HasBuffer<Efficiency>(e))
                {
                    var penalties = new JArray(); foreach(var f in em.GetBuffer<Efficiency>(e,true))
                        if(f.m_Efficiency<0.99f) penalties.Add(new JObject{["factor"]=f.m_Factor.ToString(),["multiplier"]=f.m_Efficiency});
                    if(penalties.Count>0 && efficiency.Count<100) { var issue=(JObject)row.DeepClone(); issue.Remove("actual"); issue.Remove("design"); issue["penalties"]=penalties; efficiency.Add(issue); }
                }
            }
            result["utilitiesRaw"] = new JObject { ["electricity"] = new JObject{["capacity"]=electricityCapacity,["production"]=electricityProduced}, ["water"] = new JObject{["capacity"]=waterCapacity,["production"]=waterProduced}, ["sewage"] = new JObject{["capacity"]=sewageCapacity,["processing"]=sewageProcessed} };
            result["serviceBuildings"]=services; result["persistentShortages"]=shortages; result["efficiencyPenalties"]=efficiency;
            result["buildingCount"]=count; result["underConstruction"]=construction;
            result["notes"]=new JArray("Demand factors and efficiency penalties come from native simulation data; they are evidence, not a complete causal model.","Capacity totals do not prove that every separate network is connected. One-unit shortfalls without a cooldown are excluded from persistent shortages.","Design capacities exclude some upgrades and efficiency adjustments; actual fields and enrollment are returned separately.");
            return result;
        }
        private static JObject HappinessFactors(World w)
        {
            // Same 16-frame aggregation used by CitizenHappinessSystem.GetHappinessFactor.
            var system=w.GetExistingSystemManaged<CitizenHappinessSystem>();
            ConstructionAccess.Read<JobHandle>(typeof(CitizenHappinessSystem),system,"m_LastDeps").Complete();
            var data=ConstructionAccess.Read<NativeArray<int4>>(typeof(CitizenHappinessSystem),system,"m_HappinessFactors");
            var result=new JObject();int count=(int)CitizenHappinessSystem.HappinessFactor.Count;
            if(!data.IsCreated || data.Length!=count*16)throw new InvalidOperationException("happiness_factor_layout_changed");
            using(var query=w.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HappinessFactorParameterData>()))
            using(var entities=query.ToEntityArray(Allocator.Temp))
            {
                if(entities.Length!=1)return new JObject{["available"]=false,["reason"]="parameter_singleton_unavailable"};
                var parameters=w.EntityManager.GetBuffer<HappinessFactorParameterData>(entities[0],true);
                if(parameters.Length<count)throw new InvalidOperationException("happiness_parameters_changed");
                for(int i=0;i<count;i++)
                {
                    int4 sum=0;for(int frame=0;frame<16;frame++)sum+=data[i+frame*count];
                    var p=parameters[i];bool locked=p.m_LockedEntity!=Entity.Null && IsPrefabLocked(w.EntityManager,p.m_LockedEntity);
                    float3 value=locked?float3.zero:(sum.y>0?new float3(sum.x/(2f*sum.y),sum.z/sum.y,sum.w/sum.y):float3.zero)-p.m_BaseLevel;
                    result[((CitizenHappinessSystem.HappinessFactor)i).ToString()]=new JObject{["effect"]=value.x,["nativeSecondary"]=new JArray(value.y,value.z),["samples"]=sum.y,["locked"]=locked};
                }
            }
            return result;
        }
    }
}

