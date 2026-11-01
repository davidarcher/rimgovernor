using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.Policy;
using Xunit;
using F = RimGovernor.Brain.GameFacts.Observations;
using O = RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The defense candidates' probe and protection (task 7.2): the batched probe rows join the candidates by id and a row
/// no probe asked for is refused; staffing excludes the violence-incapable and the downed; an unpowered turret protects
/// nothing and is named; small arms need no ammunition while a mortar counts its shells; the civilians on the observed
/// raid walk are counted before and after the candidate's walls; and with no raid observed every term is unassessed.
/// The recorded comparison sets an open sandbag line beside a doored perimeter from recorded probe rows; no combat is
/// simulated and no raid spawned.
/// </summary>
public sealed class DefenseProtectionTests
{
    /// <summary>One observed melee raid (range 0) walking north from (22,44) past (22,50) to (22,58).</summary>
    private static readonly DefenseThreat Raid = new() { Trails = [[new Cell(22, 44), new Cell(22, 50), new Cell(22, 58)]], ArrivalEdges = [new Cell(22, 44)] };

    /// <summary>An open sandbag line across the walk at z 52, shot from behind at (21,53) and (23,53).</summary>
    private static readonly DefenseCandidate Cover = new()
    {
        Kind = DefenseCandidateKind.TemporaryCover,
        Key = "temporary_cover@21,52",
        Structures = [.. new[] { 21, 22, 23 }.Select(x => new DefenseStructure("Sandbags", new Cell(x, 52), "", false))],
        Firing = [new Cell(21, 53), new Cell(23, 53)],
        Threat = Raid,
    };

    /// <summary>A doored perimeter across the walk at z 52: walls at x 20, 21, 23, 24 and a door at x 22, held from the door.</summary>
    private static readonly DefenseCandidate Perimeter = new()
    {
        Kind = DefenseCandidateKind.PerimeterDoored,
        Key = "perimeter_doored@20,52",
        Structures =
        [
            .. new[] { 20, 21, 23, 24 }.Select(x => new DefenseStructure("Wall", new Cell(x, 52), "WoodLog", true)),
            new DefenseStructure("Door", new Cell(22, 52), "WoodLog", false),
        ],
        Closes = [new Cell(20, 52), new Cell(21, 52), new Cell(23, 52), new Cell(24, 52)],
        Barriers = [new Cell(20, 52), new Cell(21, 52), new Cell(22, 52), new Cell(23, 52), new Cell(24, 52)],
        Firing = [new Cell(22, 52)],
        Threat = Raid,
    };

    /// <summary>
    /// The probe reply recorded for <see cref="Cover"/> and <see cref="Perimeter"/> against the melee raid: two rifle
    /// colonists, no turret, no shells; the raid engages the line on its sandbag cell and at its last cell, where the bags give
    /// 0.75 against the shot from the south and the open ground none, and it engages the perimeter at the cell before its door,
    /// which gives no cover.
    /// </summary>
    private const string Recorded = """
        {"context":{"identity":{"colonyId":"colony","loadToken":"load","mapId":0},"tick":100,"nativeGeneration":{"type":"unclaimed"}},
         "candidates":[
          {"id":"temporary_cover@21,52","lines":[
            {"type":"traced","from":{"x":21,"z":53},"to":{"x":22,"z":52},"distance":1.41,"lineOfSight":true,"cover":0.75},
            {"type":"traced","from":{"x":21,"z":53},"to":{"x":22,"z":58},"distance":5.1,"lineOfSight":true,"cover":0},
            {"type":"traced","from":{"x":23,"z":53},"to":{"x":22,"z":52},"distance":1.41,"lineOfSight":true,"cover":0.75},
            {"type":"traced","from":{"x":23,"z":53},"to":{"x":22,"z":58},"distance":5.1,"lineOfSight":true,"cover":0}]},
          {"id":"perimeter_doored@20,52","lines":[
            {"type":"traced","from":{"x":22,"z":52},"to":{"x":22,"z":51},"distance":1,"lineOfSight":true,"cover":0}]}],
         "shooters":[
          {"pawn":"Human1","verb":{"range":30,"minRange":0,"warmup":1,"cooldown":1.7,"burstShots":1,"burstSeconds":0,"lineOfSight":true},"shooting":8,"violentDisabled":false,"downed":false},
          {"pawn":"Human2","verb":{"range":30,"minRange":0,"warmup":1,"cooldown":1.7,"burstShots":1,"burstSeconds":0,"lineOfSight":true},"shooting":5,"violentDisabled":false,"downed":false}],
         "turrets":[],
         "shells":0}
        """;

    private static readonly F.DefenseVerb Rifle = new() { Range = 30, MinRange = 0, Warmup = 1, Cooldown = 1.7, BurstShots = 1, BurstSeconds = 0, LineOfSight = true };

    /// <summary>A field across the raid's walk at z 56 (x 20 to 24).</summary>
    private static DefenseGround Field() => DefenseGround.Of(new LayoutPlan { Zones = [new LayoutZone { Kind = LayoutZoneKind.Field, Runs = [new RowRun(56, 20, 5)] }] });

    private static F.DefenseShooter Shooter(string id, bool violentDisabled = false, bool downed = false) =>
        new() { Pawn = new PawnId(id), Verb = Rifle, Shooting = 5, ViolentDisabled = violentDisabled, Downed = downed };

    private static F.DefenseProbeSnapshot Snapshot(IEnumerable<F.DefenseProbeAnswer> answers, IEnumerable<F.DefenseShooter> shooters, IEnumerable<F.DefenseTurretProbe>? turrets = null, int shells = 0) => new()
    {
        Context = DefenseWire.Context,
        Candidates = [.. answers],
        Shooters = [.. shooters],
        Turrets = [.. turrets ?? []],
        Shells = shells,
    };

    private static F.DefenseSightLine.Traced Line(Cell from, Cell to) => new()
    {
        From = from,
        To = to,
        Distance = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Z - from.Z, 2)),
        LineOfSight = true,
        Cover = 0,
    };

    /// <summary>The answer tracing every probed firing cell (and the given turret cells) to every probed approach cell.</summary>
    private static F.DefenseProbeAnswer Answer(O.DefenseCandidateProbe probe, params Cell[] turrets) => new()
    {
        Id = probe.Id,
        Lines = [.. probe.Firing.Concat(turrets).SelectMany(f => probe.Approach.Select(a => (F.DefenseSightLine)Line(f, a)))],
    };

    private static F.DefenseTurretProbe Turret(string id, Cell cell, F.BenchPower power, F.DefenseTurretAmmo? ammo = null, bool mannable = false)
    {
        var shells = ammo is F.DefenseTurretAmmo.Shells;
        return new()
        {
            Id = id,
            Cell = cell,
            Planned = false,
            Verb = new F.DefenseVerb { Range = 28.9, MinRange = shells ? 8 : 0, Warmup = 0, Cooldown = 4.8, BurstShots = 3, BurstSeconds = 0.4, LineOfSight = !shells },
            BurstWarmup = 1.5,
            Power = power,
            Mannable = mannable,
            Ammo = ammo ?? new F.DefenseTurretAmmo.None(),
        };
    }

    private static double Value(DefenseTerm t) => Assert.IsType<DefenseTerm.Assessed>(t).Value;

    private static string Fact(DefenseTerm t) => Assert.IsType<DefenseTerm.Unassessed>(t).Fact;

    private static IEnumerable<DefenseTerm> Terms(DefenseProtection p) => [p.Covered, p.Shots, p.Cover, p.Staffing, p.Ammunition, p.Power, p.Exposure];

    /// <summary>
    /// (a) The lines join their candidate by id; a reply row for a candidate no probe asked for is refused, and an asked
    /// candidate the reply omits is named, not zeroed. The melee raid engages the open line where its walk crosses the bags and
    /// at its last cell, and the perimeter at the cell before the door that stops it; a rifle raid engages every walk cell
    /// within its range of the pieces, and every observed walk is probed, not a newest few.
    /// </summary>
    [Fact]
    public void LinesJoinByIdAndAnUnknownIdIsRefused()
    {
        var probes = DefenseProtectionRules.Probes([Cover, Perimeter]);
        Assert.Equal(["temporary_cover@21,52", "perimeter_doored@20,52"], probes.Select(p => p.Id));
        Assert.Equal([new Cell(22, 52), new Cell(22, 58)], probes[0].Approach);
        Assert.Equal([new Cell(22, 51)], probes[1].Approach);
        Assert.Equal(5, probes[1].Pieces.Count);
        var rifles = Raid with { Range = 3, Trails = [.. Raid.Trails, [new Cell(30, 44), new Cell(30, 48)]] };
        Assert.Equal([new Cell(22, 49), new Cell(22, 50), new Cell(22, 51), new Cell(30, 48)],
            DefenseProtectionRules.Approach(rifles, Perimeter with { Threat = rifles }));

        var joined = DefenseProtectionRules.Protections([Cover, Perimeter], probes, Snapshot([Answer(probes[1])], [Shooter("Human1")]), Field());
        Assert.All(Terms(joined[0]), t => Assert.Equal("defense_probe:temporary_cover@21,52", Fact(t)));
        Assert.Equal(1, Value(joined[1].Covered));

        var stray = Snapshot([Answer(probes[0]), Answer(probes[1]) with { Id = "killbox@0,0" }], [Shooter("Human1")]);
        var refused = Assert.Throws<RequiredFactException>(() => DefenseProtectionRules.Protections([Cover, Perimeter], probes, stray, Field()));
        Assert.Equal(DefenseProtectionRules.UnknownCandidate("killbox@0,0"), refused.Fact);
    }

    /// <summary>(b) Four firing cells and two able shooters: the violence-incapable and the downed colonists do not count; the shortfall is named 4/2 and the staffing term is the two able (priced, not unassessed).</summary>
    [Fact]
    public void StaffingExcludesTheViolenceIncapableAndTheDowned()
    {
        var line = Cover with { Firing = [new Cell(20, 53), new Cell(21, 53), new Cell(23, 53), new Cell(24, 53)] };
        var probe = Assert.Single(DefenseProtectionRules.Probes([line]));
        var shooters = new[] { Shooter("Human1"), Shooter("Human2"), Shooter("Human3", violentDisabled: true), Shooter("Human4", downed: true), Shooter("Human5") with { Verb = null } };
        var p = Assert.Single(DefenseProtectionRules.Protections([line], [probe], Snapshot([Answer(probe)], shooters), Field()));
        Assert.Equal(2, Value(p.Staffing));
        Assert.Contains("staffing:4/2", p.States);
        // The two who can shoot still fire from the first two cells.
        Assert.Equal(2 / 2.7, Value(p.Shots), 6);

        var seated = Assert.Single(DefenseProtectionRules.Probes([Cover]));
        var staffed = Assert.Single(DefenseProtectionRules.Protections([Cover], [seated], Snapshot([Answer(seated)], shooters), Field()));
        Assert.Equal(2, Value(staffed.Staffing));
    }

    /// <summary>(c) An unpowered turret protects nothing and is named power:&lt;id&gt;; a powered one adds its burst over its cycle.</summary>
    [Fact]
    public void AnUnpoweredTurretIsZeroAndNamedAndAPoweredOneFiresItsBurst()
    {
        var emplacement = new DefenseCandidate { Kind = DefenseCandidateKind.Turrets, Key = "turrets@22,40", Threat = Raid };
        var probe = Assert.Single(DefenseProtectionRules.Probes([emplacement]));
        var at = new Cell(22, 40);

        var dark = Turret("Turret_MiniTurret1", at, new F.BenchPower.Unconnected(null));
        var off = Assert.Single(DefenseProtectionRules.Protections([emplacement], [probe], Snapshot([Answer(probe, at)], [], [dark]), Field()));
        Assert.Equal(0, Value(off.Power));
        Assert.Equal(0, Value(off.Shots));
        Assert.Equal(0, Value(off.Covered));
        Assert.Contains("power:Turret_MiniTurret1", off.States);

        var lit = dark with { Power = new F.BenchPower.Connected("net:1", 0, 300) };
        var on = Assert.Single(DefenseProtectionRules.Protections([emplacement], [probe], Snapshot([Answer(probe, at)], [], [lit]), Field()));
        Assert.Equal(1, Value(on.Power));
        Assert.Equal(3 / (1.5 + 0 + 4.8 + 0.4), Value(on.Shots), 6);
        Assert.Equal(1, Value(on.Covered));
        Assert.DoesNotContain(on.States, s => s.StartsWith("power:", StringComparison.Ordinal));
    }

    /// <summary>(d) Rifles load nothing (a known zero, not unread); a mortar counts the shells in stock, and with none loaded or stocked it reaches nothing: the term is the known zero stock and the state names the mortar.</summary>
    [Fact]
    public void RiflesNeedNoAmmunitionAndAMortarCountsItsShells()
    {
        var probe = Assert.Single(DefenseProtectionRules.Probes([Cover]));
        var rifles = Assert.Single(DefenseProtectionRules.Protections([Cover], [probe], Snapshot([Answer(probe)], [Shooter("Human1"), Shooter("Human2")]), Field()));
        Assert.Equal(0, Value(rifles.Ammunition));

        var mortar = Turret("Turret_Mortar1", new Cell(22, 30), new F.BenchPower.Unneeded(), new F.DefenseTurretAmmo.Shells { Loaded = null }, mannable: true);
        var shooters = new[] { Shooter("Human1"), Shooter("Human2"), Shooter("Human3") };
        var stocked = Assert.Single(DefenseProtectionRules.Protections([Cover], [probe], Snapshot([Answer(probe, mortar.Cell)], shooters, [mortar], shells: 5), Field()));
        Assert.Equal(5, Value(stocked.Ammunition));
        Assert.Equal(3, Value(stocked.Staffing));

        var dry = Assert.Single(DefenseProtectionRules.Protections([Cover], [probe], Snapshot([Answer(probe, mortar.Cell)], shooters, [mortar], shells: 0), Field()));
        Assert.Equal(0, Value(dry.Ammunition));
        Assert.Contains("shells:Turret_Mortar1", dry.States);
        Assert.True(Value(dry.Shots) <= Value(stocked.Shots));
    }

    /// <summary>(e) The field on the raid's walk counts before the perimeter's wall and not after it; the open sandbag line leaves it exposed.</summary>
    [Fact]
    public void AFieldOnTheApproachCountsBeforeTheWallNotAfter()
    {
        var walled = DefenseProtectionRules.Civilians(Perimeter, Field());
        Assert.Equal(["ground:field:0"], walled.Before);
        Assert.Empty(walled.After);

        var open = DefenseProtectionRules.Civilians(Cover, Field());
        Assert.Equal(["ground:field:0"], open.Before);
        Assert.Equal(["ground:field:0"], open.After);
    }

    /// <summary>
    /// (f) From the recorded probe rows, the open sandbag line and the doored perimeter protect differently: the line
    /// covers the walk from two seats behind 0.75 cover with the field still on the walk; the perimeter holds one door with
    /// no cover and keeps the field off the walk.
    /// </summary>
    [Fact]
    public void RecordedCoverAndDooredPerimeterGetDifferentProtection()
    {
        var read = OrdersJson.Deserialize<F.DefenseProbeSnapshot>(Recorded);
        var probes = DefenseProtectionRules.Probes([Cover, Perimeter]);
        var protections = DefenseProtectionRules.Protections([Cover, Perimeter], probes, read, Field());
        var (line, perimeter) = (protections[0], protections[1]);

        Assert.Equal("temporary_cover@21,52", line.Candidate);
        Assert.Equal(1, Value(line.Covered));
        Assert.Equal(2 / 2.7, Value(line.Shots), 6);
        Assert.Equal(0.375, Value(line.Cover), 6);
        Assert.Equal(2, Value(line.Staffing));
        Assert.Equal(0, Value(line.Ammunition));
        Assert.Equal(0, Value(line.Power));
        Assert.Equal(1, Value(line.Exposure));

        Assert.Equal("perimeter_doored@20,52", perimeter.Candidate);
        Assert.Equal(1, Value(perimeter.Covered));
        Assert.Equal(1 / 2.7, Value(perimeter.Shots), 6);
        Assert.Equal(0, Value(perimeter.Cover));
        Assert.Equal(1, Value(perimeter.Staffing));
        Assert.Equal(0, Value(perimeter.Exposure));
        Assert.Equal(["ground:field:0"], perimeter.Civilians.Before);
    }

    /// <summary>
    /// The raiders' weapon range (RaidTrack.Range) sets the exposure: a melee raid (range 0) exposes only the routine
    /// positions on its walk, so a field eight cells beside it is safe; a rifle raid (range 24.9) exposes that field too,
    /// with or without the perimeter's wall, and either way the term is assessed, never named.
    /// </summary>
    [Fact]
    public void RaiderWeaponRangeSetsTheExposedPositions()
    {
        var beside = DefenseGround.Of(new LayoutPlan { Zones = [new LayoutZone { Kind = LayoutZoneKind.Field, Runs = [new RowRun(48, 30, 3)] }] });
        var melee = DefenseProtectionRules.Civilians(Perimeter with { Threat = Raid with { Range = 0 } }, beside);
        Assert.Empty(melee.Before);
        Assert.Empty(melee.After);

        var rifle = DefenseProtectionRules.Civilians(Perimeter with { Threat = Raid with { Range = 24.9 } }, beside);
        Assert.Equal(["ground:field:0"], rifle.Before);
        Assert.Equal(["ground:field:0"], rifle.After);

        var read = OrdersJson.Deserialize<F.DefenseProbeSnapshot>(Recorded);
        var armed = new[] { Cover with { Threat = Raid with { Range = 24.9 } }, Perimeter with { Threat = Raid with { Range = 24.9 } } };
        var protections = DefenseProtectionRules.Protections(armed, DefenseProtectionRules.Probes(armed), read, beside);
        Assert.All(protections, p => Assert.Equal(1, Value(p.Exposure)));
        Assert.All(protections, p => Assert.DoesNotContain(p.States, s => s.StartsWith("exposure", StringComparison.Ordinal)));
    }

    /// <summary>The recorded reply and the probe request round-trip exactly through the wire JSON.</summary>
    [Fact]
    public void TheProbeRoundTripsExactly()
    {
        var json = OrdersJson.Serialize(OrdersJson.Deserialize<F.DefenseProbeSnapshot>(Recorded));
        Assert.Equal(json, OrdersJson.Serialize(OrdersJson.Deserialize<F.DefenseProbeSnapshot>(json)));
        var request = new O.DefenseProbeRequest { Scope = new F.ReadScope(), Candidates = [.. DefenseProtectionRules.Probes([Cover, Perimeter])] };
        var asked = OrdersJson.Serialize(request);
        Assert.Equal(asked, OrdersJson.Serialize(OrdersJson.Deserialize<O.DefenseProbeRequest>(asked)));
    }

    /// <summary>(g) With no ground raid in the saved raid tracks nothing is probed and every term names the missing arrival edges; a fogged line or an unread probe leaves the terms named, not zeroed.</summary>
    [Fact]
    public void NoArrivalEdgesLeaveEveryTermUnassessed()
    {
        var unseen = DefenseCandidateRules.ThreatOf(new ReviewFacts());
        var blind = new[] { Cover with { Threat = unseen }, Perimeter with { Threat = unseen } };
        Assert.Empty(DefenseProtectionRules.Probes(blind));
        var protections = DefenseProtectionRules.Protections(blind, [], null, Field());
        Assert.All(protections, p => Assert.All(Terms(p), t => Assert.Equal(DefenseProtectionRules.ArrivalUnobserved, Fact(t))));

        var probe = Assert.Single(DefenseProtectionRules.Probes([Perimeter]));
        var fogged = new F.DefenseProbeAnswer { Id = probe.Id, Lines = [new F.DefenseSightLine.Fogged { From = new Cell(22, 52), To = new Cell(22, 45), Distance = 7 }] };
        var p = Assert.Single(DefenseProtectionRules.Protections([Perimeter], [probe], Snapshot([fogged], [Shooter("Human1")]), Field()));
        Assert.Equal(DefenseProtectionRules.Fogged, Fact(p.Covered));
        Assert.Equal(DefenseProtectionRules.Fogged, Fact(p.Shots));
        Assert.Equal(1, Value(p.Staffing));

        var unread = Assert.Single(DefenseProtectionRules.Protections([Perimeter], [probe], null, Field()));
        Assert.All(Terms(unread), t => Assert.Equal(DefenseProtectionRules.ProbeUnread, Fact(t)));
    }
}
