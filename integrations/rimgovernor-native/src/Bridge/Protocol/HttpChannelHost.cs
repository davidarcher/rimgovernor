#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimGovernor.Brain.HttpChannel;
using RimGovernor.Host.Sdk;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Runs the local HTTP/JSON control channel inside the mod (#3083). <c>POST /native/&lt;tool&gt;</c> reaches the
    /// same tool methods GABP dispatches, so the two routes share one implementation, admission class and
    /// main-thread rule. The channel itself (listener, guard, routes, endpoint file) is the game-free
    /// RimGovernor.Brain.HttpChannel; this file only adapts the mod's tools to it and starts it.
    /// </summary>
    internal sealed class NativeToolInvoker : INativeToolInvoker
    {
        private const string Prefix = "rimgovernor/";
        private readonly Dictionary<string, Tool> _tools;

        private NativeToolInvoker(Dictionary<string, Tool> tools) { _tools = tools; }

        /// <summary>
        /// The ProtoJSON tools of this assembly plus every other tool the host registry knows (the host's own
        /// <c>rimgovernor/set_time_speed</c>, <c>test/*</c> fixture ops, tools with other arguments). The registry fills
        /// in as the host starts, so the second part is read on each call.
        /// </summary>
        public IReadOnlyCollection<string> ToolNames
        {
            get
            {
                var names = new HashSet<string>(_tools.Keys, StringComparer.Ordinal);
                foreach (var name in RimBridgeToolAccess.Current?.ToolNames ?? Array.Empty<string>()) names.Add(RouteName(name));
                return names;
            }
        }

        /// <summary>The route name of a canonical tool name: <c>rimgovernor/x</c> is <c>x</c>, any other name keeps its path.</summary>
        private static string RouteName(string canonical) =>
            canonical.StartsWith(Prefix, StringComparison.Ordinal) ? canonical.Substring(Prefix.Length) : canonical;

        /// <summary>
        /// Every <c>[Tool]</c> of the assembly whose only caller argument is <c>request</c>: the ProtoJSON tools.
        /// Tools that take other arguments (the supervised-play and preview tools) stay GABP-only.
        /// </summary>
        internal static NativeToolInvoker Discover(Assembly assembly)
        {
            var tools = new Dictionary<string, Tool>(StringComparer.Ordinal);
            var instances = new Dictionary<Type, object?>();
            foreach (var type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    var attribute = method.GetCustomAttribute<ToolAttribute>(inherit: false);
                    if (attribute == null || !attribute.Name.StartsWith(Prefix, StringComparison.Ordinal) || !TakesOnlyRequest(method)) continue;
                    object? instance = null;
                    if (!method.IsStatic && !instances.TryGetValue(type, out instance))
                    {
                        try { instance = Activator.CreateInstance(type, nonPublic: true); }
                        catch (Exception e) when (e is MissingMethodException or TargetInvocationException)
                        {
                            Log.Warning($"[RimGovernor] HTTP channel skips tool type {type.FullName}: {e.Message}");
                        }
                        instances[type] = instance;
                    }
                    if (!method.IsStatic && instance == null) continue;
                    tools[attribute.Name.Substring(Prefix.Length)] = new Tool(attribute.Name, instance, method);
                }
            }
            return new NativeToolInvoker(tools);
        }

        private static bool TakesOnlyRequest(MethodInfo method)
        {
            var seenRequest = false;
            foreach (var parameter in method.GetParameters())
            {
                if (typeof(IRimBridgeContext).IsAssignableFrom(parameter.ParameterType) || parameter.ParameterType == typeof(CancellationToken)) continue;
                if (parameter.Name != "request") return false;
                seenRequest = true;
            }
            return seenRequest && typeof(Task).IsAssignableFrom(method.ReturnType);
        }

        public async Task<NativeToolReply> InvokeAsync(string tool, string requestJson, string? admissionClass, string? trace, CancellationToken cancellationToken)
        {
            if (!_tools.TryGetValue(tool, out var entry)) return await InvokeRegisteredAsync(tool, requestJson, cancellationToken).ConfigureAwait(false);
            var mainThread = RimBridgeMainThreadAccess.Current ?? throw new InvalidOperationException("The host's game-thread access is not available.");
            var arguments = new Dictionary<string, object>(StringComparer.Ordinal) { ["request"] = requestJson };
            if (!string.IsNullOrEmpty(admissionClass)) arguments[MainThreadAdmission.ClassArgument] = admissionClass!;
            if (!string.IsNullOrEmpty(trace)) arguments["trace"] = trace!;
            var context = new HttpToolContext(entry.CapabilityId, arguments, mainThread);

            var values = entry.Method.GetParameters().Select(p =>
                typeof(IRimBridgeContext).IsAssignableFrom(p.ParameterType) ? (object?)context
                : p.ParameterType == typeof(CancellationToken) ? cancellationToken
                : requestJson).ToArray();
            var task = (Task)entry.Method.Invoke(entry.Instance, values)!;
            await task.ConfigureAwait(false);
            var result = task.GetType().GetProperty("Result")?.GetValue(task);
            if (result is not IDictionary<string, object?> reply || !reply.TryGetValue("payload", out var payload) || payload is not string json)
                throw new InvalidOperationException($"{entry.CapabilityId} returned no ProtoJSON payload.");
            string? timing = null;
            if (reply.TryGetValue("timing", out var raw) && raw != null) timing = JsonConvert.SerializeObject(raw);
            return new NativeToolReply(json, timing);
        }

        /// <summary>
        /// A tool that takes named arguments rather than one ProtoJSON <c>request</c>: the body is a JSON object of its
        /// arguments, the host registry runs it as a GABP call would, and the reply is its result as a JSON object (a
        /// non-object result is wrapped as <c>{"result": ...}</c>). The registry alias is the canonical name, which
        /// is the route name with the <c>rimgovernor/</c> prefix put back when the route had none of its own.
        /// </summary>
        private static async Task<NativeToolReply> InvokeRegisteredAsync(string route, string argumentsJson, CancellationToken cancellationToken)
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
            return new NativeToolReply(reply.ToString(Formatting.None), null);
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

        private sealed record Tool(string CapabilityId, object? Instance, MethodInfo Method);

        /// <summary>The context a tool sees for an HTTP call: the caller's arguments and the host's game-thread access.</summary>
        private sealed class HttpToolContext : IRimBridgeContext
        {
            internal HttpToolContext(string capabilityId, IDictionary<string, object> arguments, IRimBridgeMainThread mainThread)
            { CapabilityId = capabilityId; Arguments = arguments; MainThread = mainThread; }
            public string OperationId { get; } = "http-" + Guid.NewGuid().ToString("N");
            public string CapabilityId { get; }
            public IDictionary<string, object> Arguments { get; }
            public IRimBridgeMainThread MainThread { get; }
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
        private static HttpChannelServer? server;
        private static readonly TaskCompletionSource<RouteTable?> published = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// The route table the running channel serves, for the in-process brain's player routes (#3115): completes with
        /// the table once the channel is listening, or with null when it will not run (no <see cref="DirectoryVariable"/>,
        /// or it failed to start). The brain registers on this one table so <c>/player/*</c> shares the endpoint file and
        /// the request guard with <c>/native/*</c>.
        /// </summary>
        internal static Task<RouteTable?> Routes => published.Task;

#pragma warning disable CA2255 // See NativeFacadeBootstrap: the host has no startup hook for a plugin assembly.
        [ModuleInitializer]
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
            Task.Run(() =>
            {
                try
                {
                    var routes = new RouteTable();
                    NativeRoutes.RegisterHealth(routes);
                    var invoker = NativeToolInvoker.Discover(typeof(HttpChannelBootstrap).Assembly);
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
#pragma warning restore CA2255
    }
}
