using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using CitiesIIAgentBridge;
using Newtonsoft.Json.Linq;

internal static class MailboxClientTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); ++passed; Console.WriteLine("PASS: " + label); }
        string client = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../bridge.ps1"));
        foreach (bool permanent in new[] { false, true })
        {
            string root = Path.Combine(Path.GetTempPath(), "CitiesIIAgentBridge-client-lock-" + Guid.NewGuid().ToString("N"));
            var box = new Mailbox(root, (c, a) => throw new Exception("Fake server must not dispatch to a game"), () => "city-test");
            box.Publish(new JObject { ["status"] = "ready", ["controlEnabled"] = true });
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("CIAB_TEST_PWSH") ?? "pwsh") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoProfile", "-File", client, "set_camera", "-TimeoutSeconds", permanent ? "1" : "5", "-MailboxPath", root })
                start.ArgumentList.Add(argument);
            using (var process = Process.Start(start))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                try
                {
                    string requestPath = null;
                    var deadline = Stopwatch.StartNew();
                    while (requestPath == null && deadline.Elapsed.TotalSeconds < 10 && !process.HasExited)
                    {
                        requestPath = Directory.GetFiles(box.Requests, "*.json").SingleOrDefault();
                        if (requestPath == null) Thread.Sleep(10);
                    }
                    Check(requestPath != null, "real client sends one request to isolated fake mailbox");
                    var request = JObject.Parse(Mailbox.ReadSharedText(requestPath));
                    string responsePath = Path.Combine(box.Responses, (string)request["id"] + ".json");
                    var response = new JObject { ["id"] = request["id"], ["ok"] = true,
                        ["result"] = new JObject { ["status"] = "complete", ["marker"] = "fake-response" } };
                    using (var blocker = new FileStream(responsePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    {
                        var bytes = Encoding.UTF8.GetBytes(response.ToString());
                        blocker.Write(bytes, 0, bytes.Length);
                        blocker.Flush();
                        if (permanent)
                            Check(process.WaitForExit(7000) && process.ExitCode != 0, "persistent response lock exits with bounded unknown-outcome error");
                        else
                        {
                            Thread.Sleep(500);
                            Check(!process.HasExited, "temporary response lock keeps real client polling instead of failing");
                        }
                    }
                    Check(process.WaitForExit(5000), "real mailbox client finishes within its deadline");
                    string output = stdout.GetAwaiter().GetResult(), error = stderr.GetAwaiter().GetResult();
                    if (permanent)
                        Check(error.Contains((string)request["id"]) && error.Contains("inspect before retrying"), "timeout identifies the original request and uncertain outcome");
                    else
                        Check(process.ExitCode == 0 && (string)JObject.Parse(output)["result"]["marker"] == "fake-response",
                            "real PowerShell client receives the original result after lock release: " + error);
                    Check(Directory.GetFiles(box.Requests, "*.json").Length == 1 && !File.Exists(Path.Combine(root, "STOP")),
                        "response retries never resubmit a mutation or alter the user's control latch");
                }
                finally { if (!process.HasExited) process.Kill(true); }
            }
        }
        return passed;
    }
}
