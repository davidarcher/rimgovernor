using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Contracts.Wire.Clock;
using JournalStore = RimGovernor.Brain.Store.Store;
using StoreRounds = RimGovernor.Brain.Store.Rounds;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Calls one test/defense_setup op, refusing an unsuccessful one (Go <c>fixtureFunc</c>).</summary>
internal delegate Task<JsonObject> DefenseFixture(string label, JsonObject args);

/// <summary>
/// One perimeter-campaign scenario (Go <c>variant</c>): the edge raid staged after the layout (its
/// RaidStrategyDef/PawnsArrivalModeDef), and the hooks a case built on the campaign adds: <see cref="Gates"/> stages extra
/// layout gates before the layout is built, <see cref="LayoutBuilt"/> audits the built layout against the native inspect
/// after it, and <see cref="Raided"/> compares that inspect with the one taken as soon as the raid resolved.
/// </summary>
internal sealed record DefenseVariant
{
    internal string Strategy { get; init; } = "";

    internal string Arrival { get; init; } = "";

    internal string Threat { get; init; } = "";

    internal Func<DefenseFixture, int, int, Task<JsonObject>>? Gates { get; init; }

    /// <summary>Throws when the built layout fails the native inspect.</summary>
    internal Action<DefenseLayoutRecord, JsonObject>? LayoutBuilt { get; init; }

    /// <summary>Throws when the raid left the colony in a state the case does not accept.</summary>
    internal Action<JsonObject, JsonObject>? Raided { get; init; }

    /// <summary>
    /// Has the fixture raise every wall, door and embrasure as it is placed (the perimeter is ~1600 walls: hauling and
    /// building them took 36 wall minutes of game time, the layout itself is what the case proves).
    /// </summary>
    internal bool InstantWalls { get; init; }
}

/// <summary>
/// <c>defense/perimeter</c> (Go <c>defense.go</c>): the defensive-layout vertical and perimeter end to end against a live
/// game and the in-process brain. On the tribal8 baseline with stone blocks stocked, the layout planner anchors its
/// corridor on the layout plan's killbox opening, builds every tier natively (firing line, funnel, trap corridor) and the
/// plan's 3-thick stone perimeter wall with its 3-door gates, section by section. The stored layout is re-verified by an
/// independent native spatial-access read with every wall and barricade blocked and by a native inspection that no
/// colonist stands on a trap. A real RaidEnemy edge assault is then raised at the opening and the defense planner must
/// open one fight (method "combat-...") formed as hold-the-line. The service then holds the raid itself, and the run
/// waits for the goal to recover, then for the aftermath: the hold plan's drafts are released and the layout planner
/// re-admits every tier the raid degraded until the stored record is verified standing again within a bounded number of
/// ticks. The repair is the planner's own: before the repair service starts, the fixture switches the game's
/// auto-rebuild off, removes its pending trap blueprints and vanishes a surviving trap, so every missing trap can only be
/// replaced by a re-admitted tier method.
/// </summary>
internal static class DefensePerimeter
{
    /// <summary>
    /// Bounds the native layout build (the killbox's tiers and every perimeter section) in wall time.
    /// </summary>
    internal static readonly TimeSpan LayoutTimeout = TimeSpan.FromMinutes(150);

    /// <summary>Bounds the raid response and resolution.</summary>
    private static readonly TimeSpan RaidTimeout = TimeSpan.FromMinutes(8);

    /// <summary>Bounds the post-raid draft release and layout repair in wall time (raid injuries are tended first: CriticalMedical suspends the layout goal).</summary>
    private static readonly TimeSpan RepairTimeout = TimeSpan.FromMinutes(15);

    /// <summary>The same repair bound in game ticks from the tick the raid resolved (one day).</summary>
    private const long RepairTicks = 60000;

    /// <summary>Blocks of granite cover the wall: 3 cells thick round the core, the yard and the fields, 5 blocks a wall and 25 a door.</summary>
    private const int PerimeterStone = 14000;

    /// <summary>
    /// Wood is what the killbox funnel, trap corridor, fences, doors and floors are built from: the case serves no
    /// resource family, so nothing fells trees and an unstocked colony leaves the funnel's blueprints waiting for wood
    /// that never comes.
    /// </summary>
    private const int WoodLogs = 1500;

    /// <summary>Bounds how long one set of placed tier blueprints counts as progress while the clock advances: three game days.</summary>
    private const long LayoutBuildTicks = 3 * 60000;

    /// <summary>
    /// The families a perimeter campaign serves. Work staffs the layout's construction (every building method's builder
    /// check needs the work priorities only that family applies). Fire belongs here because a raid can leave a home fire
    /// burning on the corridor, and MaintainFireSafety is a priority-1 emergency whose only method is a short
    /// native-firefighting window; without the family nothing admits one, so the fire holds every other goal suspended.
    /// Supply belongs here too: a dead raider's drops lie in the trap lane, where the hauling safety verdict flaps unsafe,
    /// and ManageSupplySafety is then a priority-0 emergency that only the supplies planner clears.
    /// </summary>
    internal static readonly string[] PerimeterFamilies = ["defensive-layout", "defense", "tend", "rescue", "fire", "supply", "work"];

    private static readonly DefenseVariant s_raid = new() { Strategy = "ImmediateAttack", Arrival = "EdgeWalkIn", Threat = "raid" };

    internal static AcceptanceCase Case() => new()
    {
        Name = "defense/perimeter",
        Scope = "Layout v2 perimeter (#789) and the defensive layout vertical (#5 M4): the routine planner anchors the corridor on the layout plan's killbox "
            + "opening and builds every tier and the 3-thick stone wall with its 3-door gates natively; independent spatial-access and trap-cell reads "
            + "prove colonists pass the gates; a real RaidEnemy edge assault walks to the opening and is answered with hold-the-line, and afterwards "
            + "the defenders are undrafted and the layout is repaired to its audited state (#72).",
        // The baseline save keeps the site deterministic.
        Start = new SaveStart { Name = Baselines.BaselineName },
        Serve = new ServeSpec { Families = PerimeterFamilies, Prefix = "defense" },
        Budget = TimeSpan.FromHours(3),
        Crew = new Crew { Size = 20 },
        Reason = "the whole perimeter's build, the raid answer and the repair are one native campaign",
        Run = (s, cancellationToken) => RunVariantAsync(s, s_raid, cancellationToken),
    };

    /// <summary>The perimeter campaign under <paramref name="v"/> (Go <c>run</c>).</summary>
    internal static async Task RunVariantAsync(ISession s, DefenseVariant v, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(v);
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        report["raid"] = new JsonObject { ["strategy"] = v.Strategy, ["arrival"] = v.Arrival, ["threat"] = v.Threat };
        var world = WorldOf(identity);
        foreach (var want in new[] { "test/defense_setup", "test/guarded_construction_prepare", DebugGame.LabSpawnTool })
        {
            if (!Dyn.Contains(s.Names, want))
            {
                throw new AcceptanceException($"missing {want} in discovery; rebuild the native mod with -Fixture DefenseFixture,GuardedConstructionFixture");
            }
        }

        // h is rebound after every service phase, so the fixture reads the current harness.
        async Task<JsonObject> FixtureAsync(string label, JsonObject args)
        {
            var output = await h.CallAsync(label, "test/defense_setup", args, cancellationToken).ConfigureAwait(false);
            return output["success"].AsBool() == true ? output : throw new AcceptanceException($"defense_setup {args["op"].AsString()} refused: {output.ToJson()}");
        }

        JsonObject Op(string op) => new() { ["op"] = op };

        var layout = new DefenseLayoutRecord();
        // The band, the stock, the rifles and the construction site are staged here, in the run body, so a resume from the
        // ring finds them in the save already: the resumed entry's state names the site the fresh run chose, and the prep is
        // skipped rather than laid again on the colonists' shifted centre.
        var staged = false;
        if (s.Resumed is { } entry && entry.State?["fixture"].AsMap() is { } resumedFixture)
        {
            staged = true;
            report["fixture_resumed"] = resumedFixture.DeepClone();
        }

        if (v.InstantWalls)
        {
            _ = await FixtureAsync("instant-shells", Op("instant-shells")).ConfigureAwait(false);
        }

        if (!staged)
        {
            _ = await FixtureAsync("stock", Op("stock")).ConfigureAwait(false);
            var ranged = await FixtureAsync("ranged", new JsonObject { ["op"] = "ranged", ["rifles"] = 3 }).ConfigureAwait(false);
            report["ranged"] = ranged.DeepClone();
            var (siteX, siteZ) = await PrepareSiteAsync(h, identity, report, cancellationToken).ConfigureAwait(false);
            if (v.Gates is not null)
            {
                var gates = await v.Gates(FixtureAsync, siteX, siteZ).ConfigureAwait(false);
                report["gates"] = gates.DeepClone();
            }

            var before = await FixtureAsync("inspect-before", Op("inspect")).ConfigureAwait(false);
            report["inspect_before"] = before.DeepClone();
            if ((int)before["traps"].AsNumber() != 0)
            {
                throw new AcceptanceException($"fresh colony already has traps: {before.ToJson()}");
            }

            Checkpoints.SetState("fixture", new JsonObject { ["siteX"] = siteX, ["siteZ"] = siteZ });
        }

        // A resumed store may already hold the finished layout (the run failed past it): then the layout service has nothing to build.
        if (staged)
        {
            using var stored = ControllerJournal.OpenStore(s, cancellationToken);
            var resumed = await stored.LoadDefenseLayoutAsync(world, cancellationToken).ConfigureAwait(false);
            if (resumed is { Complete: true })
            {
                layout = resumed;
                report["layout_record"] = LayoutJson(layout);
            }
        }

        if (!layout.Complete)
        {
            // Scenario 1: the layout is planned on the constrained site and every tier is built natively under the live
            // rounder/planner.
            await using var svc = await DefenseService.LaunchAsync(s, "layout", identity, cancellationToken).ConfigureAwait(false);
            layout = await InPhaseAsync("layout", () => WaitLayoutCompleteAsync(svc.Store, world, svc.Budget(LayoutTimeout), report, cancellationToken)).ConfigureAwait(false);
            await svc.StopAsync().ConfigureAwait(false);
            report["layout_authority"] = svc.Authority?.DeepClone();
            h = await SessionReattach.PausedAsync(s, cancellationToken).ConfigureAwait(false);
        }

        // The opening the layout was proposed on is the plan's killbox node.
        var opening = await PlanOpeningAsync(s, world, cancellationToken).ConfigureAwait(false);
        // The audits below are cheap reads proving the layout stands.
        var after = await FixtureAsync("inspect-after-layout", Op("inspect")).ConfigureAwait(false);
        report["inspect_after_layout"] = after.DeepClone();
        var (trapTier, _, _) = layout.Tier(PolicyFunctions.TierTrapCorridor);
        var wantTraps = trapTier.Buildings.Count(b => b.Definition == "TrapSpike");
        var nativeTraps = (int)after["traps"].AsNumber();
        if (nativeTraps < wantTraps || nativeTraps == 0)
        {
            throw new AcceptanceException($"native trap count {nativeTraps} below the layout's {wantTraps}");
        }

        if (after["colonistsOnTraps"].AsSlice() is { Count: > 0 } onTraps)
        {
            throw new AcceptanceException($"colonists standing on trap cells after layout: {new JsonArray([.. onTraps.Select(n => n?.DeepClone())]).ToJson()}");
        }

        v.LayoutBuilt?.Invoke(layout, after);

        // Independent audit: with every wall, fence and barricade of the layout blocked (traps stay walkable for colonists,
        // the safe lane's doors open for them and the shooter floors are terrain) every colonist still reaches the entry and
        // every colony door, and nobody lost a cell.
        var impassable = new List<Cell>();
        var seen = new HashSet<Cell>();
        // A Fence cell is PassThroughOnly: native pathing lets a colonist through, but the audit projects over Walkable()
        // cells, which exclude it, so a fenced target is held to the native CanReach alone.
        var fenced = new HashSet<Cell>();
        foreach (var tier in layout.Tiers)
        {
            foreach (var b in tier.Buildings)
            {
                // A Fence is PassThroughOnly (the killbox lane's): only roamers are stopped, so colonists pass it.
                if (b.Definition == "Fence")
                {
                    _ = fenced.Add(b.Cell);
                }

                if (b.Definition is not ("TrapSpike" or "Door" or "WoodPlankFloor" or "Fence") && seen.Add(b.Cell))
                {
                    impassable.Add(b.Cell);
                }
            }
        }

        var targets = new List<Cell> { opening.Entry };
        targets.AddRange(layout.Entrances);
        var access = await h.WireAsync("spatial-access", "observations_read_spatial_access", new JsonObject
        {
            ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
            ["blockedCells"] = CellsJson(impassable),
            ["targetCells"] = CellsJson(targets),
        }, cancellationToken).ConfigureAwait(false);
        // The three audits below read one finished layout and do not depend on each other, so each is recorded and the run
        // goes on: one run lists every stale or failing check.
        _ = report.Expect("spatial access after layout", AssertAccess(access, fenced));
        report["spatial_access_after_layout"] = access.DeepClone();
        // Cover for the firing line: every firing cell sees some cell of the trap lane raiders must walk, the chokepoint
        // mouth is covered, and the line has cover. Flank cells sit behind the funnel walls, so the mouth itself is only
        // visible from the centre.
        var fire = await h.WireAsync("lines-of-fire", "observations_read_lines_of_fire", new JsonObject
        {
            ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
            ["firingCells"] = CellsJson(layout.Firing),
            ["approachCells"] = CellsJson(layout.TrapLane),
        }, cancellationToken).ConfigureAwait(false);
        JsonObject observedFire;
        try
        {
            observedFire = Harness.Outcome(fire, "observed").Value;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"lines of fire after layout: {e.Message}", e);
        }

        var lines = observedFire["lines"].AsSlice();
        report["lines_of_fire_after_layout"] = new JsonArray([.. lines.Select(l => l?.DeepClone())]);
        _ = report.Expect("lines of fire after layout", AssertCover(lines, layout));
        // The perimeter stood with its gates: the access audit above blocked every wall of it, so colonists reached the
        // entry through the doors.
        int sections = 0, walls = 0, doors = 0;
        foreach (var tier in layout.Tiers)
        {
            if (!PolicyFunctions.IsPerimeterTier(tier.Name) || tier.Buildings.Count == 0)
            {
                continue;
            }

            sections++;
            if (!tier.Built)
            {
                _ = report.Expect("perimeter built", new AcceptanceException($"perimeter section {tier.Name.Value} not built"));
            }

            foreach (var b in tier.Buildings)
            {
                if (b.Definition == "Door")
                {
                    doors++;
                }
                else
                {
                    walls++;
                }
            }
        }

        report["perimeter"] = new JsonObject { ["sections"] = sections, ["walls"] = walls, ["gate_doors"] = doors };
        if (sections == 0 || doors < 3)
        {
            _ = report.Expect("perimeter gates", new AcceptanceException($"layout has no perimeter wall with gates: {sections} sections, {doors} gate doors"));
        }

        // Scenario 2/3: a real edge raid.
        // The colonists may be parked inside the corridor they had been building. A hold plan drafts and moves them one
        // action per window, so a raider reaching the corridor first bounced authority on a colonist-health stop and settled
        // the plan before dispatch. The line is held from the firing positions, so stand the defenders there before the
        // raid; the plan's own drafts and moves are still what the run asserts.
        var cells = string.Join(';', layout.Firing.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.X},{f.Z}")));
        var mustered = await FixtureAsync("muster", new JsonObject { ["op"] = "muster", ["cells"] = cells }).ConfigureAwait(false);
        report["muster"] = mustered.DeepClone();
        // Hold-the-line needs the raid to come through the corridor the cover row faces: it walks in from the map edge
        // nearest the corridor entry rather than any edge the raid worker picks. A raider arriving behind the cover row
        // rightly gets no hold. The nearest edge to the entry can be a flank one cell closer than the edge the corridor
        // faces, so pin the edge the corridor faces: Toward runs from that edge toward Home.
        var raidArgs = new JsonObject
        {
            ["op"] = "raid",
            ["strategy"] = v.Strategy,
            ["arrival"] = v.Arrival,
            ["x"] = opening.Entry.X,
            ["z"] = opening.Entry.Z,
            ["side"] = EdgeSide(opening.Toward),
        };
        // The ring stops here: a resume replays the pre-raid audits above, and a world captured after the raid has sprung
        // traps the repair may not have replaced yet, so every resume starts from a pre-raid entry and stages the raid again.
        Checkpoints.Cap("raid staged; a resume replays the pre-raid audits, so no entry is taken after this point (#330)");
        var raid = await FixtureAsync("raid", raidArgs).ConfigureAwait(false);
        report["raid_incident"] = raid.DeepClone();

        long resolvedTick;
        await using (var svc = await DefenseService.LaunchAsync(s, "raid", identity, cancellationToken).ConfigureAwait(false))
        {
            var method = await InPhaseAsync("combat response", () => WaitCombatMethodAsync(svc.Store, svc.Budget(RaidTimeout), report, cancellationToken)).ConfigureAwait(false);
            if (!method.Method.Value.StartsWith("combat-", StringComparison.Ordinal))
            {
                throw new AcceptanceException($"combat method \"{method.Method.Value}\", expected prefix \"combat-\"");
            }

            var fight = svc.Store.LoadCombatFight(method.Plan, cancellationToken) ?? throw new AcceptanceException($"combat plan {method.Plan.Value} has no fight record");
            AssertHoldPlan(fight.Memory, layout);
            // The service holds the raid: with the hold plan admitted the scheduler runs bounded combat windows that
            // acknowledge the live hostiles, re-planning between them, until the ActiveCombat goal recovers. The fixture can
            // only inspect from the harness session, so the outcome is read natively once the service is gone.
            var dispatched = new JsonObject();
            report["hold_plan_dispatch"] = dispatched;
            await InPhaseAsync("hold dispatch", () => WaitHoldDispatchedAsync(svc.Store, method.Plan, svc.Budget(RaidTimeout / 2), dispatched, cancellationToken)).ConfigureAwait(false);
            var resolved = new JsonObject();
            report["raid_resolution"] = resolved;
            await InPhaseAsync("raid resolution", () => WaitRaidResolvedAsync(svc.Store, method.Plan, svc.Budget(RaidTimeout), resolved, cancellationToken)).ConfigureAwait(false);
            // Scenario 4: retreat and repair. The recovered goal no longer authorizes the hold plan, so its drafts are
            // released; the layout planner re-verifies the record after the combat epoch, re-opens every tier that lost a
            // building and rebuilds it.
            resolvedTick = (long)resolved["resolved_tick"].AsNumber();
            var released = new JsonObject();
            report["defenders_released"] = released;
            await InPhaseAsync("draft release after raid", () => WaitDefendersReleasedAsync(svc.Store, method.Plan, "combat-", svc.Budget(RepairTimeout), released, cancellationToken)).ConfigureAwait(false);
            // The wounded are staged healed before the repair phase: a CriticalMedical hold suspends every routine goal, the
            // controller's tend order has no native preview yet, and the fixture colony has no bed for the game's own
            // doctors to use. Fixture ops need the harness session, so the repair phase runs under a fresh service on the
            // same journal.
            await svc.StopAsync().ConfigureAwait(false);
            report["raid_authority"] = svc.Authority?.DeepClone();
        }

        h = await SessionReattach.PausedAsync(s, cancellationToken).ConfigureAwait(false);
        if (v.Raided is not null)
        {
            var raided = await FixtureAsync("inspect-raid-resolved", Op("inspect")).ConfigureAwait(false);
            report["inspect_raid_resolved"] = raided.DeepClone();
            v.Raided(after, raided);
        }

        var healed = await FixtureAsync("heal-after-raid", Op("heal")).ConfigureAwait(false);
        report["healed_after_raid"] = healed.DeepClone();
        // The game's auto-rebuild must not repair the corridor for the planner: its pending trap blueprints are removed and
        // one more trap vanishes (when any survived), so the traps missing now are the planner's to replace.
        var breach = await FixtureAsync("breach-after-raid", Op("breach")).ConfigureAwait(false);
        report["breach_after_raid"] = breach.DeepClone();
        var missingTraps = (int)after["traps"].AsNumber() - (int)breach["standing"].AsNumber();
        if (missingTraps < 1)
        {
            throw new AcceptanceException($"breach left no trap missing: {breach.ToJson()}");
        }

        await using (var svc = await DefenseService.LaunchAsync(s, "repair", identity, cancellationToken).ConfigureAwait(false))
        {
            var repaired = new JsonObject();
            report["layout_repair"] = repaired;
            await InPhaseAsync("layout repair after raid", () => WaitLayoutRepairedAsync(svc.Store, world, resolvedTick, RepairTicks, svc.Budget(RepairTimeout), repaired, cancellationToken)).ConfigureAwait(false);
            var rebuilt = (int)repaired["traps_rebuilt"].AsNumber();
            if (rebuilt < missingTraps)
            {
                throw new AcceptanceException($"planner rebuilt {rebuilt} traps in its repair methods, {missingTraps} were missing after the breach: {repaired.ToJson()}");
            }

            await svc.StopAsync().ConfigureAwait(false);
            report["repair_authority"] = svc.Authority?.DeepClone();
        }

        h = await SessionReattach.PausedAsync(s, cancellationToken).ConfigureAwait(false);
        var final = await FixtureAsync("inspect-after-raid", Op("inspect")).ConfigureAwait(false);
        report["inspect_after_raid"] = final.DeepClone();
        if (final["colonistsOnTraps"].AsSlice() is { Count: > 0 } stillOnTraps)
        {
            throw new AcceptanceException($"colonists standing on trap cells after raid: {new JsonArray([.. stillOnTraps.Select(n => n?.DeepClone())]).ToJson()}");
        }

        // A spike trap is trapDestroyOnSpring: springing destroys it, so a trap id from the layout audit that is gone now
        // was sprung, and an id that is new now is its replacement (the planner's re-admitted tier: the breach removed the
        // game's own auto-rearm blueprints); the fixture's armed-state count only covers rearmable traps.
        var (sprung, rebuiltIds) = TrapIdDelta(after, final);
        var sprungCount = sprung.Count + (int)final["sprung"].AsNumber();
        report["traps_sprung"] = sprungCount;
        report["traps_sprung_ids"] = StringsJson(sprung);
        report["traps_rebuilt_ids"] = StringsJson(rebuiltIds);
        if (sprungCount == 0)
        {
            throw new AcceptanceException($"no trap sprung during the edge raid: {final.ToJson()}");
        }

        // The repaired layout matches the audited one natively (every sprung trap replaced, no wall missing), and nobody is
        // left drafted once the raid is over.
        if (rebuiltIds.Count < sprung.Count)
        {
            throw new AcceptanceException($"{sprung.Count} traps sprung ([{string.Join(' ', sprung)}]) but {rebuiltIds.Count} rebuilt ([{string.Join(' ', rebuiltIds)}])");
        }

        var finalTraps = (int)final["traps"].AsNumber();
        var auditedTraps = (int)after["traps"].AsNumber();
        if (finalTraps < auditedTraps)
        {
            throw new AcceptanceException($"native trap count {finalTraps} after repair, {auditedTraps} after the layout audit");
        }

        if (breach["breached"].AsMap() is { } breached && !HasCell(final["trapCells"].AsSlice(), (int)breached["x"].AsNumber(), (int)breached["z"].AsNumber()))
        {
            throw new AcceptanceException($"breached trap cell {breached.ToJson()} holds no trap after repair: {final["trapCells"].ToJson()}");
        }

        var finalWalls = final["walls"].AsSlice().Count;
        var auditedWalls = after["walls"].AsSlice().Count;
        if (finalWalls < auditedWalls)
        {
            throw new AcceptanceException($"native wall count {finalWalls} after repair, {auditedWalls} after the layout audit");
        }

        foreach (var raw in final["colonists"].AsSlice())
        {
            var row = raw.Obj();
            if (row["drafted"].AsBool() == true)
            {
                throw new AcceptanceException($"colonist {row["id"].AsString()} still drafted after the raid resolved");
            }
        }

        var neutralised = 0;
        foreach (var raw in final["hostiles"].AsSlice())
        {
            var row = raw.Obj();
            if (row["dead"].AsBool() == true || row["downed"].AsBool() == true)
            {
                neutralised++;
            }
        }

        var raidersLeft = final["hostiles"].AsSlice().Count;
        report["raid_outcome"] = new JsonObject { ["hostiles_on_map"] = raidersLeft, ["dead_or_downed"] = neutralised };
        if (neutralised == 0 && raidersLeft == raid["added"].AsSlice().Count)
        {
            throw new AcceptanceException($"raid did not resolve: no raider dead, downed or gone: {final.ToJson()}");
        }
    }

    /// <summary>The world the session's identity names.</summary>
    internal static World WorldOf(JsonObject identity) =>
        new(new ColonyId(identity["colonyId"].AsString()), new LoadId(identity["loadToken"].AsString()), new MapId((int)identity["mapId"].AsNumber()));

    /// <summary>Runs one phase of the campaign and names the phase in the error it fails with (Go <c>fmt.Errorf("phase: %w")</c>).</summary>
    internal static async Task<T> InPhaseAsync<T>(string phase, Func<Task<T>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{phase}: {e.Message}", e);
        }
    }

    /// <summary>
    /// Stages the colony's one guarded construction site, whose building plan holds the routine arbitration slot, and
    /// stocks the perimeter's stone blocks beside it so the wall's sections are admitted as fast as they are built. It
    /// returns the site's cell.
    /// </summary>
    internal static async Task<(int X, int Z)> PrepareSiteAsync(Harness h, JsonObject identity, Report report, CancellationToken cancellationToken)
    {
        var construction = await h.CallAsync("prepare-construction", "test/guarded_construction_prepare", new JsonObject { ["siteCount"] = 1 }, cancellationToken).ConfigureAwait(false);
        if (construction["success"].AsBool() != true || !ServiceProcess.MatchesIdentity(construction, identity))
        {
            throw new AcceptanceException($"guarded_construction_prepare refused or identity mismatch: {construction.ToJson()}");
        }

        var sites = construction["sites"].AsSlice();
        if (sites.Count != 1)
        {
            throw new AcceptanceException($"guarded_construction_prepare: expected exactly one site, got {construction.ToJson()}");
        }

        var site = sites[0].Obj();
        var x = (int)site["x"].AsNumber();
        var z = (int)site["z"].AsNumber();
        foreach (var stock in SiteStock(x, z))
        {
            _ = await DebugGame.LabStockAsync(h, stock, cancellationToken).ConfigureAwait(false);
        }

        report["stone_blocks"] = PerimeterStone;
        report["wood_logs"] = WoodLogs;
        return (x, z);
    }

    /// <summary>The resources <see cref="PrepareSiteAsync"/> lays beside the site: the perimeter's granite blocks and the layout's wood.</summary>
    internal static Stock[] SiteStock(int x, int z) =>
    [
        new Stock { Def = "BlocksGranite", Total = PerimeterStone, X = x, Z = z },
        new Stock { Def = "WoodLog", Total = WoodLogs, X = x, Z = z },
    ];

    /// <summary>The map side a corridor facing <paramref name="toward"/> (the direction from the edge toward Home) opens onto, as the fixture's raid side.</summary>
    internal static string EdgeSide(Rotation toward) => toward switch
    {
        Rotation.North => "south",
        Rotation.South => "north",
        Rotation.East => "west",
        Rotation.West => "east",
        _ => "",
    };

    /// <summary>Reads the killbox opening of the layout plan the journal holds.</summary>
    private static async Task<DefenseKillbox> PlanOpeningAsync(ISession s, World world, CancellationToken cancellationToken)
    {
        using var stored = ControllerJournal.OpenStore(s, cancellationToken);
        var snapshot = new GenerationSnapshot(world.Colony, world.Map, world.Load, new PlanId("audit"), new PlanRevision(1), default);
        var record = await stored.LayoutPlanAsync(snapshot, new Tick(int.MaxValue), cancellationToken).ConfigureAwait(false);
        if (record?.Plan is not { } plan)
        {
            throw new AcceptanceException("layout plan for the opening: found False");
        }

        var (killbox, ok) = PolicyFunctions.KillboxOpening(plan);
        return ok ? killbox : throw new AcceptanceException("layout plan holds no killbox opening");
    }

    /// <summary>
    /// Polls the journal until the stored layout for <paramref name="world"/> is Complete, recording each tier method's
    /// plan and its final stage. The progress signature is the tier methods' plan stages, the stored record and an open
    /// tier's construction (<see cref="DefenseBuildProgress"/>).
    /// </summary>
    internal static async Task<DefenseLayoutRecord> WaitLayoutCompleteAsync(JournalStore s, World world, Wait w, Report report, CancellationToken cancellationToken)
    {
        StandardId projectId = default;
        var record = new DefenseLayoutRecord();
        var stored = false;
        var building = new DefenseBuildProgress();
        var tiers = new JsonObject();
        try
        {
            await w.WaitProgressAsync(async ct =>
            {
                var review = await s.LoadRoundsAsync(ct).ConfigureAwait(false);
                if (projectId.Value.Length == 0 && review.StandardFor(PolicyFunctions.EnsureDefensiveLayout, out var id))
                {
                    projectId = id;
                    report["layout_project"] = projectId.Value;
                }

                var open = new List<string>();
                if (projectId.Value.Length != 0)
                {
                    var project = await TryLoadStandardAsync(s, projectId, ct).ConfigureAwait(false);
                    if (project is not null)
                    {
                        foreach (var m in project.Methods)
                        {
                            var entry = new JsonObject { ["plan"] = m.Plan.Value };
                            var plan = await TryLoadPlanAsync(s, m.Plan, ct).ConfigureAwait(false);
                            if (plan is not null)
                            {
                                entry["stages"] = StringsJson(Stages(plan));
                                if (StorePackage.PlanOpen(plan))
                                {
                                    open.Add(m.Plan.Value);
                                }
                            }

                            tiers[m.MethodId.Value] = entry;
                        }

                        report["layout_tier_methods"] = tiers.DeepClone();
                    }
                }

                var r = await s.LoadDefenseLayoutAsync(world, ct).ConfigureAwait(false);
                if (r is not null)
                {
                    record = r;
                    stored = true;
                    report["layout_record"] = LayoutJson(record);
                    if (record.Complete)
                    {
                        return record.TrapLane.Count == 0 || record.Firing.Count == 0
                            ? throw new AcceptanceException($"complete layout without trap lane or firing cells: {LayoutJson(record).ToJson()}")
                            : ("", true);
                    }
                }

                open.Sort(StringComparer.Ordinal);
                return (Wait.Signature(projectId.Value, tiers.ToJson(), stored, record.Complete, building.Signature(open, record, review.Tick.Value)), false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"layout not complete (project=\"{projectId.Value}\" stored={stored}): {e.Message}", e);
        }

        return record;
    }

    /// <summary>The ActiveCombat incident the review binds and its stored state; both null while it binds none.</summary>
    private static async Task<(IncidentState? State, RoundsIncident? Binding)> CombatIncidentAsync(JournalStore s, StoreRounds review, CancellationToken cancellationToken)
    {
        if (review.Incident(PolicyFunctions.ActiveCombat) is not { } binding)
        {
            return (null, null);
        }

        var state = await s.LoadIncidentAsync(binding.Incident, cancellationToken).ConfigureAwait(false);
        return (state, binding);
    }

    /// <summary>Polls the journal for the ActiveCombat incident's first committed method after the raid.</summary>
    private static async Task<IncidentMethod> WaitCombatMethodAsync(JournalStore s, Wait w, Report report, CancellationToken cancellationToken)
    {
        IncidentMethod? found = null;
        try
        {
            await w.WaitProgressAsync(async ct =>
            {
                var review = await s.LoadRoundsAsync(ct).ConfigureAwait(false);
                var (incident, binding) = await CombatIncidentAsync(s, review, ct).ConfigureAwait(false);
                if (incident is null || binding is null)
                {
                    return (Wait.Signature("unbound", review.Incidents.Count), false);
                }

                if (incident.Methods.Count > 0)
                {
                    report["combat_incident"] = binding.Incident.Value;
                    report["combat_method"] = incident.Methods[0].Method.Value;
                    found = incident.Methods[0];
                    return ("", true);
                }

                return (Wait.Signature("bound", binding.Incident.Value), false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"no ActiveCombat method: {e.Message}", e);
        }

        return found!;
    }

    /// <summary>
    /// Waits until every draft in the hold plan is completed and every move to a firing cell has been attempted natively:
    /// the furthest the plan can get before the first combat window lets ticks pass. A raid the first defender (or the
    /// turret tier) ends before the others reach their cells retires the plan with the rest cancelled; that settlement
    /// counts as dispatched when a defender reached its cell or fired and no stage failed.
    /// </summary>
    private static async Task<bool> WaitHoldDispatchedAsync(JournalStore s, PlanId id, Wait w, JsonObject output, CancellationToken cancellationToken)
    {
        try
        {
            await w.WaitProgressAsync(ct =>
            {
                var evidence = s.CombatEvidence(id, ct);
                int moves = 0, attacks = 0;
                foreach (var stop in evidence)
                {
                    foreach (var o in stop.Orders)
                    {
                        if (!o.Applied)
                        {
                            continue;
                        }

                        switch (o.Order.Kind)
                        {
                            case CombatOrderKind.Move:
                                moves++;
                                break;
                            case CombatOrderKind.Attack:
                                attacks++;
                                break;
                        }
                    }
                }

                output["stops"] = evidence.Count;
                output["moves"] = moves;
                output["attacks"] = attacks;
                return Task.FromResult(moves > 0 ? ("", true) : (evidence.Count.ToString(CultureInfo.InvariantCulture), false));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"fight issued no applied move: {e.Message}", e);
        }

        return true;
    }

    /// <summary>
    /// Polls the journal while the service fights the raid: it counts the combat watch windows the scheduler applied and
    /// returns once the ActiveCombat goal has recovered (no live hostile) or is no longer bound. At least one combat window
    /// must have run, and at least one defender must have been observed arriving on its firing cell (a completed movement
    /// in the first hold plan or any hold plan the goal re-planned after it). The progress signature is the window counts,
    /// the goal's binding and the defenders on the line.
    /// </summary>
    private static async Task<bool> WaitRaidResolvedAsync(JournalStore s, PlanId first, Wait w, JsonObject output, CancellationToken cancellationToken)
    {
        var holdPlans = new HashSet<PlanId> { first };
        var onLine = new HashSet<RimGovernor.Brain.Domain.PawnId>();
        try
        {
            await w.WaitProgressAsync(async ct =>
            {
                var attempts = await s.LoadClockAttemptsAsync(4096, ct).ConfigureAwait(false);
                int combatWindows = 0, colonyWindows = 0;
                var lastPhase = "";
                var acknowledged = new List<string>();
                foreach (var a in attempts)
                {
                    var start = a.Intent.Command.Start;
                    if (start is null || a.Phase != ClockPhase.Applied)
                    {
                        continue;
                    }

                    lastPhase = "applied";
                    if (start.Policy?.Mode == WatchMode.Combat)
                    {
                        combatWindows++;
                        acknowledged = [.. start.Policy.AcknowledgedHostileIds];
                    }
                    else
                    {
                        colonyWindows++;
                    }
                }

                output["combat_windows"] = combatWindows;
                output["colony_windows"] = colonyWindows;
                output["last_acknowledged_hostiles"] = StringsJson(acknowledged);
                output["last_applied_phase"] = lastPhase;
                var review = await s.LoadRoundsAsync(ct).ConfigureAwait(false);
                var (incident, binding) = await CombatIncidentAsync(s, review, ct).ConfigureAwait(false);
                var bound = incident is not null && binding is not null;
                var need = binding is null ? "" : SituationName(binding.Situation);
                if (incident is not null)
                {
                    foreach (var m in incident.Methods)
                    {
                        if (m.Method.Value.StartsWith("combat-", StringComparison.Ordinal))
                        {
                            _ = holdPlans.Add(m.Plan);
                        }
                    }
                }

                foreach (var id in holdPlans)
                {
                    foreach (var stop in s.CombatEvidence(id, ct))
                    {
                        foreach (var o in stop.Orders)
                        {
                            if (o.Order.Kind == CombatOrderKind.Move && o.Applied)
                            {
                                _ = onLine.Add(o.Order.Pawn);
                            }
                        }
                    }
                }

                var line = onLine.Select(p => p.Value).Order(StringComparer.Ordinal).ToList();
                output["defenders_on_firing_cells"] = StringsJson(line);
                output["combat_standard_bound"] = bound;
                output["combat_standard_need"] = need;
                output["resolved_tick"] = review.Tick.Value;
                if (combatWindows > 0 && (!bound || need == "met"))
                {
                    return line.Count == 0
                        ? throw new AcceptanceException($"no defender completed its move to a firing cell during the raid: {output.ToJson()}")
                        : ("", true);
                }

                return (Wait.Signature(combatWindows, colonyWindows, bound, need, holdPlans.Count, string.Join(',', line)), false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"raid not resolved under the service ({output.ToJson()}): {e.Message}", e);
        }

        return true;
    }

    /// <summary>Compares the trap ids of two fixture inspects: ids only in <paramref name="before"/> were destroyed (sprung), ids only in <paramref name="after"/> are replacements.</summary>
    internal static (List<string> Sprung, List<string> Rebuilt) TrapIdDelta(JsonObject before, JsonObject after)
    {
        static HashSet<string> Ids(JsonObject m) => new(m["trapIds"].AsSlice().Select(raw => raw.AsString()), StringComparer.Ordinal);
        var was = Ids(before);
        var now = Ids(after);
        var sprung = was.Where(id => !now.Contains(id)).Order(StringComparer.Ordinal).ToList();
        var rebuilt = now.Where(id => !was.Contains(id)).Order(StringComparer.Ordinal).ToList();
        return (sprung, rebuilt);
    }

    /// <summary>
    /// Waits until every fight rostering a defender is closed and its defenders undrafted: the recovered ActiveCombat goal
    /// closes the fight, so the undraft sweep returns each defender to colony work.
    /// </summary>
    private static async Task<bool> WaitDefendersReleasedAsync(JournalStore s, PlanId first, string prefix, Wait w, JsonObject output, CancellationToken cancellationToken)
    {
        try
        {
            await w.WaitProgressAsync(async ct =>
            {
                var plans = new HashSet<PlanId> { first };
                var review = await s.LoadRoundsAsync(ct).ConfigureAwait(false);
                // The recovered incident closes once its fights settle, so read the latest one, closed or not.
                var world = new World(review.Snapshot.Colony, review.Snapshot.Load, review.Snapshot.Map);
                var incident = await s.LatestIncidentAsync(world, PolicyFunctions.ActiveCombat, ct).ConfigureAwait(false);
                if (incident is not null)
                {
                    foreach (var m in incident.Methods)
                    {
                        if (m.Method.Value.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            _ = plans.Add(m.Plan);
                        }
                    }
                }

                var drafts = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var id in plans)
                {
                    var fight = s.LoadCombatFight(id, ct);
                    if (fight is null)
                    {
                        continue;
                    }

                    foreach (var pawn in fight.Roster)
                    {
                        drafts[id.Value + "/" + pawn.Value] = "roster";
                    }
                }

                var draftsJson = new JsonObject();
                foreach (var (key, value) in drafts)
                {
                    draftsJson[key] = value;
                }

                output["drafts"] = draftsJson;
                output["hold_plans"] = plans.Count;
                return drafts.Count == 0 ? ("", true) : (Wait.Signature(draftsJson.ToJson()), false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"defenders not released ({output.ToJson()}): {e.Message}", e);
        }

        return true;
    }

    /// <summary>
    /// Waits until the stored layout has been re-verified after the raid's combat epoch with every tier standing: the
    /// planner re-opens each tier that lost a building and admits a fresh tier method ("defense-&lt;tier&gt;-&lt;attempt&gt;")
    /// to rebuild it; traps_rebuilt counts the spike traps those methods placed. At least one tier must have been observed
    /// degraded (the edge raid springs at least one trap), and the verification must land within
    /// <paramref name="repairTicks"/> of the tick the goal recovered.
    /// </summary>
    private static async Task<bool> WaitLayoutRepairedAsync(JournalStore s, World world, long resolvedTick, long repairTicks, Wait w, JsonObject output, CancellationToken cancellationToken)
    {
        output["resolved_tick"] = resolvedTick;
        var methods = new JsonObject();
        var reopened = new HashSet<string>(StringComparer.Ordinal);
        var plans = new SortedDictionary<string, PlanId>(StringComparer.Ordinal);
        var trapsRebuilt = 0;
        try
        {
            await w.WaitProgressAsync(async ct =>
            {
                var review = await s.LoadRoundsAsync(ct).ConfigureAwait(false);
                var combat = "";
                if (await s.LatestIncidentAsync(world, PolicyFunctions.ActiveCombat, ct).ConfigureAwait(false) is { } latest)
                {
                    combat = latest.Incident.Id.Value;
                }

                if (review.StandardFor(PolicyFunctions.EnsureDefensiveLayout, out var id))
                {
                    var project = await TryLoadStandardAsync(s, id, ct).ConfigureAwait(false);
                    foreach (var m in project?.Methods ?? [])
                    {
                        if (methods.ContainsKey(m.MethodId.Value))
                        {
                            continue;
                        }

                        var plan = await s.LoadPlanAsync(m.Plan, ct).ConfigureAwait(false);
                        var traps = plan.Spec.Actions.Count(a => a.Building is { } b && b.Definition == "TrapSpike");
                        trapsRebuilt += traps;
                        plans[m.MethodId.Value] = m.Plan;
                        methods[m.MethodId.Value] = new JsonObject { ["plan"] = m.Plan.Value, ["actions"] = plan.Spec.Actions.Count, ["traps"] = traps };
                    }
                }

                var record = await s.LoadDefenseLayoutAsync(world, ct).ConfigureAwait(false) ?? throw new AcceptanceException("stored layout gone after the raid");
                var standing = true;
                foreach (var tier in record.Tiers)
                {
                    if (tier.Buildings.Count > 0 && !tier.Built)
                    {
                        standing = false;
                        _ = reopened.Add(tier.Name.Value);
                    }
                }

                var names = reopened.Order(StringComparer.Ordinal).ToList();
                output["tiers_reopened"] = StringsJson(names);
                output["repair_methods"] = methods.DeepClone();
                output["traps_rebuilt"] = trapsRebuilt;
                output["verified_tick"] = record.VerifiedTick.Value;
                output["verified_combat"] = record.VerifiedCombat;
                output["complete"] = record.Complete;
                // A checkpoint run re-observes the record under its new load (Complete is cleared on reload), so Complete is
                // required only once the post-raid census has found every tier standing.
                var verified = standing && record.Complete && record.VerifiedCombat == combat && record.VerifiedTick.Value >= resolvedTick;
                if (verified && reopened.Count == 0)
                {
                    throw new AcceptanceException($"layout verified standing after the raid without any tier observed degraded: {output.ToJson()}");
                }

                if (verified)
                {
                    var elapsed = record.VerifiedTick.Value - resolvedTick;
                    return elapsed > repairTicks
                        ? throw new AcceptanceException($"layout repaired {elapsed} ticks after the raid resolved, budget {repairTicks}")
                        : ("", true);
                }

                // The rebuild is native work: while a repair plan's frames are under construction nothing in the record
                // moves, so the review tick and each plan's stages carry the progress; repairTicks bounds the wait in game
                // time.
                var progress = new List<string>();
                foreach (var (name, planId) in plans)
                {
                    var state = await s.LoadPlanAsync(planId, ct).ConfigureAwait(false);
                    progress.Add(name + ":" + string.Join(',', Stages(state)));
                }

                return (Wait.Signature(standing, record.VerifiedCombat, record.VerifiedTick.Value, methods.Count, string.Join(',', names), review.Tick.Value, string.Join(';', progress)), false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"layout not repaired ({output.ToJson()}): {e.Message}", e);
        }

        return true;
    }

    /// <summary>The goal's stored state; null while the journal does not hold it (Go ignored the not-found error).</summary>
    private static async Task<StandardState?> TryLoadStandardAsync(JournalStore s, StandardId id, CancellationToken cancellationToken)
    {
        try
        {
            return await s.LoadStandardAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    /// <summary>The plan's stored state; null while the journal does not hold it.</summary>
    private static async Task<PlanState?> TryLoadPlanAsync(JournalStore s, PlanId id, CancellationToken cancellationToken)
    {
        try
        {
            return await s.LoadPlanAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    /// <summary>Reports whether the fixture's {x, z} list contains the cell.</summary>
    private static bool HasCell(IReadOnlyList<JsonNode?> cells, int x, int z) =>
        cells.Any(raw => raw.Obj() is var row && (int)row["x"].AsNumber() == x && (int)row["z"].AsNumber() == z);

    private static List<string> Stages(PlanState plan) => [.. plan.Progress.Select(p => p.View.Stage.ToString().ToLowerInvariant())];

    /// <summary>The situation as Go printed it (unmet, met, unclear).</summary>
    private static string SituationName(Situation situation) => situation switch
    {
        Situation.Active => "unmet",
        Situation.Clear => "met",
        _ => "unclear",
    };

    /// <summary>Checks the fight formed hold-the-line: every positioned role stands on one of the layout's firing cells and never a trap cell.</summary>
    private static void AssertHoldPlan(CombatMemory memory, DefenseLayoutRecord layout)
    {
        if (layout.Firing.Count == 0)
        {
            throw new AcceptanceException("layout has no firing cells");
        }

        if (memory.Tactic != CombatTactic.Hold)
        {
            throw new AcceptanceException($"fight formed \"{memory.Tactic}\", want \"{CombatTactic.Hold}\" (hold refused: \"{memory.Refusal}\")");
        }

        var firing = new HashSet<Cell>(layout.Firing);
        var traps = new HashSet<Cell>();
        foreach (var tier in layout.Tiers)
        {
            foreach (var b in tier.Buildings)
            {
                if (b.Definition == "TrapSpike")
                {
                    _ = traps.Add(b.Cell);
                }
            }
        }

        var positioned = 0;
        foreach (var role in memory.Roles)
        {
            if (role.Cell is not { } cell)
            {
                continue;
            }

            positioned++;
            if (traps.Contains(cell))
            {
                throw new AcceptanceException($"hold places {role.Pawn.Value} on trap cell {CellText(cell)}");
            }

            if (!firing.Contains(cell))
            {
                throw new AcceptanceException($"hold places {role.Pawn.Value} on {CellText(cell)}, not a firing cell [{string.Join(' ', layout.Firing.Select(CellText))}]");
            }
        }

        if (positioned == 0)
        {
            throw new AcceptanceException("hold places no defender");
        }
    }

    private static string CellText(Cell c) => string.Create(CultureInfo.InvariantCulture, $"{{X:{c.X} Z:{c.Z}}}");

    /// <summary>The cells as the {x, z} list the spatial reads take.</summary>
    internal static JsonArray CellsJson(IEnumerable<Cell> cells) => [.. cells.Select(c => (JsonNode?)new JsonObject { ["x"] = c.X, ["z"] = c.Z })];

    private static JsonArray StringsJson(IEnumerable<string> values) => [.. values.Select(v => (JsonNode?)v)];

    /// <summary>
    /// Mirrors bridge.SpatialAccess.Accepted on the reply: no pawn lost a cell and every target is reachable natively and
    /// projected. Null accepts it.
    /// </summary>
    private static AcceptanceException? AssertAccess(JsonObject reply, HashSet<Cell> fenced)
    {
        JsonObject observed;
        try
        {
            observed = Harness.Outcome(reply, "observed").Value;
        }
        catch (AcceptanceException e)
        {
            return e;
        }

        var pawns = observed["pawns"].AsSlice();
        if (pawns.Count == 0)
        {
            return new AcceptanceException("no colonist audited");
        }

        foreach (var raw in pawns)
        {
            var p = raw.Obj();
            if (p["losesAccess"].AsBool() == true)
            {
                return new AcceptanceException($"colonist {p["pawn"].ToJson()} loses cells with the layout blocked: {p.ToJson()}");
            }

            foreach (var t in p["targets"].AsSlice())
            {
                var target = t.Obj();
                var native = target["nativeReachable"].AsBool() == true;
                var projected = target["projectedReachable"].AsBool() == true;
                if (target["cell"].AsMap() is { } cell && fenced.Contains(new Cell((int)cell["x"].AsNumber(), (int)cell["z"].AsNumber())))
                {
                    projected = true;
                }

                if (!native || !projected)
                {
                    return new AcceptanceException($"colonist {p["pawn"].ToJson()} cannot reach {target["cell"].ToJson()} with the layout blocked");
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Requires every firing cell to see at least one trap-lane cell and a positive cover block chance on at least one
    /// line. The lane is a snake whose walls hide the mouth from the firing line, so seeing the chokepoint is not asked.
    /// Null accepts it.
    /// </summary>
    private static AcceptanceException? AssertCover(IReadOnlyList<JsonNode?> lines, DefenseLayoutRecord layout)
    {
        if (lines.Count == 0)
        {
            return new AcceptanceException("no lines of fire observed");
        }

        var sees = new HashSet<Cell>();
        var covered = 0;
        foreach (var raw in lines)
        {
            var line = raw.Obj();
            if (line["lineOfSight"].AsBool() != true)
            {
                continue;
            }

            var from = line["from"].Obj();
            _ = sees.Add(new Cell((int)from["x"].AsNumber(), (int)from["z"].AsNumber()));
            if (line["shooterCover"].AsNumber() > 0)
            {
                covered++;
            }
        }

        foreach (var c in layout.Firing)
        {
            if (!sees.Contains(c))
            {
                return new AcceptanceException($"firing cell {CellText(c)} has no line of sight to any trap-lane cell: {new JsonArray([.. lines.Select(l => l?.DeepClone())]).ToJson()}");
            }
        }

        return covered == 0
            ? new AcceptanceException($"no firing cell has shooter cover toward the trap lane: {new JsonArray([.. lines.Select(l => l?.DeepClone())]).ToJson()}")
            : null;
    }

    /// <summary>The layout record as the report shows it: the geometry counts and each tier's state (the full record is in the journal).</summary>
    internal static JsonObject LayoutJson(DefenseLayoutRecord r) => new()
    {
        ["complete"] = r.Complete,
        ["killbox"] = r.Killbox,
        ["trap_lane"] = CellsJson(r.TrapLane),
        ["firing"] = CellsJson(r.Firing),
        ["retreat"] = CellsJson(r.Retreat),
        ["entrances"] = CellsJson(r.Entrances),
        ["verified_tick"] = r.VerifiedTick.Value,
        ["verified_combat"] = r.VerifiedCombat,
        ["tiers"] = new JsonArray([.. r.Tiers.Select(t => (JsonNode?)TierJson(t))]),
    };

    /// <summary>One tier of the layout as the report shows it.</summary>
    internal static JsonObject TierJson(DefenseTierRecord t) => new()
    {
        ["name"] = t.Name.Value,
        ["built"] = t.Built,
        ["attempts"] = t.Attempts,
        ["reopened"] = t.Reopened,
        ["buildings"] = new JsonArray([.. t.Buildings.Select(b => (JsonNode?)new JsonObject { ["definition"] = b.Definition, ["x"] = b.Cell.X, ["z"] = b.Cell.Z })]),
    };
}

/// <summary>
/// The layout's construction as wait progress (Go <c>buildProgress</c>). A plan's stages all read completed once its
/// blueprints are placed, and the colonists then build them over game hours with nothing in the journal changing. A built
/// tier's plan is retired before the next tier is admitted. The advancing tick is that progress for three game days after
/// the open plans or the record's built and attempted tiers last changed; a stopped clock or a layout that never moves
/// again still stalls.
/// </summary>
internal sealed class DefenseBuildProgress
{
    private const long LayoutBuildTicks = 3 * 60000;

    private string _key = "";
    private long _since;

    internal string Signature(IReadOnlyList<string> open, DefenseLayoutRecord record, long tick)
    {
        int built = 0, attempts = 0;
        foreach (var t in record.Tiers)
        {
            if (t.Built)
            {
                built++;
            }

            attempts += t.Attempts;
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"{string.Join(',', open)}|built={built}|attempts={attempts}");
        if (!string.Equals(key, _key, StringComparison.Ordinal))
        {
            _key = key;
            _since = tick;
        }

        return tick - _since > LayoutBuildTicks ? "" : tick.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// One launch of the in-process brain's player session against the shared game, with the harness's player authority
/// acquired and kept alive and the journal open (Go <c>service</c> and <c>launchService</c>).
/// </summary>
internal sealed class DefenseService : IAsyncDisposable
{
    /// <summary>Resume attempts a launch makes before giving up (Go: five).</summary>
    private const int ResumeAttempts = 5;

    private readonly string _name;
    private readonly JsonObject _identity;
    private bool _stopped;

    private DefenseService(ServiceProcess process, ControllerJournal journal, string name, JsonObject identity)
    {
        Process = process;
        ControllerJournal = journal;
        _name = name;
        _identity = identity;
    }

    internal ServiceProcess Process { get; }

    internal ControllerJournal ControllerJournal { get; }

    internal JournalStore Store => ControllerJournal.Store;

    /// <summary>The authority keep-alive counters, set by <see cref="StopAsync"/>.</summary>
    internal JsonObject? Authority { get; private set; }

    /// <summary>
    /// Starts the service session for one phase (every launch shares one journal; the declared spec carries the families,
    /// tend and rescue riding along for the aftermath: raid injuries hold CriticalMedical in deficit, which suspends every
    /// priority>=2 goal including the layout repair until the wounded are treated; each phase gets its own requestId
    /// prefix), acquires authority, keeps it alive and opens the journal.
    /// </summary>
    internal static async Task<DefenseService> LaunchAsync(ISession s, string name, JsonObject identity, CancellationToken cancellationToken)
    {
        var spec = s.Spec with { Prefix = "defense-" + name };
        var process = await s.ServeAsync(spec, cancellationToken).ConfigureAwait(false);
        try
        {
            // A service launched into a world whose journal still carries the previous phase's clock events (the fixture's
            // own pause between phases is an external stop) may review those events while the resume is in flight, disable
            // authority and leave the record uncertain: the resume is repeated, bounded.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    _ = await process.AcquireAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (AcceptanceException) when (attempt < ResumeAttempts)
                {
                    await Task.Delay(AuthorityKeepAlive.KeepAliveInterval, cancellationToken).ConfigureAwait(false);
                }
            }

            process.KeepAuthority(cancellationToken);
            ControllerJournal journal;
            try
            {
                journal = ControllerJournal.Open(s, process, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new AcceptanceException($"open verification store: {e.Message}", e);
            }

            return new DefenseService(process, journal, name, identity);
        }
        catch
        {
            _ = await process.StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A journal poll bounded by <paramref name="budget"/>, the shared stall budget and the service's own exit.</summary>
    internal Wait Budget(TimeSpan budget) => new() { Ceiling = budget, Stall = Wait.StallBudget(), Terminal = Process.Exited };

    /// <summary>
    /// Stops the keep-alive, hands native authority back to manual and closes the journal. A stopped service leaves the
    /// native side in auto mode under its session, and the next service's acquire would resolve uncertain against it, so
    /// the pause comes first. Safe to call twice.
    /// </summary>
    internal async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        Process.HoldAuthority(true);
        try
        {
            _ = await Process.ApiAsync("POST", "/api/player/control/pause", new JsonObject
            {
                ["requestId"] = string.Create(CultureInfo.InvariantCulture, $"defense-{_name}-pause-{DateTimeOffset.UtcNow.Ticks}"),
                ["expected"] = _identity.DeepClone(),
            }, "", CancellationToken.None).ConfigureAwait(false);
        }
        catch (AcceptanceException tolerated)
        {
            // Best effort, as in Go: a refused pause leaves the next acquire to resolve it.
            Tolerated.Note("pause on stop", tolerated);
        }

        ControllerJournal.Dispose();
        Authority = await Process.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
