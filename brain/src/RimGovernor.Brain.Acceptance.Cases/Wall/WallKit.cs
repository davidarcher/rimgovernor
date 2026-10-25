using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The staging, reads and intents the typed <c>wall/*</c> cases share.</summary>
internal static class WallKit
{
    /// <summary>The fixture op that lays a player's own Deconstruct designation on a thing (the supervisor may adopt it).</summary>
    internal const string PlayerDesignateTool = "test/player_designate";

    /// <summary>Lays a player Deconstruct designation on one thing by id.</summary>
    internal static readonly FixtureOp<string, Fx.PlayerDesignateReply> PlayerDesignate = new(
        PlayerDesignateTool,
        target => Args(("target", target)),
        (label, reply) => reply is not null && Fx.FixtureJson.Deserialize<Fx.PlayerDesignateReply>(reply.ToJsonString()) is { Success: true, Designated: true } read
            ? read
            : throw new AcceptanceException($"{label}: {PlayerDesignateTool} refused: {reply?.ToJsonString()}"));

    /// <summary>A bespoke fixture op's JSON arguments from name/value pairs.</summary>
    internal static JsonObject Args(params (string Name, JsonNode? Value)[] pairs) =>
        new(pairs.Select(p => new KeyValuePair<string, JsonNode?>(p.Name, p.Value)));

    /// <summary>The lab crew as builders: Construction first and every hour work, so vanilla takes the designated jobs.</summary>
    internal static IReadOnlyList<Fx.StagePawn> Builders { get; } =
    [
        .. Enumerable.Range(0, 3).Select(i => new Fx.StagePawn
        {
            Name = "builder-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Colonist = i,
            Work = new Dictionary<string, int> { ["Construction"] = 1 },
            Timetable = Fx.StageTimeAssignment.Work,
        }),
    ];

    /// <summary>A colonist-anchored open, unroofed, unzoned site of heavy ground of the given size.</summary>
    internal static Fx.StageSite Site(int width, int height) => new()
    {
        Width = width,
        Height = height,
        Near = Fx.StageAnchor.Colonist,
        OffsetX = 6,
        Radius = 30,
        Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed, Fx.StageGround.Unzoned, Fx.StageGround.Heavy],
    };

    /// <summary>Clears things, pawns, roofs and zones over the whole site.</summary>
    internal static Fx.StageClear Clear(int width, int height) => new()
    {
        Rect = new Fx.StageRect(0, 0, width, height),
        Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Pawns, Fx.StageClearKind.Roofs, Fx.StageClearKind.Zones],
    };

    /// <summary>The absolute cell of a site-relative staged cell.</summary>
    internal static Cell At(Fx.StageRect site, Fx.StageCell c) => new(site.X + c.X, site.Z + c.Z);

    /// <summary>The absolute cells of a site-relative rectangle.</summary>
    internal static List<Cell> Cells(Fx.StageRect site, Fx.StageRect rect) =>
        [.. from z in Enumerable.Range(rect.Z, rect.Height) from x in Enumerable.Range(rect.X, rect.Width) select new Cell(site.X + x, site.Z + z)];

    /// <summary>The wire cell of a domain cell.</summary>
    internal static RimGovernor.Contracts.Wire.Common.Cell Wire(Cell c) => new() { X = c.X, Z = c.Z };

    /// <summary>The ids among <paramref name="ids"/> the building census still lists, any faction.</summary>
    internal static async Task<HashSet<string>> PresentAsync(TypedSession s, string label, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var rows = await s.BuildingsAsync(label, new Observations.ListBuildingsRequest { Ids = [.. ids], AnyFaction = true }, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => r.Building.Id).Where(ids.Contains)];
    }

    /// <summary>
    /// The native clock run a wall case's builders work under: the supervisor runs the clock at <paramref name="generation"/>
    /// with the brain's colony watch policy, so the guards that act only under supervised play are armed.
    /// </summary>
    internal static ClockRunPlan ClockRun(ulong generation) => new()
    {
        ExpectedGeneration = generation,
        Speed = Clock.Speed.Ultrafast,
        Policy = new Clock.WatchPolicy
        {
            SeriousSingleHitDamage = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousSingleHitDamage,
            SeriousSummaryHealthFloor = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousSummaryHealthFloor,
            SeriousBleedRateFloor = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousBleedRateFloor,
            SeriousVitalPartFloor = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousVitalPartFloor,
            ExplosiveNearMarginCells = RimGovernor.Brain.Policy.HazardThresholdsRules.ExplosiveNearMarginCells,
            MeleeReachCells = RimGovernor.Brain.Policy.HazardThresholdsRules.MeleeReachCells,
            InjurySeverityFloorTicks = RimGovernor.Brain.Policy.HazardThresholdsRules.InjurySeverityFloorTicks,
            HungerHoldCategory = RimGovernor.Brain.Policy.HazardThresholdsRules.HungerHoldCategory,
            Mode = Clock.WatchMode.Colony,
            HealthDropFraction = .1f,
            MinHealthFraction = .5f,
            HostileWithin = 20f,
            InjuryStopCooldownMs = 0,
        },
    };

    /// <summary>
    /// Runs the native clock under the Auto grant standing now until the building census no longer lists
    /// <paramref name="id"/>, at most <paramref name="ticks"/> ticks, and records the ticks it took as
    /// <c>&lt;label&gt;_after_ticks</c>. A builder works a supervisor-stood guarded designation only under supervised play
    /// (the guard's work-giver hold), so a plain unpaused game never demolishes it.
    /// </summary>
    internal static async Task DemolishedAsync(TypedSession s, string label, string id, int ticks, CancellationToken cancellationToken)
    {
        var authority = await TypedAuthority.StatusAsync(s, label + "-authority", s.Identity, cancellationToken).ConfigureAwait(false);
        var generation = (authority.Context ?? throw new AcceptanceException($"{label}: no native generation: {authority}")).NativeGeneration.Claimed($"{label}: no native generation: {authority}");
        long? start = null;
        long tick = 0;
        var gone = false;
        var status = await s.RunClockUntilHeldAsync(label, ticks, ClockRun(generation), async (running, ct) =>
        {
            tick = running.Context?.Tick ?? 0;
            start ??= tick;
            gone = !(await PresentAsync(s, label + "-probe", [id], ct).ConfigureAwait(false)).Contains(id);
            return gone;
        }, cancellationToken).ConfigureAwait(false);
        if (!gone)
        {
            throw new AcceptanceException($"{label}: {id} still stands when the clock held ({(status.Hold as Clock.HoldState.HeldCase)?.Reason}): {status.Hold}");
        }

        s.Record(label + "_after_ticks", tick - (start ?? tick));
    }

    /// <summary>How many of <paramref name="cells"/> one cells read over the site reports roofed.</summary>
    internal static int Roofed(MapCellGrid grid, IEnumerable<Cell> cells) => cells.Count(c => grid.At(c) is { Roofed: true });

    /// <summary>How many of <paramref name="cells"/> one cells read over the site reports with terrain <paramref name="terrain"/>.</summary>
    internal static int WithTerrain(MapCellGrid grid, IEnumerable<Cell> cells, string terrain) => cells.Count(c => grid.At(c) is { } g && g.Terrain == terrain);

    /// <summary>The enclosure-guarded DECONSTRUCT Designate of one building.</summary>
    internal static Operations.DesignateIntent Deconstruct(string id, bool replaceWithWall = false) => new()
    {
        Designation = Operations.ThingDesignation.Deconstruct,
        Target = new() { Id = id },
        Guard = Operations.DesignationGuard.Enclosure,
        ReplaceWithWall = replaceWithWall ? true : null,
    };

    /// <summary>The observed effect of an applied receipt, or null.</summary>
    internal static Receipts.EffectEvidenceEffect? Effect(this Operations.ActionResultOutcome outcome) =>
        outcome.Applied?.Outcome?.Applied?.Observed?.Effect;
}
