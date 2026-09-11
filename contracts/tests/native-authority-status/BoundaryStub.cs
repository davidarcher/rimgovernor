#nullable enable
// Substitute only SDK argument extraction/main-thread transport. Production
// ProtoBoundary parsing, identity admission, authority lookup and projection run unchanged.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace RimBridgeServer.Sdk
{
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class ToolAttribute : Attribute
    {
        public ToolAttribute(string name) { }
        public string? Title { get; set; }
        public string? Description { get; set; }
    }
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class ToolResponseAttribute : Attribute
    {
        public ToolResponseAttribute(string name, string type, string description) { }
        public bool Always { get; set; }
    }
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class ToolParameterAttribute : Attribute { public string? Description { get; set; } }
    public interface IRimBridgeContext
    {
        IMainThread MainThread { get; }
        Dictionary<string, object>? Arguments { get; }
    }
    public interface IMainThread { Task<T> InvokeAsync<T>(Func<T> action, CancellationToken token); }
}
namespace HomeBridge.BridgeTools
{
    internal static class BridgeCommon
    {
        internal static Dictionary<string, object>? RawArguments(RimBridgeServer.Sdk.IRimBridgeContext ctx, out string? unavailable)
        {
            unavailable = ctx.Arguments == null ? "Raw SDK arguments unavailable" : null;
            return ctx.Arguments;
        }
    }
}
