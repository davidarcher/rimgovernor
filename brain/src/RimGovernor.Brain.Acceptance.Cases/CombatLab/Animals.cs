using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/animals</c> (#1057) and <c>combatlab/orphan-animal-area</c>: the combat orders for colony animals and their areas.</summary>
internal static class CombatLabAnimals
{
    /// <summary>The ways a zoned animal stops being the colony's: despawned, dead, gone to no faction.</summary>
    private static readonly string[] OrphanModes = ["despawn", "dead", "foreign"];

    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-manhunter");

    // One untrained husky per orphan mode beside the colonist.
    private static readonly LabFixture OrphanFixture = CombatLabKit.Fixture("lab-manhunter", (f, cx, cz) =>
    {
        for (var i = 0; i < OrphanModes.Length; i++)
        {
            f.Pawns.Add(new LabPawn { Side = CombatFixtures.Animal, Kind = "Husky", X = cx - 3 - i, Z = cz });
        }
    });

    /// <summary>
    /// test/lab_stage action orphan-animal: the primitive no stage spec expresses, a player animal despawned, killed or
    /// turned factionless mid-case.
    /// </summary>
    private static readonly FixtureOp<(string Animal, string Mode), Fx.FixtureDone> Orphan = new(
        CombatFixtures.LabTool,
        r => ServedOps.Args(("action", "orphan-animal"), ("spec", CombatLabKit.Obj(("animal", r.Animal), ("mode", r.Mode)).ToJsonString())),
        ServedOps.Done(CombatFixtures.LabTool));

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/animals",
        Scope = "Combat orders for animals (#1057), native op contracts no unit test reaches: on lab-manhunter one batch releases the trained "
            + "husky at a warg (applied, AttackMelee), releases the untrained husky (untrained), zones a warg (not_ours) and zones the untrained "
            + "husky onto one cell (applied); on the paused game the trained husky attacks the warg and the other is restricted to a one-cell "
            + "\"Combat <id>\" allowed area, and a clear order leaves it in no area.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    internal static TypedCase OrphanCase() => new()
    {
        Name = "combatlab/orphan-animal-area",
        Scope = "Native area deletion after death, despawn and lost ownership: a husky zoned by an animal_area order is despawned, killed or made "
            + "factionless, and two clear orders in a row (a fresh cleanup after a lost reply) each refuse not_found or not_ours while its "
            + "\"Combat <id>\" allowed area is gone from the colony read.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = OrphanFixture.Colonists },
        Stage = CombatLabKit.Spec(OrphanFixture),
        Ops = [CombatFixtures.LabTool],
        QuietWorld = true,
        Run = RunOrphanAsync,
    };

    private static RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.AnimalAreaCase Zone(RimGovernor.Brain.GameOrders.Operations.CombatAnimalAreaArea area) =>
        new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.AnimalAreaCase(new RimGovernor.Brain.GameOrders.Operations.CombatAnimalArea { Area = area });

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);
        var animals = lab.Animals();
        var wargs = lab.Manhunters();
        if (animals.Count != 2 || wargs.Count != 2)
        {
            throw new AcceptanceException($"staged {animals.Count} animals and {wargs.Count} manhunters, want 2 and 2");
        }

        var trained = animals[0];
        var pup = animals[1];
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var results = await CombatLabKit.OrdersAsync(
            s,
            "animals",
            cancellationToken,
            CombatLabKit.Order(trained, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.ReleaseCase(CombatLabKit.Pawn(wargs[0]))),
            CombatLabKit.Order(pup, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.ReleaseCase(CombatLabKit.Pawn(wargs[0]))),
            CombatLabKit.Order(wargs[1], Zone(new RimGovernor.Brain.GameOrders.Operations.CombatAnimalAreaArea.CellCase(lab.Cell(-1, -3)))),
            CombatLabKit.Order(pup, Zone(new RimGovernor.Brain.GameOrders.Operations.CombatAnimalAreaArea.CellCase(lab.Cell(-1, -3))))).ConfigureAwait(false);
        CombatLabKit.Applied("release", results[0], "AttackMelee");
        CombatLabKit.Refused("release untrained", results[1], "untrained");
        CombatLabKit.Refused("zone warg", results[2], "not_ours");
        CombatLabKit.Applied("zone pup", results[3]);

        var husky = await CombatLabKit.PawnAsync(s, "husky", trained, cancellationToken).ConfigureAwait(false);
        if (CombatLabKit.Job(husky) != "AttackMelee" || CombatLabKit.JobThing(husky) != wargs[0])
        {
            throw new AcceptanceException($"released husky does not attack warg {wargs[0]}: {husky.Job}");
        }

        var area = await PupAreaAsync(s, "zoned", pup, cancellationToken).ConfigureAwait(false);
        if (area is not { CellCount: 1 } || area.Label != "Combat " + pup)
        {
            throw new AcceptanceException($"zoned husky not restricted to a one-cell \"Combat {pup}\" area: {area}");
        }

        CombatLabKit.Applied("clear", (await CombatLabKit.OrdersAsync(s, "clear", cancellationToken, CombatLabKit.Order(pup, Zone(new RimGovernor.Brain.GameOrders.Operations.CombatAnimalAreaArea.ClearCase()))).ConfigureAwait(false))[0]);
        if (await PupAreaAsync(s, "cleared", pup, cancellationToken).ConfigureAwait(false) is { } left)
        {
            throw new AcceptanceException($"cleared husky still restricted to {left}");
        }
    }

    // The allowed area the pawn is restricted to: the area listing it, which its own settings name.
    private static async Task<RimGovernor.Brain.GameFacts.Observations.AllowedArea?> PupAreaAsync(TypedSession s, string label, string pup, CancellationToken cancellationToken)
    {
        var areas = await CombatLabKit.AreasAsync(s, label + "-areas", cancellationToken).ConfigureAwait(false);
        s.RecordWireRows(label + "_areas", areas);
        var listing = areas.Where(a => a.Pawns.Any(p => p.Value == pup)).ToList();
        var settings = (await CombatLabKit.PawnAsync(s, label + "-pup", pup, cancellationToken).ConfigureAwait(false)).Settings.AllowedAreaId;
        if (listing.Count > 1 || listing.FirstOrDefault()?.Id != settings)
        {
            throw new AcceptanceException($"{label}: pup {pup} settings name area {settings ?? "<none>"}, the colony read lists it under [{string.Join(", ", listing.Select(a => a.Id))}]");
        }

        return listing.FirstOrDefault();
    }

    private static async Task RunOrphanAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(OrphanFixture, s.Staged!);
        var huskies = lab.Animals().Skip(2).ToList();
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var zoned = await CombatLabKit.OrdersAsync(
            s,
            "zone",
            cancellationToken,
            [.. huskies.Select((h, i) => CombatLabKit.Order(h, Zone(new RimGovernor.Brain.GameOrders.Operations.CombatAnimalAreaArea.CellCase(lab.Cell(-1 - i, -3)))))]).ConfigureAwait(false);
        foreach (var r in zoned)
        {
            CombatLabKit.Applied("zone", r);
        }

        for (var m = 0; m < OrphanModes.Length; m++)
        {
            var mode = OrphanModes[m];
            var animal = huskies[m];
            _ = await s.FixtureAsync("orphan-" + mode, Orphan, (animal, mode), cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < 2; i++)
            {
                var label = $"clear-{mode}-{i}";
                var r = (await CombatLabKit.OrdersAsync(s, label, cancellationToken, CombatLabKit.Order(animal, Zone(new RimGovernor.Brain.GameOrders.Operations.CombatAnimalAreaArea.ClearCase()))).ConfigureAwait(false))[0];
                if (r.Applied == true || r.Refusal is not ("not_found" or "not_ours"))
                {
                    throw new AcceptanceException($"{label}: want refusal not_found or not_ours, got {r}");
                }

                var areas = await CombatLabKit.AreasAsync(s, label + "-areas", cancellationToken).ConfigureAwait(false);
                if (areas.FirstOrDefault(a => a.Label == "Combat " + animal) is { } survived)
                {
                    throw new AcceptanceException($"orphan area survived the {mode} clear: {survived}");
                }
            }
        }
    }
}
