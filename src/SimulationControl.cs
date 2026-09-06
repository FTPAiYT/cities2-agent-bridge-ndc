using System;
using System.Diagnostics;
using System.Linq;
using Unity.Entities;
using Game.Simulation;
using Newtonsoft.Json.Linq;

namespace CitiesIIAgentBridge
{
    public sealed partial class Mod
    {
        private SimulationWindow simulationWindow;
        private Stopwatch simulationClock;
        private string simulationOperation, simulationCity;
        private JObject simulationBefore, simulationOptions;
        private int stagnantSteps;
        private double nextSimulationSample;

        private bool SimulationRunning => simulationWindow != null;
        private void PauseAnalysis()
        {
            var w = RequireCity(); RequireControl();
            if (SimulationRunning) FinishSimulation("analysis_requested");
            w.GetExistingSystemManaged<SimulationSystem>().selectedSpeed = 0;
        }
        private JObject BeginSimulation(JObject args)
        {
            var w = RequireCity(); RequireControl();
            if (SimulationRunning || ConstructionAccess.Active != null || (string)batch?["status"] == "running")
                throw new InvalidOperationException("finish_current_operation_before_simulating");
            if (stagnantSteps >= 3 && (bool?)args["acknowledgeNoProgress"] != true)
                throw new InvalidOperationException("reassess_required_after_three_stagnant_steps");
            int frames = args["frames"] == null ? 4096 : RequiredInt(args,"frames");
            double wall = args["wallSeconds"] == null ? 30 : RequiredFloat(args,"wallSeconds");
            double stall = args["stallSeconds"] == null ? Math.Min(5,wall) : RequiredFloat(args,"stallSeconds");
            float speed = args["speed"] == null ? 4 : RequiredFloat(args,"speed");
            if (speed != 1 && speed != 2 && speed != 4) throw new ArgumentException("step_speed_must_be_1_2_or_4");
            if (frames < 1) throw new ArgumentException("frames_must_be_positive");
            if (args["populationChange"] != null && RequiredInt(args,"populationChange") < 1) throw new ArgumentException("populationChange_must_be_positive");
            var sim = w.GetExistingSystemManaged<SimulationSystem>();
            var window = new SimulationWindow(sim.frameIndex,(ulong)frames,wall,stall);
            sim.selectedSpeed = 0;
            simulationBefore = Observation(); simulationOptions = (JObject)args.DeepClone();
            simulationOperation = ConstructionAccess.Begin("simulation_step"); simulationCity = citySession;
            simulationWindow = window; simulationClock = Stopwatch.StartNew(); nextSimulationSample = 0;
            if ((bool?)args["acknowledgeNoProgress"] == true) stagnantSteps = 0;
            var result = ConstructionAccess.Results[simulationOperation];
            result["status"] = "running"; result["before"] = simulationBefore;
            result["bounds"] = new JObject { ["frames"] = frames, ["wallSeconds"] = wall, ["stallSeconds"] = stall };
            sim.selectedSpeed = speed;
            return ConstructionAccess.Status(simulationOperation);
        }
        private void SimulationTick()
        {
            if (!SimulationRunning) return;
            try
            {
                if (simulationCity != citySession) { FinishSimulation("city_changed", false); return; }
                var sim = RequireCity().GetExistingSystemManaged<SimulationSystem>();
                double elapsed = simulationClock.Elapsed.TotalSeconds;
                string reason = simulationWindow.Check(sim.frameIndex,elapsed,ConstructionAccess.Allowed?.Invoke() == true,true,sim.selectedSpeed == 0,null);
                if (reason == null && elapsed >= nextSimulationSample)
                {
                    nextSimulationSample = elapsed + 1;
                    var now = Observation();
                    if (simulationOptions["cashFloor"] != null && (long)now["city"]["money"] < (long)simulationOptions["cashFloor"]) reason = "cash_floor";
                    else if (simulationOptions["populationChange"] != null && Math.Abs((int)now["city"]["population"]-(int)simulationBefore["city"]["population"]) >= (int)simulationOptions["populationChange"]) reason = "population_changed";
                    else if ((bool?)simulationOptions["stopOnDemandChange"] == true && !JToken.DeepEquals(now["demand"],simulationBefore["demand"])) reason = "demand_changed";
                    else if ((bool?)simulationOptions["stopOnNewShortage"] != false && now["shortageEntities"].Values<string>().Except(simulationBefore["shortageEntities"].Values<string>()).Any()) reason = "new_utility_shortage";
                    else if ((bool?)simulationOptions["stopOnConstructionComplete"] == true && ((JArray)simulationBefore["constructionEntities"]).Any(item=> {
                        var e=new Entity{Index=(int)item["index"],Version=(int)item["version"]};var em=RequireCity().EntityManager;
                        return em.Exists(e)&&em.HasComponent<Game.Buildings.Building>(e)&&!em.HasComponent<Game.Common.Deleted>(e)&&!em.HasComponent<Game.Objects.UnderConstruction>(e);
                    })) reason = "construction_completed";
                    ConstructionAccess.Results[simulationOperation]["latest"] = now;
                }
                if (reason != null) FinishSimulation(reason);
            }
            catch (Exception e) { FinishSimulation("observation_failed: " + e.GetBaseException().Message); }
        }
        private JObject EndSimulation(JObject args)
        {
            RequireCity(); RequireControl();
            if (SimulationRunning) FinishSimulation("cancelled");
            else PauseAnalysis();
            return simulationOperation == null ? new JObject { ["paused"] = true } : ConstructionAccess.Status(simulationOperation);
        }
        private void FinishSimulation(string reason, bool pause = true)
        {
            if (!SimulationRunning) return;
            var id = simulationOperation; var result = ConstructionAccess.Results[id];
            try
            {
                if (pause && simulationCity == citySession)
                {
                    var sim = RequireCity().GetExistingSystemManaged<SimulationSystem>(); sim.selectedSpeed = 0;
                    result["paused"] = true;
                    result["advancedFrames"] = sim.frameIndex >= simulationWindow.StartFrame ? (ulong)sim.frameIndex-simulationWindow.StartFrame : 0;
                    var after = Observation(); result["after"] = after;
                    var delta = new JObject();
                    foreach (var key in new[] { "population", "money", "xp", "averageHappiness", "averageHealth" })
                        if (after["city"][key] != null && simulationBefore["city"][key] != null) delta[key] = (double)after["city"][key] - (double)simulationBefore["city"][key];
                    result["delta"] = delta;
                    bool changed = ((double?)delta["population"]??0) != 0 || ((double?)delta["xp"]??0) != 0 || !JToken.DeepEquals(after["demand"],simulationBefore["demand"]) || !JToken.DeepEquals(after["underConstruction"],simulationBefore["underConstruction"]);
                    stagnantSteps = changed ? 0 : stagnantSteps + 1;
                }
            }
            catch (Exception e) { result["snapshotError"] = e.GetBaseException().Message; }
            finally
            {
                result["reason"] = reason; result["elapsedSeconds"] = simulationClock.Elapsed.TotalSeconds;
                result["stagnantSteps"] = stagnantSteps; result["reassessmentRequired"] = stagnantSteps >= 3;
                simulationWindow = null; simulationClock.Stop();
                ConstructionAccess.Finish(id, reason == "city_changed" || reason == "control_stopped" ? "interrupted" : "complete");
            }
        }
    }
}
