using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The concrete <see cref="IBrainComposition"/>: Go's <c>serve_building.go</c> and <c>serve_clock.go</c> over the
/// in-process native client. <see cref="ComposeSession"/> is the part of <c>openBuildingService</c> and
/// <c>serveBuildingWithBridge</c> that builds the session's capabilities; <see cref="ComposePlanners"/> is
/// <c>startServiceClock</c>.
/// </summary>
/// <remarks>
/// Not carried: the autosave at a window's paused start (Go <c>newGoAutosaver</c>, slice b #3114), which is left
/// null in the window configuration.
/// </remarks>
public sealed partial class BrainComposition(BrainCompositionOptions options) : IBrainComposition
{
    private readonly BrainCompositionOptions _options = Validated(options);

    // Set by ComposeSession (called once, before ComposePlanners) and read by ComposePlanners.
    private FacadeNativeClient? _client;
    private FacadeActionsWriter? _writer;

    private static BrainCompositionOptions Validated(BrainCompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options;
    }

    /// <inheritdoc />
    public SessionParts ComposeSession(INativeFacade facade, TelemetryEmitter telemetry)
    {
        ArgumentNullException.ThrowIfNull(facade);
        ArgumentNullException.ThrowIfNull(telemetry);
        var client = FacadeNativeClient.Create(facade, _options.CatalogFactory);
        // The writers go through the client's counting facade, so their native calls are tallied like its reads.
        var writer = new FacadeActionsWriter(client.Facade);
        _client = client;
        _writer = writer;
        var families = _options.Families;
        return new SessionParts
        {
            Native = client,
            Writer = writer,
            Clock = new ClockCapabilities(client, new FacadeClockWriter(client.Facade)),
            // Go haulExecutorRequired: only the food storage upkeep family hauls.
            Haul = families.FoodStorageUpkeep ? new HaulCapabilities(client, writer) : null,
            Movement = families.Defense ? new MovementCapabilities(writer) : null,
            Trade = families.Trade ? new TradeCapabilities { Native = client, Writer = writer, Requests = client } : null,
            Orphans = (plans, ct) => SweepOrphans(client, writer, new HostLog(telemetry, "governor-state"), plans, ct),
        };
    }

    // Go orphanSweep: cancels the open trade session a per-world rebuild left without an owner. The world is the
    // one the rebuild runs for, which is the loaded one. Read and transport errors abort the rebuild; a native
    // refusal is logged. The callback is synchronous, as the store's orphan pass is.
    private static void SweepOrphans(FacadeNativeClient client, IActionsWriter writer, HostLog log, IReadOnlyList<PlanState> plans, CancellationToken ct) =>
        SweepOrphansAsync(client, writer, log, plans, ct).GetAwaiter().GetResult();

    private static async Task SweepOrphansAsync(FacadeNativeClient client, IActionsWriter writer, HostLog log, IReadOnlyList<PlanState> plans, CancellationToken ct)
    {
        var context = await new FacadeWorldSource(client.Facade).ReadContextAsync(ct).ConfigureAwait(false);
        var identity = context.Identity ?? throw new InvalidOperationException("orphans: the loaded world has no identity");
        TradeSessionRead session;
        try
        {
            (session, _) = await ((ITradeNative)client).ReadTradeSessionAsync(identity, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException("orphans: trade session: " + e.Message, e);
        }
        if (session.Trader.Length == 0)
        {
            return;
        }
        var end = Trade.NewTradeEnd(session.Trader, new PawnId(session.Negotiator), TradeEndKind.Cancel, false);
        var action = DomainAction.NewTradeAction(new ActionId("orphan-trade-end"), end);
        var wire = BridgeFunctions.IntentAction("orphan-trade-end", action);
        PbOperations.ApplyReply reply;
        try
        {
            reply = await writer.ApplyAsync(identity, [wire], ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException("orphans: cancel trade: " + e.Message, e);
        }
        if (reply.Results.Count == 0 || reply.Results[0].Outcome is not PbOperations.ActionResultOutcome.AppliedCase)
        {
            log.Warn("orphan_trade_not_cancelled", "governor state: orphan trade not cancelled", ("trader", session.Trader));
            return;
        }
        log.Info("orphan_trade_cancelled", "governor state: cancelled orphan trade", ("trader", session.Trader), ("old_plans", plans.Count));
    }
}
