using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// Unified fake RimBridgeServer.Sdk surface + BridgeCommon double for the in-process probes
// (native-authority-status, native-authority-control, native-clock, native-proto-boundary).
// Reconciled from native-authority-status/BoundaryStub.cs (the fuller IRimBridgeContext shape,
// kept as canonical) and native-proto-boundary/TestSeams.cs (a strict subset, safely replaced).
namespace RimBridgeServer.Sdk
{
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class ToolAttribute : Attribute
    {
        public ToolAttribute(string name) { }
        public string Title { get; set; }
        public string Description { get; set; }
    }
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class ToolResponseAttribute : Attribute
    {
        public ToolResponseAttribute(string name, string type, string description) { }
        public bool Always { get; set; }
    }
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class ToolParameterAttribute : Attribute { public string Description { get; set; } }

    public interface IRimBridgeContext
    {
        IMainThread MainThread { get; }
        Dictionary<string, object> Arguments { get; }
    }
    public interface IMainThread { Task<T> InvokeAsync<T>(Func<T> action, CancellationToken token); }
}
namespace HomeBridge.BridgeTools
{
    // Reconciliation: native-authority-status/-control/-clock construct a local Context that
    // implements IRimBridgeContext.Arguments directly and never touch a static field, so the
    // ctx.Arguments fallback below preserves their exact original behavior. native-proto-boundary
    // never implements IRimBridgeContext at all (it calls production code with ctx: null) and
    // instead assigns the static Arguments field directly before each case -- the static-field
    // check short-circuits before ctx is ever dereferenced, so passing null stays safe.
    internal static class BridgeCommon
    {
        internal static IDictionary<string, object> Arguments;

        internal static IDictionary<string, object> RawArguments(RimBridgeServer.Sdk.IRimBridgeContext ctx, out string unavailable)
        {
            if (Arguments != null) { unavailable = null; return Arguments; }
            unavailable = ctx?.Arguments == null ? "Raw SDK arguments unavailable" : null;
            return ctx?.Arguments;
        }
    }
}
