using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimGovernor.Host.Gab.Protocol;
using RimGovernor.Host.Gab.Server;

// The vendored GABP server must keep reading while a tool runs: a tool handler
// is synchronous in the host (it blocks until the call completes), so a held
// clock_read_events long poll would otherwise stall the connection's reader and
// every call behind it. The claim is an ordering, not a latency:
// a second call issued while the first is held is answered before the first is
// released. The held call blocks on a gate the probe opens, so nothing here
// waits on a wall-clock bound; the waits are hang guards only.
internal static class GabDispatchProbe
{
    internal static readonly TimeSpan Guard = TimeSpan.FromSeconds(60);
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    internal sealed class Client : IDisposable
    {
        // Server-pushed event frames in arrival order (the mod-log probe reads them).
        internal readonly BlockingCollection<JObject> Events = new BlockingCollection<JObject>();
        private readonly TcpClient tcp;
        private readonly NetworkStream stream;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> pending = new ConcurrentDictionary<string, TaskCompletionSource<JObject>>();

        internal Client(int port)
        {
            tcp = new TcpClient();
            tcp.Connect("127.0.0.1", port);
            stream = tcp.GetStream();
            Task.Run(ReadAsync);
        }

        internal Task<JObject> Send(string method, object parameters)
        {
            var request = new GabpRequest { Method = method, Params = parameters };
            var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[request.Id] = completion;
            var body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(request));
            var header = Encoding.UTF8.GetBytes("Content-Length: " + body.Length + "\r\nContent-Type: application/json\r\n\r\n");
            lock (stream)
            {
                stream.Write(header, 0, header.Length);
                stream.Write(body, 0, body.Length);
                stream.Flush();
            }
            return completion.Task;
        }

        private async Task ReadAsync()
        {
            try
            {
                var buffer = new List<byte>();
                var chunk = new byte[8192];
                while (true)
                {
                    var read = await stream.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false);
                    if (read == 0) break;
                    for (var i = 0; i < read; i++) buffer.Add(chunk[i]);
                    while (TryFrame(buffer, out var message))
                    {
                        if ((string)message["type"] == "event") Events.Add(message);
                        else if (message["id"] != null && pending.TryRemove((string)message["id"], out var completion))
                            completion.TrySetResult(message);
                    }
                }
            }
            catch (Exception) { /* closing */ }
            foreach (var completion in pending.Values) completion.TrySetCanceled();
        }

        private static bool TryFrame(List<byte> buffer, out JObject message)
        {
            message = null;
            var text = Encoding.ASCII.GetString(buffer.ToArray(), 0, Math.Min(buffer.Count, 256));
            var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) return false;
            var marker = text.IndexOf("Content-Length:", StringComparison.OrdinalIgnoreCase);
            var lineEnd = text.IndexOf('\r', marker);
            var length = int.Parse(text.Substring(marker + "Content-Length:".Length, lineEnd - marker - "Content-Length:".Length).Trim());
            if (buffer.Count < headerEnd + 4 + length) return false;
            message = JObject.Parse(Encoding.UTF8.GetString(buffer.GetRange(headerEnd + 4, length).ToArray()));
            buffer.RemoveRange(0, headerEnd + 4 + length);
            return true;
        }

        public void Dispose() { tcp.Close(); }
    }

    internal static JObject Settle(Task<JObject> task, string name)
    {
        Check(task.Wait(Guard), name + " is answered within the hang guard");
        return task.Result;
    }

    internal static void Invoke()
    {
        const string token = "probe-token";
        using (var entered = new ManualResetEventSlim(false))
        using (var release = new ManualResetEventSlim(false))
        {
            var server = new GabpServer(new GabpServerConfig { Port = 0, Token = token });
            // Synchronous handlers, as the host registers them: the thread that runs
            // the handler is held until it returns.
            server.Tools.RegisterTool("probe/hold", _ =>
            {
                entered.Set();
                if (!release.Wait(Guard)) throw new TimeoutException("held call never released");
                return Task.FromResult<object>("held");
            });
            server.Tools.RegisterTool("probe/fast", _ => Task.FromResult<object>("probe/fast"));
            server.Tools.RegisterTool("probe/fail", _ => throw new InvalidOperationException("tool failed"));
            server.StartAsync().GetAwaiter().GetResult();
            try
            {
                using (var client = new Client(server.Port))
                {
                    var hello = Settle(client.Send("session/hello", new Dictionary<string, object> { ["token"] = token, ["bridgeVersion"] = "probe", ["platform"] = "probe", ["launchId"] = "probe" }), "hello");
                    Check(hello["error"] == null || hello["error"].Type == JTokenType.Null, "handshake accepted");

                    var held = client.Send("tools/call", new Dictionary<string, object> { ["name"] = "probe/hold", ["arguments"] = new Dictionary<string, object>() });
                    Check(entered.Wait(Guard), "the held call reached its handler");
                    Check(!held.IsCompleted, "the held call has not answered while its handler runs");

                    // Behind the held call: a fast call, a failing call, an unknown tool.
                    var fast = Settle(client.Send("tools/call", new Dictionary<string, object> { ["name"] = "probe/fast", ["arguments"] = new Dictionary<string, object>() }), "fast call under the held call");
                    Check((string)fast["result"] == "probe/fast", "the fast call carries its result");
                    Check(!held.IsCompleted, "the fast call was answered before the held call was released");
                    var failed = Settle(client.Send("tools/call", new Dictionary<string, object> { ["name"] = "probe/fail", ["arguments"] = new Dictionary<string, object>() }), "failing call under the held call");
                    Check(failed["error"] != null && failed["error"].Type != JTokenType.Null && ((string)failed["error"]["message"]).Contains("tool failed"), "a throwing tool answers an error under the held call");
                    var unknown = Settle(client.Send("tools/call", new Dictionary<string, object> { ["name"] = "probe/absent", ["arguments"] = new Dictionary<string, object>() }), "unknown tool under the held call");
                    Check(unknown["error"] != null && unknown["error"].Type != JTokenType.Null, "an unknown tool answers an error under the held call");
                    Check(!held.IsCompleted, "no later call released the held call");

                    release.Set();
                    var answer = Settle(held, "held call after release");
                    Check((string)answer["result"] == "held", "the held call answers once released");
                }
            }
            finally
            {
                release.Set();
                server.Dispose();
            }
        }
        Console.WriteLine("gab-dispatch: " + checks + " checks passed");
    }
}
