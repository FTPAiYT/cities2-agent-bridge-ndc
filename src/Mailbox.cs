using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CitiesIIAgentBridge
{
    // A local file mailbox. No listening socket, shell execution, or arbitrary reflection commands.
    public sealed class Mailbox
    {
        public const int Protocol = 1;
        public readonly string Session = Guid.NewGuid().ToString("N");
        public readonly string Root;
        public readonly string Requests;
        public readonly string Responses;
        private readonly Func<string, JObject, JObject> dispatch;
        private readonly Func<string> citySession;
        private readonly Dictionary<string, JObject> pendingResponses = new Dictionary<string, JObject>();

        public Mailbox(string root, Func<string, JObject, JObject> dispatch, Func<string> citySession)
        {
            Root = root;
            Requests = Path.Combine(root, "requests");
            Responses = Path.Combine(root, "responses");
            this.dispatch = dispatch;
            this.citySession = citySession;
            Directory.CreateDirectory(Requests);
            Directory.CreateDirectory(Responses);
        }

        public void Publish(JObject state)
        {
            state["protocol"] = Protocol;
            state["session"] = Session;
            state["citySession"] = citySession();
            state["heartbeatUtc"] = DateTime.UtcNow.ToString("O");
            AtomicWrite(Path.Combine(Root, "session.json"), state);
        }

        public int Pump()
        {
            int count = 0;
            foreach (string path in Directory.EnumerateFiles(Requests, "*.json").Take(4))
            {
                string id = Path.GetFileNameWithoutExtension(path);
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                string responsePath = Path.Combine(Responses, id + ".json");
                if (pendingResponses.TryGetValue(id, out var pending))
                {
                    AtomicWrite(responsePath, pending);
                    pendingResponses.Remove(id);
                    File.Delete(path);
                    continue;
                }
                // Completed request IDs are never executed twice, even if resubmitted.
                if (File.Exists(responsePath)) { File.Delete(path); continue; }
                JObject response = new JObject
                {
                    ["protocol"] = Protocol, ["id"] = id, ["session"] = Session,
                    ["citySession"] = citySession(), ["utc"] = DateTime.UtcNow.ToString("O")
                };
                try
                {
                    if (new FileInfo(path).Length > 16384) throw new InvalidOperationException("request_too_large");
                    string requestText;
                    try { requestText = ReadSharedText(path); }
                    catch (IOException e) when (IsSharingViolation(e)) { continue; }
                    JObject request;
                    using (var reader = new JsonTextReader(new StringReader(requestText)))
                    {
                        reader.DateParseHandling = DateParseHandling.None;
                        request = JObject.Load(reader);
                    }
                    if ((string)request["id"] != id) throw new InvalidOperationException("id_mismatch");
                    if ((int?)request["protocol"] != Protocol) throw new InvalidOperationException("protocol_mismatch");
                    if ((string)request["session"] != Session) throw new InvalidOperationException("stale_session");
                    var expiry = DateTimeOffset.Parse((string)request["expiresUtc"] ?? "", System.Globalization.CultureInfo.InvariantCulture);
                    if (expiry <= DateTimeOffset.UtcNow || expiry > DateTimeOffset.UtcNow.AddSeconds(60))
                        throw new InvalidOperationException("request_expired_or_invalid_deadline");
                    string command = (string)request["command"];
                    if (command != "ping" && command != "get_city_state" && command != "get_capabilities" &&
                        (string)request["citySession"] != citySession())
                        throw new InvalidOperationException("stale_city_session");
                    response["result"] = dispatch(command, request["args"] as JObject ?? new JObject());
                    response["ok"] = true;
                }
                catch (Exception e)
                {
                    response["ok"] = false;
                    response["error"] = e.GetType().Name + ": " + e.Message;
                }
                // Keep the response until the client explicitly cleans its mailbox.
                // Retain the result if publishing fails; do not dispatch again in this process.
                // A restarted process has a new session and rejects the old request.
                pendingResponses[id] = response;
                AtomicWrite(responsePath, response);
                pendingResponses.Remove(id);
                File.Delete(path);
                ++count;
            }
            return count;
        }

        public static void AtomicWrite(string path, JObject value)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, value.ToString(Formatting.Indented), new UTF8Encoding(false));
                RetrySharingViolation(() => {
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                });
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        internal static bool IsSharingViolation(IOException error)
        {
            // Windows ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION only.
            // Disk, permission and other I/O faults must not be mistaken for contention.
            int code = error.HResult & 0xffff;
            return code == 32 || code == 33;
        }

        internal static void RetrySharingViolation(Action action)
        {
            // Bound main-thread blocking to three 10ms waits; longer locks retry next tick.
            for (int attempt = 0; ; ++attempt)
            {
                try { action(); return; }
                catch (IOException e) when (IsSharingViolation(e) && attempt < 3)
                { Thread.Sleep(10); }
            }
        }

        internal static string ReadSharedText(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                return reader.ReadToEnd();
        }
    }
}
