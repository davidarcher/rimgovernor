using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>shelter/manhunter_waitout</c> and <c>shelter/toxic_fallout</c>, one row each: the served shelter family's safe-area Need and the sheltering planner on a real threat. A
/// roofed hut is the lab's only room, so the Safe area covers it; the colonists (and, under fallout, a tame dog) stand ten
/// cells south of it when the trigger starts. Each row's served brain moves every one of them into the Safe area, where
/// they stand in the hut unhurt (the colony facts' allowed areas and the production pawn read); then test/shelter_end ends
/// the trigger and the brain lifts every restriction again.
/// </summary>
internal static class ShelterSheltering
{
    private const string EndOp = "test/shelter_end";

    private const string PackPrefix = "pack-";

    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> End = ServedOps.Inject(EndOp);

    /// <summary>The injury hediffs a fight or a bite leaves; an old scar carries the same def, so a pawn's count at the first read is its baseline.</summary>
    private static readonly HashSet<string> Injuries = new(StringComparer.Ordinal) { "Bite", "Scratch", "Cut", "Stab", "Bruise", "Crush", "Crack", "Scratch_Claw" };

    /// <summary>One trigger: a manhunter pack at the west, or toxic fallout with a tame dog to shelter too.</summary>
    private sealed record Row(string Name, string Trigger, bool Dog, string Threat);

    private static readonly Row[] Rows =
    [
        new("shelter/manhunter_waitout", "manhunter", false, "a manhunter pack entering from the west, none of the colonists drafted"),
        new("shelter/toxic_fallout", "fallout", true, "toxic fallout, with a tame pen-free dog to shelter too"),
    ];

    internal static IEnumerable<TypedCase> Cases() => Rows.Select(row => new TypedCase
    {
        Name = row.Name,
        Scope = $"On the lab beside a roofed hut, the only room the Safe area covers, {row.Threat}: under the served brain every "
            + "colonist (and the dog) is moved into the Safe area and stands in the hut unhurt; once test/shelter_end ends the "
            + "threat, every restriction is lifted. Native area writes, the native Safe area and a real threat are the signal, "
            + "not a planner decision.",
        Ticks = 12000,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 3 },
        Stage = Spec(row),
        Crew = new Crew { Size = 3 },
        Ops = [EndOp],
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["recovery", "sheltering"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "sheltering" },
        Run = (s, ct) => RunAsync(s, row, ct),
    });

    // A 9x9 roofed wooden hut (door south) on the north of a 9x16 site; the colonists and the dog stand on its south row.
    private static Fx.StageSpec Spec(Row row) => new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 9,
            Height = 16,
            Radius = 12,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Heavy, Fx.StageGround.Unroofed],
        },
        Rooms = [new Fx.StageRoom { Name = "hut", Rect = new(0, 7, 9, 9), Stuff = "WoodLog", Doors = [new(4, 7)], Roof = true }],
        Pawns = Pawns(row),
        Incidents = row.Trigger == "fallout" ? [new Fx.StageIncident { Name = "fallout", Def = "ToxicFallout" }] : [],
    };

    private static List<Fx.StagePawn> Pawns(Row row)
    {
        List<Fx.StagePawn> pawns =
        [
            new Fx.StagePawn { Name = "colonist-0", Colonist = 0, Cell = new(1, 0) },
            new Fx.StagePawn { Name = "colonist-1", Colonist = 1, Cell = new(3, 0) },
            new Fx.StagePawn { Name = "colonist-2", Colonist = 2, Cell = new(5, 0) },
        ];
        if (row.Dog)
        {
            pawns.Add(new Fx.StagePawn { Name = "dog", Kind = "Husky", Cell = new(7, 0) });
        }

        if (row.Trigger == "manhunter")
        {
            // The pack stands well west of the hut, so the brain has the walk in hand before it arrives.
            for (var i = 0; i < 3; i++)
            {
                pawns.Add(new Fx.StagePawn { Name = PackPrefix + i.ToString(CultureInfo.InvariantCulture), Kind = "Tortoise", Faction = Fx.StageFaction.None, Manhunter = true, Cell = new(-25, 6 + (2 * i)) });
            }
        }

        return pawns;
    }

    private static async Task RunAsync(TypedSession s, Row row, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var hut = staged.Rooms["hut"].Interior.Select(c => (c.X, c.Z)).ToHashSet();
        var sheltered = staged.Pawns.Where(p => !p.Key.StartsWith(PackPrefix, StringComparison.Ordinal)).Select(p => p.Value.Id).ToList();
        var ended = false;
        var scars = new Dictionary<string, int>(StringComparer.Ordinal);
        _ = await s.ServeUntilAsync("sheltering-" + row.Trigger, s.TicksLeft, async ct =>
        {
            var safe = await SafePawnsAsync(s, row.Trigger + "-safe", ct).ConfigureAwait(false);
            var states = new List<string>();
            var allIn = true;
            var noneIn = true;
            foreach (var id in sheltered)
            {
                var pawn = await TypedPawnOps.PawnAsync(s, row.Trigger + "-pawn", id, ct).ConfigureAwait(false);
                if (pawn is null || pawn.Dead)
                {
                    throw new AcceptanceException($"{row.Trigger}: {id} died or left the map: {pawn}");
                }

                var injuries = pawn.Health.Hediffs.Items.Count(h => Injuries.Contains(h.DefName));
                if (injuries > (scars.TryAdd(id, injuries) ? injuries : scars[id]))
                {
                    throw new AcceptanceException($"{row.Trigger}: {id} was harmed ({injuries} injuries, {scars[id]} at the start): {pawn}");
                }

                var restricted = safe.Contains(id);
                var inHut = pawn.Pawn.Place is RimGovernor.Contracts.Wire.Observations.EntityPlace.OnMap { Cell: { X: var x, Z: var z } } && hut.Contains((x, z));
                allIn &= restricted && inHut;
                noneIn &= !restricted;
                states.Add(string.Create(CultureInfo.InvariantCulture, $"{id}:{restricted}:{inHut}:{pawn.Pawn.Place}"));
            }

            if (!ended && allIn)
            {
                s.Record(row.Trigger + "-sheltered", string.Join(' ', states));
                _ = await s.FixtureAsync(row.Trigger + "-end", End, NoArgs.Value, ct).ConfigureAwait(false);
                ended = true;
                return ("ended " + string.Join(' ', states), false);
            }

            return ($"{ended} {string.Join(' ', states)}", ended && noneIn);
        }, cancellationToken).ConfigureAwait(false);
        s.Record(row.Trigger + "-restored", true);
    }

    /// <summary>The pawns the colony facts list as restricted to the Safe area; none when the area is gone.</summary>
    private static async Task<HashSet<string>> SafePawnsAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var facts = await s.CallAsync(label, NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        return facts.Policies.AllowedAreas.Items
            .Where(a => a.Label == ShelterRules.SafeAreaLabel)
            .SelectMany(a => a.Pawns.Items.Select(p => p.Value))
            .ToHashSet(StringComparer.Ordinal);
    }
}
