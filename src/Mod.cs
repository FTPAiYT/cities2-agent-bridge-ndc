using System;
using System.IO;
using System.Linq;
using Colossal.Logging;
using Colossal.Serialization.Entities;
using Game;
using Game.City;
using Game.Modding;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitiesIIAgentBridge
{
    // Main-thread mailbox with native construction-tool subclasses; no Harmony patches.
    public sealed partial class Mod : IMod
    {
        private ILog log;
        private Mailbox mailbox;
        private BridgeSettings settings;
        private Func<bool> updater;
        private string citySession;
        private DateTime nextTick;
        private bool disposed;
        private bool faulted;
        private bool mailboxContended;
        private const string ModVersion = "0.4.2";

        public void OnLoad(UpdateSystem updateSystem)
        {
            // IMod instances may be allocated without running constructors/field initializers.
            log = LogManager.GetLogger("CitiesIIAgentBridge").SetShowsErrorsInUI(false);
            citySession = Guid.NewGuid().ToString("N");
            settings = new BridgeSettings(this);
            settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(settings));
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CitiesIIAgentBridge");
            mailbox = new Mailbox(root, Dispatch, () => citySession);
            updater = Tick;
            ConstructionAccess.Allowed = () => !disposed && settings.AllowControl && !File.Exists(Path.Combine(mailbox.Root, "STOP"));
            updateSystem.UpdateAt<BridgeRoadTool>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BridgeZoneTool>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BridgeObjectTool>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BridgeBulldozeTool>(SystemUpdatePhase.ToolUpdate);
            GameManager.instance.onGamePreload += OnPreload;
            GameManager.instance.RegisterUpdater(updater);
            try { mailbox.Publish(new JObject { ["status"] = "starting", ["modVersion"] = ModVersion }); }
            catch (IOException e) when (Mailbox.IsSharingViolation(e)) { MailboxContention(e); }
            log.Info("Bridge loaded; mailbox: " + root);
        }

        private void OnPreload(Purpose purpose, GameMode mode)
        {
            FinishSimulation("city_changed", false);
            neighborhoodPlan = null;
            if (ConstructionAccess.Active != null) ConstructionAccess.Finish(ConstructionAccess.Active,"interrupted","city_changed");
            tileOperation = null; pendingTiles = null;
            if (batch != null && (string)batch["status"] == "running") { batch["status"] = "interrupted"; batch["error"] = "city_changed"; }
            citySession = Guid.NewGuid().ToString("N");
            // Changing saves invalidates pending control permission as well as entity IDs.
            settings.AllowControl = false;
        }

        public void OnDispose()
        {
            FinishSimulation("mod_disposed");
            disposed = true;
            if (GameManager.instance != null)
            {
                if (updater != null) GameManager.instance.UnregisterUpdater(updater);
                GameManager.instance.onGamePreload -= OnPreload;
            }
            settings?.UnregisterInOptionsUI();
            try { mailbox?.Publish(new JObject { ["status"] = "stopped", ["modVersion"] = ModVersion }); }
            catch (Exception e) { log?.Warn(e.Message); }
            log?.Info("Bridge disposed");
        }

        private bool Tick()
        {
            if (disposed) return true; // MainThreadDispatcher removes a callback returning true.
            if (DateTime.UtcNow < nextTick) return false;
            nextTick = DateTime.UtcNow.AddMilliseconds(250);
            try
            {
                bool communicated = BridgeTick.Run(
                    () => { if (File.Exists(Path.Combine(mailbox.Root, "STOP"))) settings.AllowControl = false; },
                    SimulationTick,
                    () => mailbox.Publish(new JObject
                {
                    ["status"] = "ready", ["modVersion"] = ModVersion,
                    ["gameVersion"] = Application.version,
                    ["gameMode"] = GameManager.instance.gameMode.ToString(),
                    ["loading"] = GameManager.instance.isGameLoading,
                    ["controlEnabled"] = settings.AllowControl,
                    ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id
                }),
                    () => mailbox.Pump(), WorkflowTick, MailboxContention);
                if (communicated && mailboxContended)
                {
                    log.Info("Mailbox access recovered; control permission was not changed by recovery.");
                    mailboxContended = false;
                }
                faulted = false;
            }
            catch (Exception e)
            {
                settings.AllowControl = false;
                FinishSimulation("bridge_fault");
                if (!faulted) log.Error(e);
                faulted = true;
            }
            return false;
        }

        private void MailboxContention(IOException error)
        {
            if (!mailboxContended)
                log.Warn("Temporary mailbox file lock; retrying on subsequent ticks. " + error.Message);
            mailboxContended = true;
        }

        private World RequireCity()
        {
            var manager = GameManager.instance;
            var world = World.DefaultGameObjectInjectionWorld;
            if (manager.isGameLoading || manager.gameMode != GameMode.Game || world == null || !world.IsCreated)
                throw new InvalidOperationException("no_loaded_city");
            return world;
        }

        private void RequireControl()
        {
            if (!settings.AllowControl || File.Exists(Path.Combine(mailbox.Root, "STOP")))
                throw new InvalidOperationException("control_disabled: enable it in Options > Cities II Agent Bridge");
        }

        private JObject Dispatch(string command, JObject args)
        {
            // These status polls must not terminate an active bounded simulation step.
            bool statusOnly = command == "ping" || command == "get_capabilities" || command == "get_operation" || command == "get_batch" || command == "get_simulation_step";
            if(!statusOnly && command != "simulate_step" && command != "cancel_simulation_step" && command != "set_simulation_speed" && command != "set_camera")
            {
                if(settings.AllowControl) PauseAnalysis();
                else if(RequireCity().GetExistingSystemManaged<SimulationSystem>().selectedSpeed != 0)
                    throw new InvalidOperationException("pause_game_before_analysis_or_enable_bridge_controls");
            }
            if(!dispatchingBatch && (string)batch?["status"] == "running" && (Array.IndexOf(batchCommands,command)>=0 || command=="preview_building" || command=="plan_neighborhood" || command=="execute_neighborhood"))
                throw new InvalidOperationException("batch_in_progress_wait_or_cancel_batch");
            switch (command)
            {
                case "ping": return new JObject { ["pong"] = true, ["modVersion"] = ModVersion };
                case "get_capabilities": return new JObject
                {
                    ["read"] = new JArray("ping", "get_capabilities", "get_city_state", "get_camera", "get_selected", "inspect_entity", "get_water_facilities"),
                    ["control"] = new JArray("set_camera", "set_simulation_speed", "build_road", "build_network", "upgrade_network", "zone_rectangle", "clear_zoning", "place_building", "relocate_building", "demolish", "purchase_tiles", "set_tax", "set_service_budget", "save_checkpoint", "batch_execute"),
                    ["constructionQueries"] = new JArray("get_build_prefabs", "get_prefab_details", "get_network", "get_network_edges", "trace_network", "get_zone_cells", "get_operation", "get_batch", "get_city_management", "get_services", "sample_terrain", "get_tiles", "get_buildings", "diagnose_connections"),
                    ["buildVersion"] = ModVersion, ["liveValidation"] = "v0.4.2_object_placement_requires_live_validation",
                    ["planning"] = new JArray("get_city_map","get_city_diagnostics","find_building_sites","preview_building","plan_neighborhood","execute_neighborhood","get_neighborhood_plan"),
                    ["simulation"] = new JArray("pause_for_analysis","simulate_step","get_simulation_step","cancel_simulation_step","cancel_batch"),
                    ["analysisPausesGame"] = true,
                    ["construction"] = "native_preview_and_apply", ["controlEnabled"] = settings.AllowControl
                };
                case "get_city_state": return CityState();
                case "get_city_diagnostics": return Diagnostics(args);
                case "get_city_map": return CityMap(args);
                case "find_building_sites": return FindBuildingSites(args);
                case "preview_building": args["previewOnly"]=true; return PlaceBuilding(args);
                case "plan_neighborhood": return PlanNeighborhood(args);
                case "execute_neighborhood": return ExecuteNeighborhood(args);
                case "get_neighborhood_plan": return NeighborhoodStatus(args);
                case "cancel_batch": return CancelBatch();
                case "pause_for_analysis": PauseAnalysis(); return Diagnostics(args);
                case "simulate_step": return BeginSimulation(args);
                case "cancel_simulation_step": return EndSimulation(args);
                case "get_simulation_step":
                    if(simulationOperation == null || (string)args["id"] != simulationOperation) throw new ArgumentException("simulation_step_not_found");
                    return ConstructionAccess.Status(simulationOperation);
                case "get_camera": RequireCity(); return CameraState();
                case "get_selected":
                {
                    World world = RequireCity();
                    var tools = world.GetExistingSystemManaged<ToolSystem>();
                    if (tools == null || tools.selected == Entity.Null) return new JObject { ["selected"] = null };
                    return new JObject { ["selected"] = Inspect(world, tools.selected) };
                }
                case "inspect_entity":
                {
                    var entity = new Entity { Index = RequiredInt(args, "index"), Version = RequiredInt(args, "version") };
                    return Inspect(RequireCity(), entity);
                }
                case "get_water_facilities": return WaterFacilities();
                case "get_build_prefabs": return BuildPrefabs(args);
                case "get_prefab_details": return PrefabDetails(args);
                case "get_network": return Network(args);
                case "get_network_edges": return NetworkEdges(args);
                case "trace_network": return NetworkPath(args);
                case "upgrade_network": return UpgradeNetwork(args);
                case "purchase_tiles": return PurchaseTiles(args);
                case "get_zone_cells": return ZoneCells(args);
                case "build_road": RequireControl(); return StartRoad(args);
                case "build_network": RequireControl(); return StartRoad(args);
                case "place_building": return PlaceBuilding(args);
                case "relocate_building": if (args["moveIndex"] == null) throw new ArgumentException("moveIndex_required"); return PlaceBuilding(args);
                case "demolish": return Demolish(args);
                case "get_city_management": return CityManagement();
                case "get_services": return Services(args);
                case "set_tax": return SetTax(args);
                case "set_service_budget": return SetServiceBudget(args);
                case "sample_terrain": return Terrain(args);
                case "get_tiles": return Tiles();
                case "get_buildings": return Buildings(args);
                case "diagnose_connections": args["problemsOnly"] = true; return Buildings(args);
                case "save_checkpoint": return SaveCheckpoint(args);
                case "batch_execute": return StartBatch(args);
                case "get_batch": return BatchStatus(args);
                case "zone_rectangle": RequireControl(); return StartZone(args);
                case "clear_zoning": RequireControl(); args["dezone"] = true; return StartZone(args);
                case "get_operation": return ConstructionAccess.Status((string)args["id"]);
                case "set_camera": return SetCamera(args);
                case "set_simulation_speed":
                {
                    World world = RequireCity();
                    RequireControl();
                    float speed = RequiredFloat(args, "speed");
                    if (speed != 0 && speed != 1 && speed != 2 && speed != 4)
                        throw new ArgumentException("speed must be 0, 1, 2, or 4");
                    var simulation = world.GetExistingSystemManaged<SimulationSystem>();
                    if (simulation == null) throw new InvalidOperationException("simulation_unavailable");
                    if(SimulationRunning) FinishSimulation("speed_changed");
                    if(speed!=0 && (ConstructionAccess.Active!=null || (string)batch?["status"]=="running")) throw new InvalidOperationException("finish_construction_before_resuming");
                    float previous = simulation.selectedSpeed;
                    simulation.selectedSpeed = speed;
                    return new JObject { ["previousSpeed"] = previous, ["selectedSpeed"] = simulation.selectedSpeed };
                }
                default: throw new ArgumentException("unknown_command");
            }
        }

        private JObject CityState()
        {
            World world = RequireCity();
            var city = world.GetExistingSystemManaged<CitySystem>();
            var config = world.GetExistingSystemManaged<CityConfigurationSystem>();
            var simulation = world.GetExistingSystemManaged<SimulationSystem>();
            var time = world.GetExistingSystemManaged<TimeSystem>();
            if (city == null || city.City == Entity.Null || !world.EntityManager.Exists(city.City))
                throw new InvalidOperationException("city_entity_unavailable");
            var em = world.EntityManager;
            JObject result = new JObject
            {
                ["cityName"] = config?.cityName, ["money"] = city.moneyAmount,
                ["xp"] = city.XP, ["simulationFrame"] = simulation?.frameIndex,
                ["selectedSpeed"] = simulation?.selectedSpeed,
                ["date"] = time?.GetCurrentDateTime().ToString("O"),
                ["controlEnabled"] = settings.AllowControl, ["citySession"] = citySession
            };
            if (em.HasComponent<Population>(city.City))
            {
                var population = em.GetComponentData<Population>(city.City);
                result["population"] = population.m_Population;
                result["populationWithMoveIn"] = population.m_PopulationWithMoveIn;
                result["averageHappiness"] = population.m_AverageHappiness;
                result["averageHealth"] = population.m_AverageHealth;
            }
            return result;
        }

        private static CameraController Camera()
        {
            if (!CameraController.TryGet(out var camera) || camera == null)
                throw new InvalidOperationException("camera_unavailable");
            return camera;
        }

        private static JObject CameraState()
        {
            var camera = Camera();
            return new JObject
            {
                ["pivot"] = Vector(camera.pivot), ["position"] = Vector(camera.position),
                ["angle"] = new JArray(camera.angle.x, camera.angle.y), ["zoom"] = camera.zoom
            };
        }

        private JObject SetCamera(JObject args)
        {
            RequireCity();
            RequireControl();
            var camera = Camera();
            var before = CameraState();
            // Validate all supplied values before changing anything.
            Vector3 pivot = camera.pivot;
            if (args["pivot"] is JObject p)
            {
                pivot = new Vector3(RequiredFloat(p, "x"), RequiredFloat(p, "y"), RequiredFloat(p, "z"));
                if (Math.Abs(pivot.x) > 15000 || Math.Abs(pivot.z) > 15000 || pivot.y < -1000 || pivot.y > 5000)
                    throw new ArgumentException("pivot_out_of_bounds");
            }
            float zoom = args["zoom"] == null ? camera.zoom : RequiredFloat(args, "zoom");
            var range = camera.zoomRange;
            if (zoom < range.min || zoom > range.max) throw new ArgumentException("zoom_out_of_range");
            camera.pivot = pivot;
            camera.zoom = zoom;
            return new JObject { ["before"] = before, ["after"] = CameraState(), ["note"] = "Controller target updated; rendering may interpolate on subsequent frames." };
        }

        private JObject WaterFacilities()
        {
            World world = RequireCity();
            var em = world.EntityManager;
            using (var query = em.CreateEntityQuery(new EntityQueryDesc
            {
                Any = new[] { ComponentType.ReadOnly<Game.Buildings.WaterPumpingStation>(), ComponentType.ReadOnly<Game.Buildings.SewageOutlet>() },
                None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() }
            }))
            using (var entities = query.ToEntityArray(Allocator.Temp))
            {
                var items = new JArray();
                for (int i = 0; i < entities.Length && i < 128; ++i) items.Add(Inspect(world, entities[i]));
                return new JObject { ["total"] = entities.Length, ["truncated"] = entities.Length > 128, ["facilities"] = items };
            }
        }

        private static JObject Inspect(World world, Entity entity)
        {
            var em = world.EntityManager;
            if (entity == Entity.Null || !em.Exists(entity)) throw new InvalidOperationException("entity_not_found_or_stale");
            JObject result = new JObject { ["index"] = entity.Index, ["version"] = entity.Version };
            var prefabs = world.GetExistingSystemManaged<PrefabSystem>();
            if (prefabs != null && em.HasComponent<PrefabRef>(entity))
            {
                var prefab = em.GetComponentData<PrefabRef>(entity);
                result["prefab"] = prefabs.GetPrefabName(prefab.m_Prefab);
            }
            if (em.HasComponent<Game.Objects.Transform>(entity))
            {
                var transform = em.GetComponentData<Game.Objects.Transform>(entity);
                result["position"] = Vector(transform.m_Position);
                result["rotationQuaternion"] = new JArray(transform.m_Rotation.value.x, transform.m_Rotation.value.y, transform.m_Rotation.value.z, transform.m_Rotation.value.w);
            }
            if (em.HasComponent<Game.Net.Node>(entity)) result["position"] = Vector(em.GetComponentData<Game.Net.Node>(entity).m_Position);
            using (var types = em.GetComponentTypes(entity, Allocator.Temp))
                result["components"] = new JArray(types.Select(t => t.GetManagedType().FullName));
            if (em.HasComponent<Game.Buildings.WaterConsumer>(entity))
            {
                var w = em.GetComponentData<Game.Buildings.WaterConsumer>(entity);
                result["waterConsumer"] = new JObject
                {
                    ["wantedConsumption"] = w.m_WantedConsumption, ["fulfilledFresh"] = w.m_FulfilledFresh,
                    ["fulfilledSewage"] = w.m_FulfilledSewage, ["pollution"] = w.m_Pollution, ["flags"] = w.m_Flags.ToString()
                };
            }
            if (em.HasComponent<Game.Buildings.WaterPumpingStation>(entity))
            {
                var w = em.GetComponentData<Game.Buildings.WaterPumpingStation>(entity);
                result["waterProducer"] = new JObject { ["capacityRaw"] = w.m_Capacity, ["lastProductionRaw"] = w.m_LastProduction, ["pollution"] = w.m_Pollution };
            }
            if (em.HasComponent<Game.Buildings.SewageOutlet>(entity))
            {
                var w = em.GetComponentData<Game.Buildings.SewageOutlet>(entity);
                result["sewageOutlet"] = new JObject { ["capacityRaw"] = w.m_Capacity, ["lastProcessedRaw"] = w.m_LastProcessed, ["lastPurifiedRaw"] = w.m_LastPurified };
            }
            return result;
        }

        private static JObject Vector(Vector3 value) => new JObject { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z };
        private static JObject Vector(float3 value) => new JObject { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z };
        private static int RequiredInt(JObject value, string key)
        {
            if (value[key]?.Type != JTokenType.Integer) throw new ArgumentException("integer_required: " + key);
            return (int)value[key];
        }
        private static float RequiredFloat(JObject value, string key)
        {
            if (value[key]?.Type != JTokenType.Float && value[key]?.Type != JTokenType.Integer)
                throw new ArgumentException("number_required: " + key);
            float number = (float)value[key];
            if (float.IsNaN(number) || float.IsInfinity(number)) throw new ArgumentException("finite_number_required: " + key);
            return number;
        }
    }
}
