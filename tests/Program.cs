using System;
using System.IO;
using CitiesIIAgentBridge;
using Newtonsoft.Json.Linq;

string root = Path.Combine(Path.GetTempPath(), "CitiesIIAgentBridge-tests-" + Guid.NewGuid().ToString("N"));
int calls = 0, passed = 0;
var box = new Mailbox(root, (command, args) => {
    ++calls;
    if (command != "ping" && command != "set_camera") throw new InvalidOperationException("unknown_command");
    return new JObject { ["pong"] = true };
}, () => "city-a");
JObject Request(string command = "ping") => new JObject {
    ["id"] = Guid.NewGuid().ToString("N"), ["protocol"] = 1, ["session"] = box.Session,
    ["citySession"] = "city-a", ["expiresUtc"] = DateTime.UtcNow.AddSeconds(30).ToString("O"),
    ["command"] = command, ["args"] = new JObject()
};
JObject Send(JObject request) {
    string id = (string)request["id"];
    Mailbox.AtomicWrite(Path.Combine(box.Requests, id + ".json"), request);
    box.Pump();
    return JObject.Parse(File.ReadAllText(Path.Combine(box.Responses, id + ".json")));
}
void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); ++passed; Console.WriteLine("PASS: " + label); }
var valid = Request();
Check((bool)Send(valid)["ok"] && calls == 1, "valid round trip");
Send(valid);
Check(calls == 1, "completed request replay does not dispatch");
foreach (string field in new[] { "session", "citySession", "protocol", "expiresUtc" }) {
    var r = Request("set_camera");
    r[field] = field == "protocol" ? new JValue(999) : new JValue(field == "expiresUtc" ? DateTime.UtcNow.AddSeconds(-1).ToString("O") : "stale");
    Check(!(bool)Send(r)["ok"] && calls == 1, "reject " + field + " before dispatch");
}
Check(!(bool)Send(Request("execute_shell"))["ok"], "unknown command rejected");
var broken = Request();
File.WriteAllText(Path.Combine(box.Requests, (string)broken["id"] + ".json"), "{broken");
box.Pump();
Check(!(bool)JObject.Parse(File.ReadAllText(Path.Combine(box.Responses, (string)broken["id"] + ".json")))["ok"], "malformed JSON handled");
var retry = Request();
string retryPath = Path.Combine(box.Responses, (string)retry["id"] + ".json");
Directory.CreateDirectory(retryPath); // Force response publication to fail after dispatch.
int before = calls;
try { Send(retry); } catch (IOException) {} catch (UnauthorizedAccessException) {}
Check(calls == before + 1, "publication failure occurs after one dispatch");
Directory.Delete(retryPath);
box.Pump();
Check(calls == before + 1 && File.Exists(retryPath), "publication retry does not repeat action");
box.Publish(new JObject { ["status"] = "ready" });
box.Publish(new JObject { ["status"] = "ready" });
Check((string)JObject.Parse(File.ReadAllText(Path.Combine(root, "session.json")))["session"] == box.Session, "heartbeat replacement");
Console.WriteLine($"{passed} checks passed. Test files: {root}");
Console.WriteLine($"{PolicyTests.Run()} simulation and geometry checks passed.");
Console.WriteLine($"{ObjectPlacementTests.Run()} object placement safety checks passed.");
Console.WriteLine($"{RecoveryTests.Run()} mailbox recovery checks passed.");
Console.WriteLine($"{MailboxClientTests.Run()} real PowerShell mailbox client checks passed.");
