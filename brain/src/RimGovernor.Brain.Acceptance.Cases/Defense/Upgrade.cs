using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using JournalStore = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>defense/tier-upgrade</c> (Go <c>upgrade.go</c>): raises the threat tier under a built layout. On the baseline the
/// fixture opens the turret tier's power gates and the perimeter layout is built with the storyteller's raid points on the
/// quiet 35-point floor, so its turret tier holds mini turrets. The fixture then raises the difficulty threat scale until
/// the raid points pass the machining threshold, finishes Smithing, Machining and HeavyTurrets, and stands a fuelled smithy
/// with steel, plasteel and components. Under the served layout, armory and gear families the layout planner replaces a
/// mini turret in place with a higher rung (natively standing on its anchor), and a colonist ends up wielding a weapon of a
/// higher armory tier than any colonist held before the rise.
/// </summary>
internal static class DefenseUpgrade
{
    /// <summary>The raid points the fixture raises the threat to: past the machining threshold (800).</summary>
    private const int UpgradePoints = 1000;

    /// <summary>Bounds the mini turrets' build after the layout.</summary>
    private static readonly TimeSpan s_turretsTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Bounds the in-place replacement after the rise (a deconstruct and a 2x2 build).</summary>
    private static readonly TimeSpan s_replaceTimeout = TimeSpan.FromMinutes(45);

    /// <summary>One served round of the armory's weapon bill and the wear.</summary>
    private static readonly TimeSpan s_gearRound = TimeSpan.FromMinutes(10);

    private const int GearRounds = 3;

    internal static AcceptanceCase Case() => new()
    {
        Name = "defense/tier-upgrade",
        Scope = "A threat-tier rise upgrades turrets and gear (#1204, #1210): after the perimeter layout's mini turrets are built at the 35-point floor, the "
            + "fixture raises raid points past 800 and finishes Smithing, Machining and HeavyTurrets beside a fuelled smithy; the layout planner replaces a mini "
            + "turret in place with a higher rung that then stands natively, and a colonist wields a higher armory-tier weapon than any colonist held before. Native: the deconstruct, build, craft and wear outcomes end to end.",
        Start = new SaveStart { Name = Baselines.BaselineName },
        Serve = new ServeSpec { Families = [.. DefensePerimeter.PerimeterFamilies, "armory", "gear"], Prefix = "defense-upgrade" },
        NoCheckpoint = true,
        Budget = TimeSpan.FromHours(3),
        Crew = new Crew { Size = 3 },
        Reason = "the turret tier stands on the perimeter layout's killbox, which only the native campaign builds",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        foreach (var want in new[] { "test/defense_setup", "test/guarded_construction_prepare", DebugGame.LabSpawnTool })
        {
            if (!Dyn.Contains(s.Names, want))
            {
                throw new AcceptanceException($"missing {want} in discovery; rebuild the native mod with -Fixture DefenseFixture,GuardedConstructionFixture");
            }
        }

        var world = DefensePerimeter.WorldOf(identity);

        // h is rebound after every service phase, so the fixture reads the current harness.
        async Task<JsonObject> FixtureAsync(string label, JsonObject args)
        {
            var output = await h.CallAsync(label, "test/defense_setup", args, cancellationToken).ConfigureAwait(false);
            return output["success"].AsBool() == true ? output : throw new AcceptanceException($"defense_setup {args["op"].AsString()} refused: {output.ToJson()}");
        }

        _ = await FixtureAsync("stock", new JsonObject { ["op"] = "stock" }).ConfigureAwait(false);
        var (siteX, siteZ) = await DefensePerimeter.PrepareSiteAsync(h, identity, report, cancellationToken).ConfigureAwait(false);
        var power = await FixtureAsync("power", new JsonObject { ["op"] = "power", ["x"] = siteX, ["z"] = siteZ }).ConfigureAwait(false);
        report["power"] = power.DeepClone();

        DefenseTierRecord minis;
        await using (var svc = await DefenseService.LaunchAsync(s, "layout", identity, cancellationToken).ConfigureAwait(false))
        {
            _ = await DefensePerimeter.InPhaseAsync("layout", () => DefensePerimeter.WaitLayoutCompleteAsync(svc.Store, world, svc.Budget(DefensePerimeter.LayoutTimeout), report, cancellationToken)).ConfigureAwait(false);
            minis = await DefensePerimeter.InPhaseAsync("mini turrets", () => WaitTurretTierAsync(svc.Store, world, svc.Budget(s_turretsTimeout), report, "turrets_built", t => t.Built && t.Buildings.Count > 0, cancellationToken)).ConfigureAwait(false);
            foreach (var b in minis.Buildings)
            {
                if (b.Definition != PolicyFunctions.TurretMini)
                {
                    throw new AcceptanceException($"turret tier at the 35-point floor holds {b.Definition}, want only {PolicyFunctions.TurretMini}: {DefensePerimeter.TierJson(minis).ToJson()}");
                }
            }

            await svc.StopAsync().ConfigureAwait(false);
        }

        h = await SessionReattach.PausedAsync(s, cancellationToken).ConfigureAwait(false);
        var before = await FixtureAsync("inspect-before-rise", new JsonObject { ["op"] = "inspect" }).ConfigureAwait(false);
        report["inspect_before_rise"] = before.DeepClone();
        var armory = await FixtureAsync("armory", new JsonObject { ["op"] = "armory", ["x"] = siteX, ["z"] = siteZ, ["points"] = UpgradePoints }).ConfigureAwait(false);
        report["armory"] = armory.DeepClone();
        var catalog = await ArmoryCatalog.ReadAsync(h, identity, cancellationToken).ConfigureAwait(false);
        var topBefore = catalog.TopTier(before);

        DefenseBuilding rung;
        await using (var svc = await DefenseService.LaunchAsync(s, "upgrade", identity, cancellationToken).ConfigureAwait(false))
        {
            var upgraded = await DefensePerimeter.InPhaseAsync("turret replacement", () => WaitTurretTierAsync(svc.Store, world, svc.Budget(s_replaceTimeout), report, "turrets_upgraded", t => t.Built && HigherRung(t) is not null, cancellationToken)).ConfigureAwait(false);
            rung = HigherRung(upgraded)!;
            if (!minis.Buildings.Any(b => b.Cell == rung.Cell))
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"higher rung {rung.Definition} at {rung.Cell.X},{rung.Cell.Z} is not on a former mini turret's anchor {DefensePerimeter.TierJson(minis).ToJson()}"));
            }

            await svc.StopAsync().ConfigureAwait(false);
        }

        for (var round = 1; ; round++)
        {
            h = await SessionReattach.PausedAsync(s, cancellationToken).ConfigureAwait(false);
            var after = await FixtureAsync(string.Create(CultureInfo.InvariantCulture, $"inspect-after-rise-{round}"), new JsonObject { ["op"] = "inspect" }).ConfigureAwait(false);
            report["inspect_after_rise"] = after.DeepClone();
            if (round == 1 && !TurretStanding(after, rung.Definition, rung.Cell.X, rung.Cell.Z))
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"recorded {rung.Definition} at {rung.Cell.X},{rung.Cell.Z} is not standing natively: {after["turrets"].ToJson()}"));
            }

            var top = catalog.TopTier(after);
            if (top > topBefore)
            {
                report["gear"] = new JsonObject { ["before"] = topBefore.ToString(), ["after"] = top.ToString(), ["rounds"] = round };
                return;
            }

            if (round == GearRounds)
            {
                throw new AcceptanceException($"no colonist wields a weapon above the {topBefore} tier held before the rise after {GearRounds} rounds: {after["colonists"].ToJson()}");
            }

            await using var svc = await DefenseService.LaunchAsync(s, string.Create(CultureInfo.InvariantCulture, $"gear-{round}"), identity, cancellationToken).ConfigureAwait(false);
            await Task.Delay(s_gearRound, cancellationToken).ConfigureAwait(false);
            if (svc.Process.Exited() is { } exited)
            {
                throw new AcceptanceException($"gear service exited: {exited.Message}", exited);
            }

            await svc.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Polls the stored layout until its turret tier satisfies <paramref name="done"/>, and returns that tier.</summary>
    private static async Task<DefenseTierRecord> WaitTurretTierAsync(JournalStore s, World world, Wait w, Report report, string key, Func<DefenseTierRecord, bool> done, CancellationToken cancellationToken)
    {
        DefenseTierRecord tier = new();
        try
        {
            await w.WaitProgressAsync(async ct =>
            {
                var record = await s.LoadDefenseLayoutAsync(world, ct).ConfigureAwait(false);
                if (record is null)
                {
                    return ("", false);
                }

                var (found, _, ok) = record.Tier(PolicyFunctions.TierTurrets);
                if (ok)
                {
                    tier = found;
                    if (done(tier))
                    {
                        report[key] = DefensePerimeter.TierJson(tier);
                        return ("", true);
                    }
                }

                return (new JsonArray([.. record.Tiers.Select(t => (JsonNode?)DefensePerimeter.TierJson(t))]).ToJson(), false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException(e.Message, e);
        }

        return tier;
    }

    /// <summary>The tier's first turret above the mini turret, if any.</summary>
    private static DefenseBuilding? HigherRung(DefenseTierRecord t)
    {
        foreach (var b in t.Buildings)
        {
            if (PolicyFunctions.TurretRank(b.Definition) > PolicyFunctions.TurretRank(PolicyFunctions.TurretMini))
            {
                return b;
            }
        }

        return null;
    }

    private static bool TurretStanding(JsonObject inspect, string def, int x, int z)
    {
        foreach (var raw in inspect["turrets"].AsSlice())
        {
            var row = raw.Obj();
            if (row["def"].AsString() == def && (int)row["x"].AsNumber() == x && (int)row["z"].AsNumber() == z)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The armory ladder rung of a weapon def, read off the wire definition catalog (the part of Go
/// <c>DefinitionCatalog.ArmoryWeaponTier</c> the tier-upgrade case needs). The Go catalog derives the rung from the
/// recipes that make the weapon; the out-of-process harness has no recipe model, so the rung here is the def's own
/// tech level mapped onto the ladder (animal and neolithic, medieval, industrial, spacer), which orders the same weapons
/// the case compares.
/// </summary>
internal sealed class ArmoryCatalog
{
    private readonly Dictionary<string, ArmoryTier> _rungs;

    private ArmoryCatalog(Dictionary<string, ArmoryTier> rungs) => _rungs = rungs;

    /// <summary>Reads the catalog once for the colony the identity names.</summary>
    internal static async Task<ArmoryCatalog> ReadAsync(Harness h, JsonObject identity, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            "armory-definition-catalog",
            "observations_read_definition_catalog",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } },
            cancellationToken).ConfigureAwait(false);
        var observed = reply["observed"].AsMap() ?? throw new AcceptanceException($"definition catalog not observed: {reply.ToJson()}");
        var rungs = new Dictionary<string, ArmoryTier>(StringComparer.Ordinal);
        Collect(observed, rungs);
        return new ArmoryCatalog(rungs);
    }

    /// <summary>The highest armory tier among the colonists' primaries.</summary>
    internal ArmoryTier TopTier(JsonObject inspect)
    {
        var top = ArmoryTier.Unknown;
        foreach (var raw in inspect["colonists"].AsSlice())
        {
            var primary = raw.Obj()["primary"].AsString();
            if (primary.Length == 0)
            {
                continue;
            }

            if (_rungs.TryGetValue(primary, out var tier) && tier > top)
            {
                top = tier;
            }
        }

        return top;
    }

    // Def rows carry a defName and a techLevel; the catalog groups rows by def message, so walk the document.
    private static void Collect(JsonNode? node, Dictionary<string, ArmoryTier> rungs)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["defName"].AsString() is { Length: > 0 } name && o["techLevel"] is { } level && Rung(level) is { } rung)
                {
                    rungs.TryAdd(name, rung);
                }

                foreach (var (_, child) in o)
                {
                    Collect(child, rungs);
                }

                break;
            case JsonArray a:
                foreach (var child in a)
                {
                    Collect(child, rungs);
                }

                break;
        }
    }

    // The wire enum is TECH_LEVEL_* (or its number: Animal 1, Neolithic 2, Medieval 3, Industrial 4, Spacer 5).
    private static ArmoryTier? Rung(JsonNode level)
    {
        var text = level.AsString();
        if (text.Length == 0)
        {
            return (int)level.AsNumber() switch
            {
                1 or 2 => ArmoryTier.Neolithic,
                3 => ArmoryTier.Smithing,
                4 => ArmoryTier.Machining,
                5 => ArmoryTier.Fabrication,
                _ => null,
            };
        }

        var upper = text.ToUpperInvariant();
        if (upper.EndsWith("ANIMAL", StringComparison.Ordinal) || upper.EndsWith("NEOLITHIC", StringComparison.Ordinal))
        {
            return ArmoryTier.Neolithic;
        }

        if (upper.EndsWith("MEDIEVAL", StringComparison.Ordinal))
        {
            return ArmoryTier.Smithing;
        }

        if (upper.EndsWith("INDUSTRIAL", StringComparison.Ordinal))
        {
            return ArmoryTier.Machining;
        }

        return upper.EndsWith("SPACER", StringComparison.Ordinal) ? ArmoryTier.Fabrication : null;
    }
}
