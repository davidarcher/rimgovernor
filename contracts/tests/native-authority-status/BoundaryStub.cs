// Projection tests link production state/projection and official DTOs. SDK parsing
// and main-thread transport are not exercised by these deliberately throwing stubs.
using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Common = RimGovernor.Protocol.Common;
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
    public interface IRimBridgeContext { IMainThread MainThread { get; } }
    public interface IMainThread { Task<T> InvokeAsync<T>(Func<T> action, CancellationToken token); }
}
namespace HomeBridge.BridgeTools
{
    internal static class ProtoBoundary
    {
        internal static bool TryParse<T>(RimBridgeServer.Sdk.IRimBridgeContext ctx, string name, object request,
            MessageParser<T> parser, out T result, out Common.Failure failure) where T : IMessage<T>
            => throw new NotSupportedException("Projection test must not exercise a substituted parser.");
        internal static object Encode(IMessage reply) => throw new NotSupportedException();
        internal static bool ValidateIdentity(Common.Identity expected, Verse.Map? map,
            out Common.ObservationContext context, out Common.Failure failure) => throw new NotSupportedException();
    }
}
