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
    public async Task EveryFacadeCallIsBoundByExactlyOneOp()
    {
        var facade = DispatchProxy.Create<INativeFacade, RecordingFacade>();
        var recorder = (RecordingFacade)(object)facade;
        foreach (var op in NativeOps.All.Where(op => op.OnFacade))
        {
            // A request with a required member does not read from an empty body.
            var body = ReferenceEquals(op, NativeOps.ReadDefinitionCatalog) ? """{"scope":{}}"""
                : ReferenceEquals(op, NativeOps.ReadTendDoctors) ? """{"scope":{},"pawnIds":[],"reachBudgetMs":0}"""
                : ReferenceEquals(op, NativeOps.ReadColonists) ? """{"currentMapOnly":false}"""
                : ReferenceEquals(op, NativeOps.ReadConsumption) ? """{"sinceHour":0}"""
                : ReferenceEquals(op, NativeOps.ReadStorageOccupancy) ? """{"groups":[],"sinceHour":0}"""
                : ReferenceEquals(op, NativeOps.ListResourceSources) ? """{"resource":""}"""
                : ReferenceEquals(op, NativeOps.ReadRecipes) ? """{"benchId":""}"""
                : ReferenceEquals(op, NativeOps.ReadWorldProgression) ? """{"scope":{}}"""
                : ReferenceEquals(op, NativeOps.ReadCombatGeometry) ? """{"identity":{"colonyId":"","loadToken":"","mapId":0},"cells":[],"hostileIds":[]}"""
                : ReferenceEquals(op, NativeOps.ReadDefenseSite) ? """{"scope":{},"region":{"minimum":{"x":0,"z":0},"maximum":{"x":0,"z":0}}}"""
                : ReferenceEquals(op, NativeOps.ReadLinesOfFire) ? """{"scope":{},"firingCells":[],"approachCells":[],"budgetMs":0}"""
                : ReferenceEquals(op, NativeOps.ReadSpatialAccess) ? """{"scope":{},"blockedCells":[],"targetCells":[],"pawnIds":[],"budgetMs":0}"""
                : ReferenceEquals(op, NativeOps.ReadExcavationSite) ? """{"scope":{},"cells":[],"accessCell":{"x":0,"z":0}}"""
                : ReferenceEquals(op, NativeOps.Apply) ? """{"deferSnapshot":false}"""
                : ReferenceEquals(op, NativeOps.ApplyOverlay) ? """{"identity":{"colonyId":"","loadToken":"","mapId":0},"layerId":"","enabled":false,"shapes":[],"labels":[]}"""
                : ReferenceEquals(op, NativeOps.DemandRendering) ? """{"viewer":{"identity":{"colonyId":"","loadToken":"","mapId":0}},"leaseSeconds":0}"""
                : ReferenceEquals(op, NativeOps.ReadNotifications) ? """{"identity":{"colonyId":"","loadToken":"","mapId":0},"letterLimit":0,"messageLimit":0,"alertLimit":0,"includeLetters":false,"includeMessages":false,"includeAlerts":false}"""
                : ReferenceEquals(op, NativeOps.OpenFrames) ? """{"routePathBudget":{"type":"unlimited"},"linesOfFireBudget":{"type":"unlimited"}}"""
                : ReferenceEquals(op, NativeOps.PreviewPlacement) ? """{"placements":[],"identity":{"colonyId":"","loadToken":"","mapId":0}}"""
                : ReferenceEquals(op, NativeOps.SaveGame) ? """{"expectedTick":{"type":"any"}}"""
                : ReferenceEquals(op, NativeOps.StartLoad) ? """{"readiness":"READINESS_MAP","timeoutMs":0}"""
                : ReferenceEquals(op, NativeOps.StartNewColony) ? """{"timeoutMs":0}"""
                : ReferenceEquals(op, NativeOps.WaitSaveSignal) ? """{"timeoutMs":0}"""
                : ReferenceEquals(op, NativeOps.HoldClock) ? """{"reason":"HOLD_REASON_REVIEW"}"""
                : ReferenceEquals(op, NativeOps.RunClock) ? """{"expectedGeneration":1,"speed":"SPEED_NORMAL","plannedHoldSerial":0,"holdAtTick":1,"policy":{"mode":"WATCH_MODE_COLONY","healthDropFraction":0.5,"minHealthFraction":0.2,"hostileWithin":40,"injuryStopCooldownMs":0,"hungerHoldCategory":"HUNGER_CATEGORY_URGENTLY_HUNGRY","seriousSingleHitDamage":10,"seriousSummaryHealthFloor":0.3,"seriousBleedRateFloor":0.1,"seriousVitalPartFloor":0.2,"explosiveNearMarginCells":2,"meleeReachCells":1,"injurySeverityFloorTicks":1000}}"""
                : ReferenceEquals(op, NativeOps.ListSupplies) ? """{"filter":{"category":"STOCK_CATEGORY_HAULABLE","ownership":"STOCK_OWNERSHIP_OURS","includeHeld":true}}""" : "{}";
            await Assert.ThrowsAsync<NotSupportedException>(() => op.InvokeJsonAsync(facade, body, CancellationToken.None));
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

    /// <summary>Records which facade method each call reached; its fault proves deserialization reached the facade.</summary>
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
