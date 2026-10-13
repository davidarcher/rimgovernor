#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
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

        public IReadOnlyCollection<string> ToolNames => _tools.Keys;

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
            if (!_tools.TryGetValue(tool, out var entry)) throw new KeyNotFoundException(tool);
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

#pragma warning disable CA2255 // See NativeFacadeBootstrap: the host has no startup hook for a plugin assembly.
        [ModuleInitializer]
        internal static void Start()
        {
            var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
            if (string.IsNullOrWhiteSpace(directory)) return;
            var runId = Environment.GetEnvironmentVariable(RunIdVariable);
            // Binding and writing the file are off the loading thread: a slow disk must not delay the host's mod load.
            Task.Run(() =>
            {
                try
                {
                    var routes = new RouteTable();
                    NativeRoutes.RegisterHealth(routes);
                    NativeRoutes.Register(routes, NativeToolInvoker.Discover(typeof(HttpChannelBootstrap).Assembly));
                    var options = new HttpChannelOptions { EndpointDirectory = directory, RunId = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId! };
                    var started = new HttpChannelServer(options, routes);
                    started.Start();
                    server = started;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => started.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                }
                catch (Exception e)
                {
                    Log.Warning($"[RimGovernor] HTTP control channel did not start: {e.Message}");
                }
            });
        }
#pragma warning restore CA2255
    }
}
