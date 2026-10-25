using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Host;

/// <summary>
/// Builds the parts of the brain that need a native client over <see cref="IGameApi"/>; the host owns when they are built and torn down. Everything is built over the
/// in-process native client. <see cref="ComposeSession"/> is the part of <c>openBuildingService</c> and
/// <c>serveBuildingWithBridge</c> that builds the session's capabilities; <see cref="ComposePlanners"/> is
/// <c>startServiceClock</c>.
/// </summary>
/// <remarks>
/// The autosave at a run's paused start is the host's: it arrives as
/// <see cref="PlannerContext.Autosave"/> because it must flush the governor state the host owns before it saves.
/// </remarks>
public sealed partial class BrainComposition(BrainCompositionOptions options)
{
    private readonly BrainCompositionOptions _options = Validated(options);

    /// <summary>The live target speed the clock runs start at: <see cref="BrainCompositionOptions.TargetSpeed"/> until changed.</summary>
    public ClockTargetSpeed TargetSpeed { get; } = new(options.TargetSpeed);

    /// <summary>The speed every Run asks for: <see cref="TargetSpeed"/>, or Hyperfast pinned under <see cref="BrainCompositionOptions.ClockTestAcceleration"/> whatever the player's speed route says.</summary>
    internal ClockTargetSpeed RunTargetSpeed() => _options.ClockTestAcceleration ? new ClockTargetSpeed(RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast) : TargetSpeed;

    // Set by ComposeSession (called once, before ComposePlanners) and read by ComposePlanners.
    private GameClient? _client;
    private FacadeActionsWriter? _writer;

    private static BrainCompositionOptions Validated(BrainCompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options;
    }

    /// <summary>Called once, before the session exists.</summary>
    public SessionParts ComposeSession(IGameApi facade, TelemetryEmitter telemetry)
    {
        ArgumentNullException.ThrowIfNull(facade);
        ArgumentNullException.ThrowIfNull(telemetry);
        var client = GameClient.Create(facade, _options.CatalogFactory);
        client.StartingIdeoligion = _options.StartingIdeoligion;
        client.Telemetry = telemetry;
        // The writers go through the client's counting facade, so their native calls are tallied like its reads.
        var writer = new FacadeActionsWriter(client.Facade);
        var player = new FacadePlayerAdapter(client);
        _client = client;
        _writer = writer;
        var families = _options.Families;
        return new SessionParts
        {
            Native = client,
            Writer = writer,
            Clock = new ClockCapabilities(client, new FacadeClockWriter(client.Facade)),
            // Only the food storage upkeep family hauls.
            Haul = families.FoodStorageUpkeep ? new HaulCapabilities(client, writer) : null,
            Movement = families.Defense ? new MovementCapabilities(writer) : null,
            Trade = families.Trade ? new TradeCapabilities { Native = client, Writer = writer, Requests = client } : null,
            Player = new PlayerNatives
            {
                Source = client,
                Presentation = player,
                Media = player,
                Notifications = player,
                Lifecycle = player,
                Colony = client,
            },
            Orphans = (plans, ct) => SweepOrphans(client, writer, new HostLog(telemetry, "governor-state"), plans, ct),
        };
    }

    // Cancels the open trade session a per-world rebuild left without an owner. The world is the
    // one the rebuild runs for, which is the loaded one. Read and transport errors abort the rebuild; a native
    // refusal is logged. The callback is synchronous, as the store's orphan pass is.
    private static void SweepOrphans(GameClient client, IActionsWriter writer, HostLog log, IReadOnlyList<PlanState> plans, CancellationToken ct) =>
        SweepOrphansAsync(client, writer, log, plans, ct).GetAwaiter().GetResult();

    private static async Task SweepOrphansAsync(GameClient client, IActionsWriter writer, HostLog log, IReadOnlyList<PlanState> plans, CancellationToken ct)
    {
        var context = await new FacadeWorldSource(client.Facade).ReadContextAsync(ct).ConfigureAwait(false);
        var identity = context.Identity ?? throw new InvalidOperationException("orphans: the loaded world has no identity");
        RimGovernor.Brain.GameFacts.Observations.TradeSession session;
        try
        {
            session = await ((GameClient)client).ReadTradeSessionAsync(identity, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException("orphans: trade session: " + e.Message, e);
        }
        if (session.Engagement is not RimGovernor.Brain.GameFacts.Observations.TradeEngagement.Engaged engaged)
        {
            return;
        }
        var trader = RimGovernor.Brain.Runtime.TradeTarget.TraderOf(session);
        var end = Trade.NewTradeEnd(trader, engaged.Negotiator, TradeEndKind.Cancel, false);
        var action = DomainAction.NewTradeAction(new ActionId("orphan-trade-end"), end);
        var wire = IntentActionRules.IntentAction("orphan-trade-end", action);
        RimGovernor.Brain.GameFacts.Operations.ApplyReply reply;
        try
        {
            reply = await writer.ApplyAsync(identity, [wire], ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException("orphans: cancel trade: " + e.Message, e);
        }
        if (reply.Results.Count == 0 || reply.Results[0].Outcome is not RimGovernor.Brain.GameFacts.Operations.ActionResultOutcome.AppliedCase)
        {
            log.Warn("orphan_trade_not_cancelled", "governor state: orphan trade not cancelled", ("trader", trader));
            return;
        }
        log.Info("orphan_trade_cancelled", "governor state: cancelled orphan trade", ("trader", trader), ("old_plans", plans.Count));
    }
}
