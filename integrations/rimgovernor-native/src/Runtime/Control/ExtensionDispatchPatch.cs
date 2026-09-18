#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>Installation state of the extension dispatch patch, for runtime_health.</summary>
    public sealed class ExtensionDispatchStatus
    {
        internal ExtensionDispatchStatus(bool installed, int rewrapped, string hostVersion, string error)
        { Installed = installed; Rewrapped = rewrapped; HostVersion = hostVersion; Error = error; }
        /// <summary>The postfix is in place on RimBridgeStartup.RegisterExtensionTools.</summary>
        public bool Installed { get; }
        /// <summary>Extension tools re-registered with the off-reader handler so far.</summary>
        public int Rewrapped { get; }
        /// <summary>RimBridgeServer assembly version the patch resolved against.</summary>
        public string HostVersion { get; }
        public string Error { get; }
    }

    /// <summary>
    /// RimBridgeServer 2.1.1 registers every companion (extension) tool with
    /// Lib.GAB as a synchronous handler: RegisterExtensionTools hands
    /// server.Tools.RegisterTool a lambda that runs LegacyToolExecution.InvokeAlias
    /// to completion (CapabilityRegistry.Invoke = InvokeAsync().GetAwaiter().GetResult())
    /// inside the connection's MessageReceived callback, so the GABP reader
    /// parses nothing behind a running tool and a held clock_read_events
    /// blocks every other call (#115, #227). Lib.GAB itself is ready for a
    /// pending handler task: OnMessageReceived is async void and awaits the
    /// handler, replies are matched by request id and SendMessageAsync takes a
    /// send lock.
    ///
    /// This postfix re-registers each discovered extension tool (RegisterTool
    /// overwrites by name) with a handler that runs the same InvokeAlias on a
    /// thread-pool thread and returns the pending task, so the reader carries
    /// on to the next frame. Tool bodies are unchanged; they still marshal
    /// game reads through the main-thread pump, so concurrent calls overlap
    /// their transport and serialization, not their game-side work.
    ///
    /// Everything is resolved by name so the Runtime assembly needs no
    /// reference to RimBridgeServer or Lib.GAB; a target that does not
    /// resolve leaves the host serial, logs an error and reads as not
    /// installed under home/runtime_health.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ExtensionDispatchPatch
    {
        public const string Owner = "rimgovernor.native.extension-dispatch";
        private static readonly object Sync = new object();
        private static bool installed;
        private static int rewrapped;
        private static string hostVersion = "";
        private static string error = "";
        private static Func<string, IDictionary<string, object>, object>? invokeAlias;
        private static Func<object, Dictionary<string, object>>? normalizeArguments;
        private static PropertyInfo? extensionTools;

        public static ExtensionDispatchStatus Status
        {
            get { lock (Sync) return new ExtensionDispatchStatus(installed, rewrapped, hostVersion, error); }
        }

        static ExtensionDispatchPatch() => Install();

        private static void Install()
        {
            lock (Sync)
            {
                try
                {
                    var startup = AccessTools.TypeByName("RimBridgeServer.RimBridgeStartup") ?? throw new TypeLoadException("RimBridgeServer.RimBridgeStartup");
                    hostVersion = startup.Assembly.GetName().Version?.ToString() ?? "";
                    var register = AccessTools.Method(startup, "RegisterExtensionTools") ?? throw new MissingMethodException("RimBridgeStartup.RegisterExtensionTools");
                    // Open delegates rather than MethodInfo.Invoke, so a tool's
                    // exception reaches Lib.GAB unwrapped instead of as a
                    // TargetInvocationException.
                    invokeAlias = AccessTools.MethodDelegate<Func<string, IDictionary<string, object>, object>>(
                        AccessTools.Method(AccessTools.TypeByName("RimBridgeServer.LegacyToolExecution"), "InvokeAlias", new[] { typeof(string), typeof(IDictionary<string, object>) })
                        ?? throw new MissingMethodException("LegacyToolExecution.InvokeAlias"));
                    normalizeArguments = AccessTools.MethodDelegate<Func<object, Dictionary<string, object>>>(
                        AccessTools.Method(AccessTools.TypeByName("RimBridgeServer.ReflectedCapabilityBinding"), "NormalizeInvocationArguments", new[] { typeof(object) })
                        ?? throw new MissingMethodException("ReflectedCapabilityBinding.NormalizeInvocationArguments"));
                    extensionTools = AccessTools.Property(AccessTools.TypeByName("RimBridgeServer.RimBridgeCapabilities"), "ExtensionTools")
                        ?? throw new MissingMemberException("RimBridgeCapabilities.ExtensionTools");
                    new Harmony(Owner).Patch(register, postfix: new HarmonyMethod(typeof(ExtensionDispatchPatch), nameof(AfterRegister)));
                    installed = true;
                    error = "";
                    // The bridge normally starts after play data loads, i.e. after
                    // this static constructor; if it is already up, rewrap now.
                    var server = AccessTools.Field(startup, "_server")?.GetValue(null);
                    if (server != null) AfterRegister(server);
                }
                catch (Exception ex)
                {
                    installed = false;
                    error = ex.GetType().Name + ": " + ex.Message;
                    Log.Error("[RimGovernor] extension dispatch patch not installed against RimBridgeServer " + hostVersion + "; companion calls stay serial on the GABP reader: " + error);
                }
            }
        }

        /// <summary>Postfix of RimBridgeStartup.RegisterExtensionTools(GabpServer server).</summary>
        private static void AfterRegister(object server)
        {
            lock (Sync)
            {
                try
                {
                    var tools = AccessTools.Property(server.GetType(), "Tools")?.GetValue(server) ?? throw new MissingMemberException("GabpServer.Tools");
                    var registerTool = AccessTools.Method(tools.GetType(), "RegisterTool") ?? throw new MissingMethodException("ToolRegistry.RegisterTool");
                    var count = 0;
                    foreach (var discovered in (IEnumerable)extensionTools!.GetValue(null))
                    {
                        var alias = (string)AccessTools.Property(discovered.GetType(), "Alias").GetValue(discovered);
                        var info = AccessTools.Property(discovered.GetType(), "ToolInfo").GetValue(discovered);
                        registerTool.Invoke(tools, new[] { alias, OffReaderHandler(alias), info });
                        count++;
                    }
                    rewrapped = count;
                    error = "";
                    Log.Message("[RimGovernor] " + count + " extension tools dispatch off the GABP reader (RimBridgeServer " + hostVersion + ")");
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                    Log.Error("[RimGovernor] extension dispatch rewrap failed; companion calls stay serial on the GABP reader: " + error);
                }
            }
        }

        /// <summary>
        /// The same call RimBridgeServer's own lambda makes, on a pool thread.
        /// A thrown exception faults the task, which Lib.GAB's tools/call
        /// handler turns into the error response exactly as it did for a
        /// synchronous throw.
        /// </summary>
        private static Func<object, Task<object>> OffReaderHandler(string alias)
        {
            var invoke = invokeAlias!;
            var normalize = normalizeArguments!;
            return parameters => Task.Run(() => invoke(alias, normalize(parameters)));
        }
    }
}
