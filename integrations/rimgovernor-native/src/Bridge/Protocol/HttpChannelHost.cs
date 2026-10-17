#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimGovernor.Brain.HttpChannel;
using RimGovernor.Brain.Bridge;
using RimGovernor.Host.Sdk;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The local HTTP/JSON control channel inside the mod (#3083), a test-only surface for the out-of-process acceptance
    /// harness. <c>POST /native/&lt;op&gt;</c> dispatches by an explicit table: each <see cref="NativeOps"/> entry runs its
    /// <see cref="INativeFacade"/> method on the in-process facade (the same typed call the brain makes), the
    /// harness's pull read of the newest frame runs its core here, the acceptance reads (<see cref="AcceptanceReadTools"/>) run theirs, and any other name is a tool of the host's own
    /// registry (its lifecycle tools such as <c>rimgovernor/set_time_speed</c> and the <c>test/*</c> fixtures), run with
    /// a JSON object of named arguments. The channel itself (listener, guard, routes, endpoint file) is the game-free
    /// RimGovernor.Brain.HttpChannel; this file only adapts the mod to it and starts it.
    /// </summary>
    internal sealed class NativeOpTable : INativeOpInvoker
    {
        private const string Prefix = NativeOp.ToolPrefix;
        private static readonly Dictionary<string, Func<string, CancellationToken, Task<string>>> Ops = Build();

        private static Dictionary<string, Func<string, CancellationToken, Task<string>>> Build()
        {
            var ops = new Dictionary<string, Func<string, CancellationToken, Task<string>>>(StringComparer.Ordinal);
            foreach (var op in NativeOps.All)
            {
                if (op.OnFacade) ops.Add(op.Name, (body, ct) => op.InvokeJsonAsync(InProcessNativeFacade.Instance, body, ct));
            }
            ops.Add(NativeOps.ReadSnapshot.Name, (body, ct) => NativeOps.ReadSnapshot.InvokeJsonAsync(body,
                (request, token) => ProtoBoundary.OnGameThread(NativeOps.ReadSnapshot.Tool, () => SnapshotStream.Read(request), token), ct));
            foreach (var (name, read) in AcceptanceReadTools.Ops)
                ops.Add(name, async (body, ct) => new JsonObject { ["payload"] = await read(Arguments(body), ct).ConfigureAwait(false) }.ToJsonString());
            return ops;
        }

        // An acceptance op's body: a JSON object of named arguments, empty when none.
        private static JsonObject Arguments(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return new JsonObject();
            return JsonNode.Parse(body) as JsonObject ?? throw new System.Text.Json.JsonException("The arguments must be a JSON object.");
        }

        /// <summary>The ops of the table plus every tool the host registry knows (it fills in as the host starts, so it is read on each call).</summary>
        public IReadOnlyCollection<string> Names
        {
            get
            {
                var names = new HashSet<string>(Ops.Keys, StringComparer.Ordinal);
                foreach (var name in RimBridgeToolAccess.Current?.ToolNames ?? Array.Empty<string>()) names.Add(RouteName(name));
                return names;
            }
        }

        /// <summary>The route name of a canonical tool name: <c>rimgovernor/x</c> is <c>x</c>, any other name keeps its path.</summary>
        private static string RouteName(string canonical) =>
            canonical.StartsWith(Prefix, StringComparison.Ordinal) ? canonical.Substring(Prefix.Length) : canonical;

        public Task<string> InvokeAsync(string op, string body, CancellationToken cancellationToken) =>
            Ops.TryGetValue(op, out var run) ? run(body, cancellationToken) : InvokeRegisteredAsync(op, body, cancellationToken);

        /// <summary>
        /// A host registry tool: the body is a JSON object of its arguments, the registry runs it as a wire call would,
        /// and the reply is its result as a JSON object (a non-object result is wrapped as <c>{"result": ...}</c>). The
        /// registry alias is the canonical name, which is the route name with the <c>rimgovernor/</c> prefix put back
        /// when the route had none of its own.
        /// </summary>
        private static async Task<string> InvokeRegisteredAsync(string route, string argumentsJson, CancellationToken cancellationToken)
        {
            var host = RimBridgeToolAccess.Current ?? throw new KeyNotFoundException(route);
            var canonical = host.ToolNames.Contains(route, StringComparer.Ordinal) ? route : Prefix + route;
            if (!host.ToolNames.Contains(canonical, StringComparer.Ordinal)) throw new KeyNotFoundException(route);
            var parsed = JToken.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (parsed is not JObject body) throw new ArgumentException($"{canonical} takes a JSON object of arguments.");
            var arguments = (Dictionary<string, object>)Plain(body);
            var result = await host.InvokeAsync(canonical, arguments, NativeRoutes.CallTimeout - TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var token = result == null ? new JObject() : JToken.FromObject(result);
            if (token is not JObject reply) reply = new JObject { ["result"] = token };
            return reply.ToString(Formatting.None);
        }

        /// <summary>Newtonsoft tokens as the plain values the host binder reads: objects as dictionaries, arrays as lists, scalars as themselves.</summary>
        private static object Plain(JToken token)
        {
            switch (token)
            {
                case JObject obj:
                    var map = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var property in obj.Properties()) map[property.Name] = Plain(property.Value);
                    return map;
                case JArray array:
                    return array.Select(Plain).ToList();
                case JValue value:
                    return value.Value!;
                default:
                    return token.ToString(Formatting.None);
            }
        }
    }
    /// <summary>
    /// Starts the channel when the host loads this assembly, if the controller said where its run lives:
    /// <c>RIMGOVERNOR_OTLP_DIR</c> (the directory the OTLP rings are in; the endpoint file is written beside them)
    /// and optionally <c>RIMGOVERNOR_RUN_ID</c>. Without the directory nothing listens: the channel is for a
    /// controller that can find the file, and a stray game session opens no port.
    /// </summary>
    internal static class HttpChannelBootstrap
    {
        internal const string DirectoryVariable = "RIMGOVERNOR_OTLP_DIR";
        internal const string RunIdVariable = "RIMGOVERNOR_RUN_ID";
        // A hang guard, as long as the brain's own wait for loaded Defs: a cold data load takes seconds, not minutes.
        private static readonly TimeSpan RegistryBudget = TimeSpan.FromMinutes(20);
        private static HttpChannelServer? server;
        private static readonly TaskCompletionSource<RouteTable?> published = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// The route table the running channel serves, for the in-process brain's player routes (#3115): completes with
        /// the table once the channel is listening, or with null when it will not run (no <see cref="DirectoryVariable"/>,
        /// or it failed to start). The brain registers on this one table so <c>/player/*</c> shares the endpoint file and
        /// the request guard with <c>/native/*</c>.
        /// </summary>
        internal static Task<RouteTable?> Routes => published.Task;

        /// <summary>Called once by <see cref="NativeStartup"/>.</summary>
        internal static void Start()
        {
            var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
            if (string.IsNullOrWhiteSpace(directory))
            {
                published.TrySetResult(null);
                return;
            }
            var runId = Environment.GetEnvironmentVariable(RunIdVariable);
            // Binding and writing the file are off the loading thread: a slow disk must not delay the host's mod load.
            // The routes wait for the host's registry (its own and the fixture tools): the host publishes it on the
            // game thread right after the native startup.
            Task.Run(async () =>
            {
                try
                {
                    var registry = RimBridgeToolAccess.Published;
                    if (await Task.WhenAny(registry, Task.Delay(RegistryBudget)).ConfigureAwait(false) != registry)
                        throw new TimeoutException($"the host registry was not published within {RegistryBudget.TotalMinutes:0} minutes");
                    var routes = new RouteTable();
                    NativeRoutes.RegisterHealth(routes);
                    var invoker = new NativeOpTable();
                    var prefixes = NativePawnActivities.StartJobPrefixes();
                    Log.Message($"[RimGovernor] HTTP channel routes {NativeOps.All.Count} native ops; activity-execution StartJob prefixes: {prefixes}.");
                    if (prefixes > 1) Log.Warning($"[RimGovernor] The activity-execution installer ran {prefixes} times.");
                    NativeRoutes.RegisterToolList(routes, invoker);
                    NativeRoutes.Register(routes, invoker);
                    var options = new HttpChannelOptions { EndpointDirectory = directory, RunId = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId! };
                    var started = new HttpChannelServer(options, routes);
                    started.Start();
                    server = started;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => started.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                    published.TrySetResult(routes);
                }
                catch (Exception e)
                {
                    published.TrySetResult(null);
                    Log.Warning($"[RimGovernor] HTTP control channel did not start: {e.Message}");
                }
            });
        }
    }
}
