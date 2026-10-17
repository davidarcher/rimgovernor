using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/animals</c> and <c>combatlab/orphan-animal-area</c> (Go <c>animals.go</c>).</summary>
internal static class CombatLabAnimals
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/animals",
        Scope = "combat.orders animal orders (#1057), native op contracts no snapshot can prove: on lab-manhunter one call releases the trained husky "
            + "at a warg (applied, AttackMelee), releases the untrained husky (untrained), zones a warg (not_ours) and zones the untrained husky onto one "
            + "cell (applied). One tick later the trained husky attacks the warg and the other stands restricted to a one-cell area; a clear order then "
            + "restores it to no area.",
        Start = new LabStart { Colonists = 1 },
        RequiredOps = [DebugGame.LabStartTool, FixtureStage.Tool, CombatFixtures.LabTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    internal static AcceptanceCase OrphanCase() => new()
    {
        Name = "combatlab/orphan-animal-area",
        Scope = "Native area deletion after death, despawn and lost ownership; Go snapshots cannot prove native areaManager effects. Repeated clears model a fresh cleanup after a lost reply.",
        Start = new LabStart { Colonists = 1 },
        RequiredOps = [DebugGame.LabStartTool, FixtureStage.Tool, CombatFixtures.LabTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunOrphanAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var report = s.Report;
        var cx = 0;
        var cz = 0;
        var staged = await CombatStage.StageAsync(h, "lab-manhunter", cancellationToken, (_, x, z) =>
        {
            cx = x;
            cz = z;
        }).ConfigureAwait(false);
        var animals = staged.Animals();
        var wargs = staged.Manhunters();
        if (animals.Count != 2 || wargs.Count != 2)
        {
            throw new AcceptanceException($"staged {CaseSupport.Tag(animals.Count)} animals and {CaseSupport.Tag(wargs.Count)} manhunters, want 2 and 2");
        }

        var trained = animals[0];
        var pup = animals[1];
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "combat-animals-acquire", identity, cancellationToken).ConfigureAwait(false);
        var results = await LabOps.IssueAsync(
            h,
            identity,
            "combat-animals-1",
            new JsonArray(
                new JsonObject { ["pawn"] = LabOps.PawnTarget(trained), ["release"] = LabOps.PawnTarget(wargs[0]) },
                new JsonObject { ["pawn"] = LabOps.PawnTarget(pup), ["release"] = LabOps.PawnTarget(wargs[0]) },
                new JsonObject { ["pawn"] = LabOps.PawnTarget(wargs[1]), ["animalArea"] = new JsonObject { ["cell"] = CaseSupport.Cell(cx - 1, cz - 3) } },
                new JsonObject { ["pawn"] = LabOps.PawnTarget(pup), ["animalArea"] = new JsonObject { ["cell"] = CaseSupport.Cell(cx - 1, cz - 3) } }),
            cancellationToken).ConfigureAwait(false);
        report["results"] = new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())]);
        if (results[0]["applied"].AsBool() != true || results[0]["jobDef"].AsString() != "AttackMelee")
        {
            throw new AcceptanceException($"release: want applied with AttackMelee, got {results[0].ToJson()}");
        }

        foreach (var (i, want) in new[] { (1, "untrained"), (2, "not_ours") })
        {
            if (results[i]["applied"].AsBool() == true || results[i]["refusal"].AsString() != want)
            {
                throw new AcceptanceException($"order {CaseSupport.Tag(i)}: want refusal {want}, got {results[i].ToJson()}");
            }
        }

        if (results[3]["applied"].AsBool() != true)
        {
            throw new AcceptanceException($"animal_area: want applied, got {results[3].ToJson()}");
        }

        var after = await LabOps.TickReadAsync(h, cancellationToken).ConfigureAwait(false);
        report["after"] = after.PawnsJson();
        var husky = after.Row(trained);
        if (husky["job"].AsString() != "AttackMelee" || husky["jobThing"].AsString() != wargs[0])
        {
            throw new AcceptanceException($"released husky does not attack the warg: {husky.ToJson()}");
        }

        var zoned = after.Row(pup);
        if (zoned["area"].AsString() != "Combat " + pup || (int)zoned["areaCells"].AsNumber() != 1)
        {
            throw new AcceptanceException($"zoned husky not restricted to one cell: {zoned.ToJson()}");
        }

        results = await LabOps.IssueAsync(
            h,
            identity,
            "combat-animals-2",
            new JsonArray(new JsonObject { ["pawn"] = LabOps.PawnTarget(pup), ["animalArea"] = new JsonObject { ["clear"] = new JsonObject() } }),
            cancellationToken).ConfigureAwait(false);
        report["clear"] = new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())]);
        if (results[0]["applied"].AsBool() != true)
        {
            throw new AcceptanceException($"animal_area clear: want applied, got {results[0].ToJson()}");
        }

        var cleared = await LabOps.TickReadAsync(h, cancellationToken).ConfigureAwait(false);
        var restored = cleared.Row(pup);
        if (restored["area"].AsString() != string.Empty)
        {
            throw new AcceptanceException($"cleared husky still restricted: {restored.ToJson()}");
        }
    }

    private static async Task RunOrphanAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        foreach (var mode in new[] { "despawn", "dead", "foreign" })
        {
            var cx = 0;
            var cz = 0;
            var staged = await CombatStage.StageAsync(h, "lab-manhunter", cancellationToken, (_, x, z) =>
            {
                cx = x;
                cz = z;
            }).ConfigureAwait(false);
            var animal = staged.Animals()[1];
            _ = await Authority.GrantAutoAsync(h.WireFunc(), "orphan-acquire-" + mode, identity, cancellationToken).ConfigureAwait(false);
            var results = await LabOps.IssueAsync(
                h,
                identity,
                "orphan-zone-" + mode,
                new JsonArray(new JsonObject { ["pawn"] = LabOps.PawnTarget(animal), ["animalArea"] = new JsonObject { ["cell"] = CaseSupport.Cell(cx - 1, cz - 3) } }),
                cancellationToken).ConfigureAwait(false);
            if (results[0]["applied"].AsBool() != true)
            {
                throw new AcceptanceException($"zone {mode}: {new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())]).ToJson()}");
            }

            var setup = new JsonObject { ["animal"] = animal, ["mode"] = mode }.ToJsonString();
            _ = await h.CallAsync("orphan-setup-" + mode, CombatFixtures.LabTool, new JsonObject { ["action"] = "orphan-animal", ["spec"] = setup }, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < 2; i++)
            {
                var n = i.ToString(CultureInfo.InvariantCulture);
                results = await LabOps.IssueAsync(
                    h,
                    identity,
                    $"orphan-clear-{mode}-{n}",
                    new JsonArray(new JsonObject { ["pawn"] = LabOps.PawnTarget(animal), ["animalArea"] = new JsonObject { ["clear"] = new JsonObject() } }),
                    cancellationToken).ConfigureAwait(false);
                var refusal = results[0]["refusal"].AsString();
                if (refusal is not ("not_found" or "not_ours"))
                {
                    throw new AcceptanceException($"clear {mode}: {new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())]).ToJson()}");
                }

                var read = await h.CallAsync($"orphan-read-{mode}-{n}", CombatFixtures.LabTool, new JsonObject { ["action"] = "read" }, cancellationToken).ConfigureAwait(false);
                foreach (var area in read["areas"].AsSlice())
                {
                    if (area.AsString() == "Combat " + animal)
                    {
                        throw new AcceptanceException($"orphan area survived {mode} clear");
                    }
                }
            }
        }
    }
}
