using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>mood/gathering</c>: the native Party mechanics behind the explicit GatheringIntent on Actions/Apply. Five colonists
/// and one PartySpot at mid-afternoon; a rested, fed organizer is named and the game's own gathering worker starts a Party
/// the organizer joins (its lord job, read through the production pawn read). That normal review leaves gathering labor
/// unsupported is policy, held by RoomsFamilyTests.
/// </summary>
internal static class Gathering
{
    /// <summary>The LordJob class the vanilla Party gathering runs.</summary>
    private const string PartyLordJob = "LordJob_Joinable_Party";

    internal static TypedCase Case() => new()
    {
        Name = "mood/gathering",
        Scope = "An explicit GatheringIntent on Actions/Apply starts a native Party at the colony's PartySpot with the named "
            + "organizer in its lord. This proves native mechanics, not automatic participant choice.",
        Ticks = 600,
        Tier = CaseTier.Nightly,
        Start = new LabStart { Colonists = GatheringRules.GatheringMinColonists + 1 },
        Crew = new Crew { Size = GatheringRules.GatheringMinColonists + 1 },
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            World = new Fx.StageWorld { Hour = 14 },
            Site = new Fx.StageSite
            {
                Width = 5,
                Height = 5,
                OffsetX = 4,
                Radius = 12,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unroofed],
            },
            Buildings = [new Fx.StageBuilding { Name = "spot", Def = "PartySpot", Cells = [new(2, 2)] }],
            Pawns =
            [
                new Fx.StagePawn
                {
                    Name = "organizer",
                    Colonist = 0,
                    Cell = new(0, 0),
                    Needs = new Dictionary<string, float> { ["Food"] = 1f, ["Rest"] = 1f },
                },
            ],
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var organizer = s.Staged!.Pawns["organizer"].Id;
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await TypedPawnOps.ApplyAsync(s, "explicit-party", new RimGovernor.Brain.GameOrders.Operations.GatheringIntent
        {
            GatheringDef = GatheringRules.PartyGatheringDef,
            Organizer = new RimGovernor.Brain.GameFacts.Common.Ref { Id = organizer },
        }, cancellationToken).ConfigureAwait(false);
        _ = await s.RunUntilAsync("party-lord", s.TicksLeft, async ct =>
        {
            var row = await TypedPawnOps.PawnAsync(s, "organizer", organizer, ct).ConfigureAwait(false)
                ?? throw new AcceptanceException($"organizer {organizer} is gone");
            s.RecordWire("organizer", row);
            return ($"{row.LordJobClass} {row.Pawn.Place}", row.LordJobClass == PartyLordJob);
        }, cancellationToken).ConfigureAwait(false);
    }
}
