using System;
using System.Diagnostics;
using System.IO;
using CitiesIIAgentBridge;
using Newtonsoft.Json.Linq;

internal static class RecoveryTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); ++passed; Console.WriteLine("PASS: " + label); }
        if (!OperatingSystem.IsWindows()) throw new Exception("Recovery tests require Windows sharing semantics.");
        var root = Path.Combine(Path.GetTempPath(), "CitiesIIAgentBridge-recovery-" + Guid.NewGuid().ToString("N"));
        var box = new Mailbox(root, (c, a) => new JObject(), () => "city-a");
        var heartbeat = Path.Combine(root, "session.json");
        box.Publish(new JObject { ["status"] = "old" });
        using (var reader = new FileStream(heartbeat, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            box.Publish(new JObject { ["status"] = "new" });
            Check((string)JObject.Parse(Mailbox.ReadSharedText(heartbeat))["status"] == "new", "delete-sharing reader permits atomic heartbeat replacement");
            using (var old = new StreamReader(reader))
                Check((string)JObject.Parse(old.ReadToEnd())["status"] == "old", "reader sees complete old snapshot during atomic replacement");
        }

        int attempts = 0;
        var sharing = new IOException("sharing", unchecked((int)0x80070020));
        var locked = new IOException("lock", unchecked((int)0x80070021));
        var diskFull = new IOException("disk full", unchecked((int)0x80070070));
        Mailbox.RetrySharingViolation(() => { if (++attempts < 3) throw sharing; });
        Check(attempts == 3 && Mailbox.IsSharingViolation(locked), "temporary sharing and lock violations are retryable");
        attempts = 0;
        var timer = Stopwatch.StartNew();
        try { Mailbox.RetrySharingViolation(() => { ++attempts; throw sharing; }); } catch (IOException) { }
        Check(attempts == 4 && timer.Elapsed.TotalSeconds < 1, "persistent lock has bounded retries instead of blocking the game thread");
        attempts = 0;
        try { Mailbox.RetrySharingViolation(() => { ++attempts; throw diskFull; }); } catch (IOException) { }
        Check(attempts == 1 && !Mailbox.IsSharingViolation(diskFull), "non-contention I/O faults are not retried or hidden");

        bool allowed = true, paused = false;
        int ticks = 0, pumps = 0, workflows = 0, warnings = 0;
        var window = new SimulationWindow(0, 1000, 3, 3);
        string stop = Path.Combine(root, "STOP");
        Action enforce = () => { if (File.Exists(stop)) allowed = false; };
        Action simulate = () => { ++ticks; if (window.Check((ulong)ticks, ticks, allowed, true, false, null) != null) paused = true; };
        Action publish = () => box.Publish(new JObject { ["status"] = "ready", ["controlEnabled"] = allowed });
        Func<bool> tick = () => BridgeTick.Run(enforce, simulate, publish, () => ++pumps, () => ++workflows, e => ++warnings);
        using (var blocker = new FileStream(heartbeat, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Check(!tick() && !tick() && !tick(), "real Windows reader lock defers heartbeat publication across ticks");
            Check(allowed && paused && ticks == 3 && pumps == 0 && workflows == 0 && warnings == 3,
                "persistent heartbeat lock preserves permission and still enforces simulation deadline");
        }
        Check(tick() && allowed && pumps == 1 && workflows == 1,
            "heartbeat and command processing recover automatically after lock release");
        Check(Directory.GetFiles(root, "*.tmp").Length == 0, "failed atomic replacements clean temporary files");

        paused = false;
        using (var blocker = new FileStream(heartbeat, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            File.WriteAllText(stop, "stop");
            tick();
            Check(!allowed && paused, "STOP is enforced while heartbeat is locked");
        }
        File.Delete(stop);
        tick();
        Check(!allowed && !(bool)JObject.Parse(Mailbox.ReadSharedText(heartbeat))["controlEnabled"],
            "transport recovery never re-enables a stopped session");
        allowed = false; // The same state used by the option switch and city preload.
        tick();
        Check(!allowed, "manual off or city-load revocation stays off on healthy ticks");

        foreach (string stage in new[] { "simulation", "workflow", "publish", "pump" })
        {
            bool propagated = false;
            Action fail = () => { throw (stage == "simulation" || stage == "workflow" ? sharing : diskFull); };
            try {
                BridgeTick.Run(() => { }, stage == "simulation" ? fail : () => { },
                    stage == "publish" ? fail : () => { }, stage == "pump" ? fail : () => { },
                    stage == "workflow" ? fail : () => { }, e => { });
            } catch (IOException) { propagated = true; }
            Check(propagated, stage + " genuine fault reaches the existing disable-control handler");
        }

        JObject Request(Mailbox b) => new JObject {
            ["id"] = Guid.NewGuid().ToString("N"), ["protocol"] = 1, ["session"] = b.Session,
            ["citySession"] = "city-a", ["expiresUtc"] = DateTime.UtcNow.AddSeconds(30).ToString("O"),
            ["command"] = "set_camera", ["args"] = new JObject()
        };
        int calls = 0;
        FileStream responseLock = null;
        string responsePath = null;
        var responseBox = new Mailbox(Path.Combine(root, "response"), (c, a) => {
            ++calls;
            // Lock the destination after dispatch, before the atomic result publication.
            responseLock = new FileStream(responsePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            return new JObject { ["operation"] = "exactly-once" };
        }, () => "city-a");
        var request = Request(responseBox);
        responsePath = Path.Combine(responseBox.Responses, (string)request["id"] + ".json");
        string requestPath = Path.Combine(responseBox.Requests, (string)request["id"] + ".json");
        Mailbox.AtomicWrite(requestPath, request);
        Func<bool> responseTick = () => BridgeTick.Run(() => { }, () => { },
            () => responseBox.Publish(new JObject()), () => responseBox.Pump(), () => { }, e => { });
        try {
            Check(!responseTick() && calls == 1, "response lock after a mutation retains its result");
            Check(!responseTick() && calls == 1 && File.Exists(requestPath), "locked result retry does not redispatch the mutation");
        } finally { responseLock?.Dispose(); }
        Check(responseTick() && calls == 1 && !File.Exists(requestPath) &&
            (string)JObject.Parse(Mailbox.ReadSharedText(responsePath))["result"]["operation"] == "exactly-once",
            "result is delivered once when response lock clears");

        var cleanupBox = new Mailbox(Path.Combine(root, "cleanup"), (c, a) => { ++calls; return new JObject(); }, () => "city-a");
        var cleanup = Request(cleanupBox);
        string cleanupPath = Path.Combine(cleanupBox.Requests, (string)cleanup["id"] + ".json");
        Mailbox.AtomicWrite(cleanupPath, cleanup);
        int previousCalls = calls;
        using (var blocker = new FileStream(cleanupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            for (int i = 0; i < 2; ++i) {
                try { cleanupBox.Pump(); } catch (IOException e) when (Mailbox.IsSharingViolation(e)) { }
            }
            Check(calls == previousCalls + 1, "request cleanup lock cannot replay a completed command");
        }
        cleanupBox.Pump();
        Check(calls == previousCalls + 1 && !File.Exists(cleanupPath), "request cleanup recovers after reader releases lock");
        var blocked = Request(cleanupBox);
        string blockedPath = Path.Combine(cleanupBox.Requests, (string)blocked["id"] + ".json");
        Mailbox.AtomicWrite(blockedPath, blocked);
        previousCalls = calls;
        using (var blocker = new FileStream(blockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            cleanupBox.Pump();
            Check(calls == previousCalls && File.Exists(blockedPath), "exclusively locked request is deferred without dispatch");
        }
        cleanupBox.Pump();
        Check(calls == previousCalls + 1, "request dispatch resumes after exclusive lock release");
        Console.WriteLine("Recovery evidence: " + root);
        return passed;
    }
}
