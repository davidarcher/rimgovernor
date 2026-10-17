using System.Globalization;
using RimGovernor.Brain.NativeFacade;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>shelter/bunks-first</c>: the one complete-construction path. The blank lab houses nobody (asserted); with
/// ComplexFurniture finished and wood beside the colonists, the served shelter and sleeping families raise the shelter by
/// ordinary pawn work until the native room census holds a roofed proper room with a built Bed for every colonist. The
/// rung order (spots, beds on the freed slots, the ring) is policy, held by the shelter bunk tests; this case owns the
/// native outcome.
/// </summary>
internal static class ShelterBunks
{
    private const int Colonists = 3;

    internal static TypedCase Case() => new()
    {
        Name = "shelter/bunks-first",
        Scope = "Issues #612, #615 and #2278: from a lab that houses nobody indoors (asserted), the served shelter and sleeping "
            + "families raise the initial shelter by ordinary pawn work, nothing staged: the native room census then holds a "
            + "roofed proper room with a built Bed for every colonist.",
        Ticks = TypedCase.StagedTickCap,
        Tier = CaseTier.Nightly,
        Start = new LabStart { Colonists = Colonists },
        Crew = new Crew { Size = Colonists },
        Keep = ["Food"],
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Research = [new Fx.StageResearch { Def = "ComplexFurniture" }],
            World = new Fx.StageWorld { Hour = 7 },
            Site = new Fx.StageSite { Width = 3, Height = 3, Near = Fx.StageAnchor.Colonist, OffsetX = 2, Radius = 8, Require = [Fx.StageGround.Standable, Fx.StageGround.Empty] },
            Things = [new Fx.StageThing { Name = "wood", Def = "WoodLog", Count = 1000, Cell = new(1, 1), Near = true }],
        },
        Serve = new ServeSpec { Families = ["shelter", "sleeping"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "bunks" },
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var before = await HousedAsync(s, "rooms-before", cancellationToken).ConfigureAwait(false);
        if (before > 0)
        {
            throw new AcceptanceException($"the lab already holds {before} built beds in roofed rooms: the precondition satisfies the outcome");
        }

        _ = await s.ServeUntilAsync("bunks", s.TicksLeft, async ct =>
        {
            var housed = await HousedAsync(s, "rooms", ct).ConfigureAwait(false);
            var frames = await s.BuildingsAsync("construction", new Observations.ListBuildingsRequest
            {
                Statuses = [Observations.BuildingStatus.Blueprint, Observations.BuildingStatus.Frame],
            }, ct).ConfigureAwait(false);
            return (string.Create(CultureInfo.InvariantCulture, $"{housed} {frames.Count} {frames.Sum(f => f.Construction?.WorkLeft ?? 0)}"), housed >= Colonists);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The built Beds standing in the roofed proper rooms of the native room census.</summary>
    private static async Task<int> HousedAsync(ITypedSession s, string label, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ListRooms, new Observations.ListRoomsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var rooms = TypedReads.Observed(label, NativeOps.ListRooms, reply, reply.Outcome?.Observed).Rooms.Items
            .Where(r => r.ProperRoom == true && r.Outdoors != true && r.OpenRoofCount == 0)
            .ToList();
        var beds = rooms.SelectMany(r => r.Beds.Items).Select(b => b.Id).OfType<string>().ToList();
        s.Record(label, string.Join(' ', rooms.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Id}:{r.Beds.Count}"))));
        if (beds.Count == 0)
        {
            return 0;
        }

        var built = await s.BuildingsAsync(label + "-beds", new Observations.ListBuildingsRequest { Ids = [.. beds] }, cancellationToken).ConfigureAwait(false);
        return built.Count(b => b.Building.DefName == "Bed" && b.Status == Observations.BuildingStatus.Built);
    }
}
