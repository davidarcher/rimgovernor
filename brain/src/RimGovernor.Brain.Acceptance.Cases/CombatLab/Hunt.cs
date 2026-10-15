using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/hunt-squad</c> and <c>combatlab/hunt-door</c> (Go <c>hunt.go</c>), the squad hunt cases. Both start on
/// the blank lab with four riflemen (lab-ranged's line) and no raiders, a group of calm wild animals north of them, and
/// the planners a hunt and its aftermath compose: the hunt origin of ActiveCombat (defense), the butcher and haul flow
/// that takes the corpses (bill, haul, work, stockpiles).
/// </summary>
internal static class CombatLabHunt
{
    // huntTicks bounds the served window of the kill-and-butcher case: the squad walks into range, shoots three animals,
    // and the corpses are hauled to the butcher spot and butchered. Fast speed keeps it minute-scale.
    private const int Ticks = 12000;

    // huntDoorLeadStep and huntDoorLeadMax bound the lead: the hunt runs in steps until the squad is drafted, then the
    // manhunter is staged.
    private const int DoorLeadStep = 300;
    private const int DoorLeadMax = 6000;

    // huntDoorTicks bounds the door fight after the manhunter appears.
    private const int DoorTicks = 6000;
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(8);

    private const string ObserveChannelsTool = "test/food_channels_observe";

    // huntDoorRoom is the squad's room: a roofed 11x11 ring behind the sandbags whose one door faces the prey. It replaces
    // the sandbag line, which the room stands in for.
    private const int DoorHalf = 5;

    private static readonly IReadOnlyList<string> Families = ["defense", "tend", "rescue", "acquisition", "work", "bill", "stockpiles"];

    internal static AcceptanceCase SquadCase() => new()
    {
        Name = "combatlab/hunt-squad",
        Scope = "Squad hunt (#1621): four drafted riflemen on the blank lab and three calm wild animals of two species (two deer, an elk) 12 cells north, "
            + "a butcher spot behind the line, served by the routine planners. Within the tick budget every animal is dead, no colonist is downed, "
            + "and no corpse is left unbutchered on the map while butchered meat stands spawned. Native because no Go snapshot over recorded colony facts can "
            + "cover the end-to-end signal: the real draft, real shooting at fleeing animals, real hauling to the butcher spot and vanilla butchery.",
        Start = new LabStart { Colonists = 4 },
        RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool, CombatServe.PauseModeTool, ObserveChannelsTool],
        QuietWorld = true,

        // A checkpoint capture pauses the served game mid-hunt.
        NoCheckpoint = true,
        Serve = new ServeSpec { Families = Families, ClockSpeed = CombatServe.MetricsSpeed, Prefix = "combatlab-hunt" },
        Budget = Budget,
        Crew = new Crew { Size = 3 },
        Run = RunSquadAsync,
    };

    internal static AcceptanceCase DoorCase() => new()
    {
        Name = "combatlab/hunt-door",
        Scope = "Hunt falls back to the door loop (#1621, #1618): the squad hunts three calm wild animals from a roofed room with one door; once drafted and hunting, "
            + "a permanent-manhunter bear (the quiet storyteller zeroes manhunter-on-damage, so a prey cannot be provoked natively) is staged 25 cells out and the controller resumed. "
            + "Within the tick budget the bear is dead and no colonist is downed or dead. Native because no Go snapshot over recorded colony facts can cover the end-to-end signal: "
            + "the real draft kept across the restart, the real door closing and opening, shooting and vanilla bear physics.",
        Start = new LabStart { Colonists = 4 },
        RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool, CombatServe.PauseModeTool],
        QuietWorld = true,
        NoCheckpoint = true,
        Serve = new ServeSpec { Families = Families, ClockSpeed = CombatServe.MetricsSpeed, Prefix = "combatlab-hunt-door" },
        Budget = Budget,
        Crew = new Crew { Size = 3 },
        Run = RunDoorAsync,
    };

    // huntKit turns lab-ranged into a hunt: the raiders go, three wild animals of two species stand 12 cells north of the
    // centre (inside the squad's 12-cell group radius), and a butcher spot waits behind the line.
    private static void Kit(LabFixture f, int cx, int cz)
    {
        f.Name = "lab-hunt";
        f.Layout = null;
        f.Pawns = [.. f.Pawns.Where(p => p.Side != CombatFixtures.Hostile)];
        f.Pawns.Add(new LabPawn { Side = CombatFixtures.Wild, Kind = "Deer", X = cx - 3, Z = cz + 12 });
        f.Pawns.Add(new LabPawn { Side = CombatFixtures.Wild, Kind = "Deer", X = cx + 3, Z = cz + 12 });
        f.Pawns.Add(new LabPawn { Side = CombatFixtures.Wild, Kind = "Elk", X = cx, Z = cz + 14 });
        f.Things.Add(new FixtureThing { Def = "ButcherSpot", X = cx, Z = cz - 14 });
    }

    private static void DoorKit(LabFixture f, int cx, int cz)
    {
        Kit(f, cx, cz);
        f.Things = [];
        var rz = cz - 12;
        f.Things.AddRange(CombatFixtures.WalledRoom(cx, rz, DoorHalf, new LabCell(cx, rz + DoorHalf)));
        f.Things.Add(new FixtureThing { Def = "ButcherSpot", X = cx, Z = rz });
        f.Roof = new LabRoof("RoofConstructed", cx - DoorHalf, rz - DoorHalf, cx + DoorHalf, rz + DoorHalf);
        foreach (var p in f.Pawns)
        {
            if (p.Side == CombatFixtures.Colonist)
            {
                p.Z = rz;
            }
        }
    }

    private static async Task RunSquadAsync(ISession s, CancellationToken cancellationToken)
    {
        var staged = await CombatStage.StageAsync(s.Harness, "lab-ranged", cancellationToken, Kit).ConfigureAwait(false);
        var prey = staged.Ids(CombatFixtures.Wild);
        var colonists = staged.Colonists();
        if (prey.Count != 3 || colonists.Count != 4)
        {
            throw new AcceptanceException($"staged {CaseSupport.Tag(prey.Count)} prey and {CaseSupport.Tag(colonists.Count)} colonists, want 3 and 4");
        }

        await CombatServe.NeverPauseAsync(s.Harness, cancellationToken).ConfigureAwait(false);
        var h = await CombatServe.ServedWindowAsync(s, Ticks, false, cancellationToken).ConfigureAwait(false);
        var (pawns, tick) = await CombatStage.ReadAsync(h, cancellationToken).ConfigureAwait(false);
        s.Report["tick"] = tick;
        foreach (var id in prey)
        {
            if (pawns.TryGetValue(id, out var p) && !p.Dead)
            {
                throw new AcceptanceException($"prey {id} still alive at {CaseSupport.Tag(p.X)},{CaseSupport.Tag(p.Z)} after {CaseSupport.Tag(Ticks)} ticks");
            }
        }

        foreach (var id in colonists)
        {
            if (pawns.TryGetValue(id, out var p) && (p.Downed || p.Dead))
            {
                throw new AcceptanceException($"colonist {id} downed or dead in a hunt of calm animals: {Describe(p)}");
            }
        }

        var channels = await h.CallAsync("hunt-channels", ObserveChannelsTool, new JsonObject(), cancellationToken).ConfigureAwait(false);
        s.Report["channels"] = channels.DeepClone();
        var corpses = (int)channels["corpses"].AsNumber();
        if (corpses != 0)
        {
            throw new AcceptanceException($"{CaseSupport.Tag(corpses)} corpses left on the map after {CaseSupport.Tag(Ticks)} ticks: the kills were not hauled and butchered");
        }

        var meat = 0;
        foreach (var raw in channels["stock"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            if (row["spawned"].AsBool() == true && row["defName"].AsString().Contains("Meat", StringComparison.Ordinal))
            {
                meat += (int)row["units"].AsNumber();
            }
        }

        if (meat == 0)
        {
            throw new AcceptanceException($"no meat on the map: the corpses vanished without being butchered: {channels["stock"].ToJson()}");
        }

        s.Report["meat"] = meat;
    }

    private static async Task RunDoorAsync(ISession s, CancellationToken cancellationToken)
    {
        var staged = await CombatStage.StageAsync(s.Harness, "lab-ranged", cancellationToken, DoorKit).ConfigureAwait(false);
        var colonists = staged.Colonists();
        if (colonists.Count != 4)
        {
            throw new AcceptanceException($"staged {CaseSupport.Tag(colonists.Count)} colonists, want 4");
        }

        var cx = 0;
        var cz = 0;
        foreach (var p in staged.Fixture.Pawns)
        {
            if (p.Side == CombatFixtures.Wild && p.Kind == "Elk")
            {
                cx = p.X;
                cz = p.Z - 14;
            }
        }

        await CombatServe.NeverPauseAsync(s.Harness, cancellationToken).ConfigureAwait(false);
        Harness? h = null;
        var drafted = 0;
        for (var lead = 0; drafted < 3 && lead < DoorLeadMax; lead += DoorLeadStep)
        {
            h = await CombatServe.ServedWindowAsync(s, DoorLeadStep, lead > 0, cancellationToken).ConfigureAwait(false);
            var first = await CombatServe.RawReadAsync(h, new JsonObject { ["action"] = "read" }, cancellationToken).ConfigureAwait(false);
            drafted = 0;
            foreach (var raw in first["pawns"].AsSlice())
            {
                var row = raw.AsMap() ?? [];
                if (row["drafted"].AsBool() == true && row["side"].AsString() == CombatFixtures.Colonist)
                {
                    drafted++;
                }
            }
        }

        s.Report["draftedBeforeBear"] = drafted;
        if (drafted < 3 || h is null)
        {
            throw new AcceptanceException($"{CaseSupport.Tag(drafted)} colonists drafted after {CaseSupport.Tag(DoorLeadMax)} ticks, want the hunt's squad of 3 or 4");
        }

        // The bear charges from 25 cells beyond the prey; the squad is mid-hunt.
        var spec = new JsonObject
        {
            ["pawns"] = new JsonArray(new JsonObject { ["side"] = "manhunter", ["kind"] = "Bear_Grizzly", ["x"] = cx, ["z"] = cz + 25 }),
        }.ToJsonString();
        var reply = await h.CallAsync("combatlab-stage-bear", CombatFixtures.StageTool, new JsonObject { ["spec"] = spec }, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{CombatFixtures.StageTool} bear refused: {reply.ToJson()}");
        }

        var rows = reply["pawns"].AsSlice();
        if (rows.Count != 1)
        {
            throw new AcceptanceException($"staged {CaseSupport.Tag(rows.Count)} pawns for the bear, want 1");
        }

        var bear = (rows[0].AsMap() ?? [])["id"].AsString();
        h = await CombatServe.ServedWindowAsync(s, DoorTicks, true, cancellationToken).ConfigureAwait(false);
        var (pawns, tick) = await CombatStage.ReadAsync(h, cancellationToken).ConfigureAwait(false);
        s.Report["tick"] = tick;
        if (pawns.TryGetValue(bear, out var b) && !b.Dead)
        {
            throw new AcceptanceException($"manhunter bear {bear} alive at {CaseSupport.Tag(b.X)},{CaseSupport.Tag(b.Z)} after {CaseSupport.Tag(DoorTicks)} ticks");
        }

        foreach (var id in colonists)
        {
            if (pawns.TryGetValue(id, out var p) && (p.Downed || p.Dead))
            {
                throw new AcceptanceException($"colonist {id} downed or dead: the door loop did not hold the bear: {Describe(p)}");
            }
        }
    }

    private static string Describe(LabPosition p) =>
        string.Create(CultureInfo.InvariantCulture, $"{{Id:{p.Id} Side:{p.Side} X:{p.X} Z:{p.Z} Downed:{(p.Downed ? "true" : "false")} Dead:{(p.Dead ? "true" : "false")}}}");
}
