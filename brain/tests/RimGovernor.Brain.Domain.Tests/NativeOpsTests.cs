using System.Reflection;
using RimGovernor.Brain.Bridge;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// <see cref="NativeOps"/> is the one name table of the native operations (the facade's hop labels, the read tally,
/// the acceptance harness's HTTP route). Every <see cref="INativeFacade"/> call is bound by exactly one op, and names
/// are unique, so a facade method cannot go unrouted or two methods share a label. The one exception is
/// <see cref="INativeFacade.OpenColonyEventsAsync"/>, whose reply is an in-process channel reader.
/// </summary>
public sealed class NativeOpsTests
{
    [Fact]
    public void EveryFacadeCallIsBoundByExactlyOneOp()
    {
        var facade = DispatchProxy.Create<INativeFacade, RecordingFacade>();
        var recorder = (RecordingFacade)(object)facade;
        foreach (var op in NativeOps.All.Where(op => op.OnFacade))
        {
            // A request with a required member does not read from an empty body.
            var body = ReferenceEquals(op, NativeOps.ReadDefinitionCatalog) ? """{"scope":{}}"""
                : ReferenceEquals(op, NativeOps.ReadTendDoctors) ? """{"scope":{},"pawnIds":[],"reachBudgetMs":0}"""
                : ReferenceEquals(op, NativeOps.ReadConsumption) ? """{"sinceHour":0}"""
                : ReferenceEquals(op, NativeOps.ReadStorageOccupancy) ? """{"groups":[],"sinceHour":0}"""
                : ReferenceEquals(op, NativeOps.ListResourceSources) ? """{"resource":""}""" : "{}";
            _ = op.InvokeJsonAsync(facade, body, CancellationToken.None);
        }

        // Opening the colony events hands over an in-process reader, so no op routes it.
        var calls = typeof(INativeFacade).GetMethods()
            .Where(m => typeof(Task).IsAssignableFrom(m.ReturnType) && m.Name != nameof(INativeFacade.OpenColonyEventsAsync))
            .ToList();
        Assert.Equal(calls.Count, NativeOps.All.Count(op => op.OnFacade));
        Assert.Equal([.. calls.Select(m => m.Name).Order(StringComparer.Ordinal)], [.. recorder.Called.Order(StringComparer.Ordinal)]);
        Assert.Equal([NativeOps.ReadSnapshot], NativeOps.All.Where(op => !op.OnFacade));
    }

    [Fact]
    public void NamesAreUniqueAndResolveWithOrWithoutThePrefix()
    {
        Assert.Equal(NativeOps.All.Count, NativeOps.All.Select(op => op.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var op in NativeOps.All)
        {
            Assert.True(NativeOps.TryGet(op.Name, out var byName) && ReferenceEquals(op, byName));
            Assert.True(NativeOps.TryGet(op.Tool, out var byTool) && ReferenceEquals(op, byTool));
        }

        Assert.False(NativeOps.TryGet("rimgovernor/no_such_op", out _));
    }

    /// <summary>Records which facade method each call reached; the reply is a faulted task the op's edge never awaits.</summary>
    public class RecordingFacade : DispatchProxy
    {
        internal List<string> Called { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Called.Add(targetMethod!.Name);
            var reply = targetMethod.ReturnType.GetGenericArguments()[0];
            var source = Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(reply))!;
            _ = source.GetType().GetMethod(nameof(TaskCompletionSource<object>.SetException), [typeof(Exception)])!.Invoke(source, [new NotSupportedException()]);
            return source.GetType().GetProperty(nameof(TaskCompletionSource<object>.Task))!.GetValue(source);
        }
    }
}
