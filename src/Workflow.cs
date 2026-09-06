using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Colossal.IO.AssetDatabase;
using Game.SceneFlow;
using Game.Tools;
using Game.UI;
using Game.UI.Menu;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CitiesIIAgentBridge
{
    public sealed partial class Mod
    {
        private Task<bool> saveTask;
        private string saveOperation;
        private RenderTexture savePreview;
        private JObject batch;
        private JArray batchSteps;
        private int batchIndex;
        private string waitingOperation;
        private bool dispatchingBatch;
        private long batchStartMoney, batchReserve;
        private DateTime batchDeadline;
        private static readonly string[] batchCommands = { "build_road", "build_network", "upgrade_network", "purchase_tiles", "zone_rectangle", "clear_zoning", "place_building", "relocate_building", "demolish", "set_tax", "set_service_budget", "save_checkpoint", "set_simulation_speed" };

        private JObject SaveCheckpoint(JObject args)
        {
            var world = RequireCity(); RequireControl();
            if (saveTask != null) throw new InvalidOperationException("save_in_progress");
            string label = (string)args["label"] ?? "checkpoint";
            if (label.Length > 48 || System.Text.RegularExpressions.Regex.IsMatch(label, "[^a-zA-Z0-9 _-]")) throw new ArgumentException("invalid_checkpoint_label");
            string name = "CitiesIIAgentBridge-" + label + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0,8);
            saveOperation = ConstructionAccess.Begin("save");
            try
            {
                savePreview = ScreenCaptureHelper.CreateRenderTarget("CitiesIIAgentBridge-save", 680, 383);
                ScreenCaptureHelper.CaptureScreenshot(UnityEngine.Camera.main, savePreview, new MenuHelpers.SaveGamePreviewSettings());
                var info = world.GetExistingSystemManaged<MenuUISystem>().GetSaveInfo(autoSave: false);
                saveTask = GameManager.instance.Save(name, info, AssetDatabase.user, savePreview);
                ConstructionAccess.Results[saveOperation]["saveName"] = name;
                return ConstructionAccess.Status(saveOperation);
            }
            catch { ConstructionAccess.Finish(saveOperation, "failed", "save_start_failed"); if (savePreview != null) UnityEngine.Object.Destroy(savePreview); saveOperation = null; throw; }
        }
        private JObject StartBatch(JObject args)
        {
            RequireCity(); RequireControl();
            PauseAnalysis();
            if (batch != null && (string)batch["status"] == "running") throw new InvalidOperationException("batch_in_progress");
            if (ConstructionAccess.Active != null) throw new InvalidOperationException("operation_in_progress");
            var steps = args["steps"] as JArray;
            if (steps == null || steps.Count < 1 || steps.Count > 64) throw new ArgumentException("batch_requires_1_to_64_steps");
            foreach (var item in steps) if (!(item is JObject s) || Array.IndexOf(batchCommands, (string)s["command"]) < 0 || !(s["args"] is JObject)) throw new ArgumentException("invalid_batch_step");
            batchSteps = (JArray)steps.DeepClone(); batchIndex = 0; waitingOperation = null;
            batchStartMoney = (long)CityState()["money"]; batchReserve=(long?)args["reserve"]??0;
            if(batchReserve<0 || batchReserve>batchStartMoney) throw new ArgumentException("invalid_cash_reserve");
            batchDeadline=DateTime.UtcNow.AddSeconds(600);
            batch = new JObject { ["id"] = Guid.NewGuid().ToString("N"), ["citySession"] = citySession, ["status"] = "running", ["steps"] = steps.Count, ["completed"] = 0, ["results"] = new JArray() };
            return (JObject)batch.DeepClone();
        }
        private JObject CancelBatch()
        {
            RequireCity();RequireControl();PauseAnalysis();
            if((string)batch?["status"]=="running")
            {
                batch["status"]="interrupted";batch["error"]="cancelled";
                InterruptBatchOperation();
                batch["note"]="Already applied changes remain. A save already in progress is allowed to finish.";
            }
            return batch==null?new JObject{["status"]="idle"}:(JObject)batch.DeepClone();
        }
        private void InterruptBatchOperation()
        {
            if(waitingOperation==null || ConstructionAccess.Active!=waitingOperation || saveOperation==waitingOperation)return;
            var w=RequireCity();
            if(tileOperation==waitingOperation)
            {
                w.GetExistingSystemManaged<Game.Simulation.MapTilePurchaseSystem>().selecting=false;
                ConstructionAccess.Finish(tileOperation,"interrupted","batch_cancelled");tileOperation=null;pendingTiles=null;
            }
            else w.GetExistingSystemManaged<ToolSystem>().activeTool=w.GetExistingSystemManaged<DefaultToolSystem>();
        }
        private JObject BatchStatus(JObject args)
        {
            if (batch == null || (string)args["id"] != (string)batch["id"]) throw new ArgumentException("batch_not_found");
            return (JObject)batch.DeepClone();
        }
        private void WorkflowTick()
        {
            if (saveTask != null && saveTask.IsCompleted)
            {
                bool success = saveTask.Status == TaskStatus.RanToCompletion && saveTask.Result;
                ConstructionAccess.Finish(saveOperation, success ? "complete" : "failed", success ? null : saveTask.Exception?.GetBaseException().Message ?? "save_failed");
                saveTask = null; saveOperation = null;
                if (savePreview != null) UnityEngine.Object.Destroy(savePreview); savePreview = null;
            }
            TileTick();
            if (batch == null || (string)batch["status"] != "running") return;
            try
            {
                RequireCity(); RequireControl();
                if ((string)batch["citySession"] != citySession) throw new InvalidOperationException("city_changed");
                if(DateTime.UtcNow>batchDeadline) throw new InvalidOperationException("batch_wall_deadline");
                if (waitingOperation != null)
                {
                    var operation = ConstructionAccess.Status(waitingOperation);
                    string status = (string)operation["status"];
                    if (status == "queued" || status == "validating" || status == "applying") return;
                    ((JArray)batch["results"]).Add(operation); waitingOperation = null;
                    if (status != "complete") throw new InvalidOperationException("batch_step_failed");
                    ++batchIndex; batch["completed"] = batchIndex;
                }
                if (batchIndex == batchSteps.Count) { batch["status"] = "complete"; batch["moneySpent"]=batchStartMoney-(long)CityState()["money"]; return; }
                var step = (JObject)batchSteps[batchIndex];
                var stepArgs=(JObject)step["args"].DeepClone();
                if(stepArgs["maxCost"]!=null)
                {
                    long remaining=(long)CityState()["money"]-batchReserve;
                    if(remaining<0)throw new InvalidOperationException("cash_reserve_reached");
                    stepArgs["maxCost"]=Math.Min((long)stepArgs["maxCost"],remaining);
                }
                JObject result;
                dispatchingBatch=true;
                try {result = Dispatch((string)step["command"], stepArgs);} finally {dispatchingBatch=false;}
                if (result["id"] != null && result["status"] != null) waitingOperation = (string)result["id"];
                else { ((JArray)batch["results"]).Add(result); ++batchIndex; batch["completed"] = batchIndex; }
            }
            catch (Exception e) { batch["status"] = "failed"; batch["error"] = e.Message; batch["failedStep"] = batchIndex; InterruptBatchOperation(); }
        }
    }
}



