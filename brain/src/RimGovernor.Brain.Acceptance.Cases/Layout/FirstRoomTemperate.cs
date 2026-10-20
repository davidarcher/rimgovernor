using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry;
using Decision = RimGovernor.Brain.Telemetry.Decision;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>layout/first-room-temperate</c> (#3659): a new temperate colony of three with wood beside it and no room standing.
/// Under the served work, shelter and sleeping families the first layout_plan row records a plan, and a sleeping spot stands for every colonist (the day-one stand-ins, ShelterRules.StandInsOwed) while no roofed
/// proper room stands yet. The stand-ins' cells and their clearing once indoor beds take everyone are policy
/// (SleepingStandInTests); this case owns the native floor.
/// </summary>
internal static class LayoutFirstRoomTemperate
{
    private const int Colonists = 3;

    internal static TypedCase Case() => new()
    {
        Name = "layout/first-room-temperate",
        Scope = "Issue #3659: a served new temperate colony of three (wood beside it, no room standing, asserted) records a first "
            + "layout_plan row, and a built SleepingSpot stands for every colonist while no roofed "
            + "proper room stands: the stand-in floor until the first room stands.",
        Ticks = 6_000, // a hang guard: the plan and the spots land within the first game hours
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Colonists },
        Crew = new Crew { Size = Colonists },
        Keep = ["Food"],
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            World = new Fx.StageWorld { Hour = 7 },
            Things = [new Fx.StageThing { Name = "wood", Def = "WoodLog", Count = 600, Cell = new(1, 1), Near = true }],
        },
        Serve = new ServeSpec { Families = ["work", "shelter", "sleeping"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "first-room-temperate" },
        Run = RunAsync,
    };

    private static string Text(IReadOnlyDictionary<string, object?> fields, string key) => fields.GetValueOrDefault(key) as string ?? "";

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        if (await FirstRoomReads.RoofedRoomsAsync(s, "rooms-before", cancellationToken).ConfigureAwait(false) is { Count: > 0 } before)
        {
            throw new AcceptanceException($"the lab already holds roofed proper rooms ({string.Join(' ', before.Select(r => r.Id))}): the precondition hides the floor");
        }
        var floor = s.Telemetry(TelemetryKinds.LayoutPlan).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        LogRow? planned = null;
        var spots = 0;
        var roofed = 0;
        _ = await s.ServeUntilAsync("stand-ins", s.TicksLeft, async ct =>
        {
            planned ??= s.Telemetry(TelemetryKinds.LayoutPlan).FirstOrDefault(r => r.Sequence > floor
                && Text(r.Fields(), Decision.VerdictKey) == "planned" && Text(r.Fields(), Decision.TargetKey) == "plan");
            var built = await s.BuildingsAsync("spots", new Observations.ListBuildingsRequest
            {
                DefNames = [SleepingRules.SleepingSpotDefinition],
                Statuses = [Observations.BuildingStatus.Built],
            }, ct).ConfigureAwait(false);
            spots = built.Count;
            roofed = spots >= Colonists ? (await FirstRoomReads.RoofedRoomsAsync(s, "rooms", ct).ConfigureAwait(false)).Count : 0;
            return (string.Create(CultureInfo.InvariantCulture, $"plan={planned is not null} spots={spots} roofed={roofed}"), planned is not null && spots >= Colonists);
        }, cancellationToken).ConfigureAwait(false);

        var plan = planned ?? throw new AcceptanceException("no layout_plan row recorded the first plan");
        var summary = Text(plan.Fields(), "summary");
        s.Record("first-plan", summary);
        if (spots < Colonists)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{spots} built sleeping spots for {Colonists} colonists"));
        }
        if (roofed > 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"a roofed proper room stood ({roofed}) before the stand-ins: the floor was not the first sleeping place"));
        }
    }
}

/// <summary>The native reads the first-room cases share.</summary>
internal static class FirstRoomReads
{
    /// <summary>The roofed proper indoor rooms of the native room census.</summary>
    public static async Task<IReadOnlyList<Observations.RoomState>> RoofedRoomsAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ListRooms, new Observations.ListRoomsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var rooms = TypedReads.Observed(label, NativeOps.ListRooms, reply, reply.Outcome?.Observed).Rooms.Items
            .Where(r => r.ProperRoom == true && r.Outdoors != true && r.OpenRoofCount == 0)
            .ToList();
        s.Record(label, string.Join(' ', rooms.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Id}:{r.Role}:{r.CellCount}"))));
        return rooms;
    }

    /// <summary>The colonists' hypothermia severities by pawn id, from the production pawn read's body stats.</summary>
    public static async Task<IReadOnlyDictionary<string, double>> HypothermiaAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ListPawns, new Observations.ListPawnsRequest
        {
            Scope = s.Scope,
            Filter = new() { Colonist = true },
            Details = new Observations.PawnDetails { Settings = true, Work = true },
        }, cancellationToken).ConfigureAwait(false);
        var observed = TypedReads.Observed(label, NativeOps.ListPawns, reply, reply.Outcome?.Observed);
        return observed.Pawns.ToDictionary(p => p.Pawn.Id, p => p.Settings?.Body?.Hypothermia ?? 0, StringComparer.Ordinal);
    }
}
