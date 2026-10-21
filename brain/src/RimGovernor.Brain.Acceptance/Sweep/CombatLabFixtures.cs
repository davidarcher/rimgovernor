using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>One structure or item: a Def (ThingDef) with optional Stuff.</summary>
public sealed class FixtureThing
{
    public string Def { get; set; } = "";

    public string Stuff { get; set; } = "";

    public int X { get; set; }

    public int Z { get; set; }

    /// <summary>Stages a building under the lab hostiles' faction.</summary>
    public bool Hostile { get; set; }

    /// <summary>When set, stages the building damaged.</summary>
    public int HitPoints { get; set; }

    /// <summary>An item (a chunk, a shell) rather than a building: the stage spec spawns it as a thing.</summary>
    public bool Item { get; set; }

    /// <summary>This entry as the stage spec's building or thing named <paramref name="name"/>.</summary>
    public void AddTo(string name, List<Fx.StageBuilding> buildings, List<Fx.StageThing> things)
    {
        var cell = new Fx.StageCell(X, Z);
        var stuff = Stuff.Length > 0 ? Stuff : null;
        if (Item)
        {
            things.Add(new Fx.StageThing { Name = name, Def = Def, Stuff = stuff, Cell = cell });
            return;
        }

        buildings.Add(new Fx.StageBuilding
        {
            Name = name,
            Def = Def,
            Stuff = stuff,
            Cells = [cell],
            Faction = Hostile ? Fx.StageFaction.Hostile : Fx.StageFaction.Player,
            HitPoints = HitPoints,
        });
    }
}

/// <summary>One staged combatant.</summary>
public sealed class LabPawn
{
    public string Side { get; set; } = "";

    public int Index { get; set; }

    public string Kind { get; set; } = "";

    public int X { get; set; }

    public int Z { get; set; }

    public string Weapon { get; set; } = "";

    public string WeaponStuff { get; set; } = "";

    /// <summary>Stages the pawn downed under anesthetic.</summary>
    public bool Downed { get; set; }

    /// <summary>Extra HediffDef names added at staging, e.g. a drug high or addiction.</summary>
    public List<string> Hediffs { get; set; } = [];

    /// <summary>One worn apparel def, staged charged if a shield.</summary>
    public string Apparel { get; set; } = "";

    /// <summary>The TrainableDefs an Animal has learned.</summary>
    public List<string> Trained { get; set; } = [];

    /// <summary>Stages the pawn with a blunt bruise.</summary>
    public bool Injured { get; set; }

    /// <summary>ThingDef names put one each in the pawn's inventory, e.g. a carried go-juice.</summary>
    public List<string> Inventory { get; set; } = [];

    /// <summary>Stuns the pawn directly for that many ticks.</summary>
    public int StunTicks { get; set; }

    /// <summary>
    /// This combatant as the stage spec's pawn named <paramref name="name"/>: a side is a faction and the fixed staging
    /// the combat lab gives it (hostiles and prisoners an adult of 30, no traits, every skill 8,
    /// stripped of their own gear; animals 3 years old).
    /// </summary>
    public Fx.StagePawn ToSpec(string name)
    {
        var gear = new Fx.StageGear
        {
            Weapon = Weapon.Length > 0 ? Weapon : null,
            WeaponStuff = WeaponStuff.Length > 0 ? WeaponStuff : null,
            Apparel = Apparel.Length > 0 ? [Apparel] : [],
            Inventory = Inventory,
            Strip = Side is CombatFixtures.Hostile or CombatFixtures.Prisoner,
        };
        var pawn = new Fx.StagePawn
        {
            Name = name,
            Cell = new Fx.StageCell(X, Z),
            Gear = gear,
            Downed = Downed,
            Hediffs = Hediffs,
            Injured = Injured,
            StunTicks = StunTicks,
        };
        return Side switch
        {
            CombatFixtures.Colonist => pawn with { Colonist = Index },
            CombatFixtures.Hostile or CombatFixtures.Prisoner => pawn with
            {
                Kind = Kind,
                Faction = Fx.StageFaction.Hostile,
                Prisoner = Side == CombatFixtures.Prisoner,
                Age = 30,
                ClearTraits = true,
                SkillLevel = 8,
            },
            CombatFixtures.Insect => pawn with { Kind = Kind, Faction = Fx.StageFaction.Insect },
            CombatFixtures.Mech => pawn with { Kind = Kind, Faction = Fx.StageFaction.Mechanoid },
            CombatFixtures.Animal => pawn with { Kind = Kind, Faction = Fx.StageFaction.Player, Age = 3, Trained = Trained },
            CombatFixtures.Manhunter => pawn with { Kind = Kind, Faction = Fx.StageFaction.None, Age = 3, Manhunter = true },
            CombatFixtures.Wild => pawn with { Kind = Kind, Faction = Fx.StageFaction.None, Age = 3 },
            _ => throw new AcceptanceException($"combatlab: unknown side \"{Side}\""),
        };
    }
}

/// <summary>A RoofDef over the cells MinX..MaxX, MinZ..MaxZ inclusive.</summary>
public sealed record LabRoof(string Def, int MinX, int MinZ, int MaxX, int MaxZ);

/// <summary>A siege lord's camp spot and blueprint points.</summary>
public sealed record LabSiege(int X, int Z, double Points);

/// <summary>One map cell.</summary>
public readonly record struct LabCell(int X, int Z);

/// <summary>
/// A fixture's hand-written defense layout: the firing line and its fall-back cells, the direction from the enemy
/// toward home, and the choke the blockers hold, if any.
/// </summary>
public sealed class LabLayout
{
    public List<LabCell> Firing { get; } = [];

    public List<LabCell> Retreat { get; } = [];

    /// <summary>A domain.Rotation name ("south": the enemy is north).</summary>
    public string Toward { get; set; } = "";

    public LabCell? Choke { get; set; }
}

/// <summary>One staged combat. Colonists is the lab wipe's colonist count.</summary>
public sealed class LabFixture
{
    public string Name { get; set; } = "";

    public int Colonists { get; set; }

    public List<FixtureThing> Things { get; set; } = [];

    public List<LabPawn> Pawns { get; set; } = [];

    /// <summary>A PawnsArrivalModeDef that drops the hostiles in around the first hostile's cell.</summary>
    public string Arrival { get; set; } = "";

    public LabRoof? Roof { get; set; }

    /// <summary>Puts the hostiles under a real siege lord camped at its cell instead of an assault lord.</summary>
    public LabSiege? Siege { get; set; }

    public bool PrisonBreak { get; set; }

    /// <summary>Stages the hostiles' assault lord as a sapper raid.</summary>
    public bool Sappers { get; set; }

    public LabLayout? Layout { get; set; }

    /// <summary>
    /// The stage spec test/stage applies (#3393), on absolute cells: pawn i is named <c>p{i}</c> and thing i
    /// <c>t{i}</c>, so the reply reads back in fixture order.
    /// </summary>
    public Fx.StageSpec ToSpec()
    {
        var buildings = new List<Fx.StageBuilding>();
        var things = new List<Fx.StageThing>();
        for (var i = 0; i < Things.Count; i++)
        {
            Things[i].AddTo(ThingName(i), buildings, things);
        }

        var roof = Roof is null
            ? []
            : new List<Fx.StageCells> { new() { Rect = new Fx.StageRect(Roof.MinX, Roof.MinZ, Roof.MaxX - Roof.MinX + 1, Roof.MaxZ - Roof.MinZ + 1), Roof = Roof.Def } };
        var raid = Arrival.Length > 0 || Siege is not null || PrisonBreak || Sappers
            ? new Fx.StageRaid
            {
                Arrival = Arrival.Length > 0 ? Arrival : null,
                Sappers = Sappers,
                Siege = Siege is null ? null : new Fx.StageSiege(new Fx.StageCell(Siege.X, Siege.Z), (float)Siege.Points),
                PrisonBreak = PrisonBreak,
            }
            : null;
        return new Fx.StageSpec
        {
            Cells = roof,
            Buildings = buildings,
            Things = things,
            Pawns = [.. Pawns.Select((p, i) => p.ToSpec(PawnName(i)))],
            Raid = raid,
        };
    }

    /// <summary>Pawn <paramref name="i"/>'s name in <see cref="ToSpec"/>.</summary>
    public static string PawnName(int i) => "p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Thing <paramref name="i"/>'s name in <see cref="ToSpec"/>.</summary>
    public static string ThingName(int i) => "t" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The standard combat fixtures: fixed layouts on the blank lab that every combat
/// tactic case and recording stages instead of playing a colony into a raid.
/// </summary>
public static class CombatFixtures
{
    /// <summary>Stages a fixture spec on a wiped lab.</summary>
    public const string LabTool = "test/lab_stage";

    public const string Colonist = "colonist";
    public const string Hostile = "hostile";

    /// <summary>A generated player animal of Kind.</summary>
    public const string Animal = "animal";

    /// <summary>A wild Kind gone permanently manhunter.</summary>
    public const string Manhunter = "manhunter";

    /// <summary>A calm factionless animal of Kind.</summary>
    public const string Wild = "wild";

    /// <summary>A generated hostile Kind held as the colony's prisoner.</summary>
    public const string Prisoner = "prisoner";

    /// <summary>An insect Kind of the insects' faction under an assault lord.</summary>
    public const string Insect = "insect";

    /// <summary>A mechanoid Kind of the mechanoids' faction under an assault lord.</summary>
    public const string Mech = "mech";

    public const string Rifle = "Gun_AssaultRifle";
    public const string Longsword = "MeleeWeapon_LongSword";
    public const string Club = "MeleeWeapon_Club";
    public const string Gunner = "Mercenary_Gunner";
    public const string Slasher = "Mercenary_Slasher";

    /// <summary>Fixtures with committed metrics baselines. lab-infestation can be built but has no metrics baseline.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "lab-open", "lab-choke", "lab-ranged", "lab-pods", "lab-breach", "lab-siege", "lab-manhunter", "lab-mech",
        "lab-ranged-shield", "lab-mech-line", "lab-base", "lab-split-ranged", "lab-ranged-light", "lab-ranged-lone",
        "lab-ranged-overrun",
    ];

    public const int MechStunTicks = 600;
    public const int BreachHalf = 5;
    private const int BreachRaid = 15;
    private const int BaseHalf = 9;
    public const int ChokeHalf = 7;
    private const int PodsDrop = 10;
    private const int PodsHalf = 6;
    private const int PodsSafe = -13;
    private const int PodsSafeHalf = 3;
    public const int SiegeOurMortar = -15;
    public const int SiegeCampMortar = 20;
    private const double SiegePoints = 500;
    private const int InfestRoom = 10;
    private const int InfestHalf = 5;

    /// <summary>Returns the named fixture around the lab centre (cx, cz).</summary>
    public static LabFixture Build(string name, int cx, int cz) => name switch
    {
        "lab-open" => Open(cx, cz),
        "lab-choke" => Choke(cx, cz),
        "lab-split-ranged" => SplitRanged(cx, cz),
        "lab-ranged" => Ranged(cx, cz),
        "lab-ranged-shield" => RangedShield(cx, cz),
        "lab-ranged-light" => RangedLine("lab-ranged-light", cx, cz, [-3, -1, 1, 3], [-2, 0, 2]),
        "lab-ranged-lone" => RangedLine("lab-ranged-lone", cx, cz, [-3, -1, 1, 3], [0]),
        "lab-ranged-overrun" => RangedLine("lab-ranged-overrun", cx, cz, [-1, 1], [-6, -4, -2, 0, 2, 4, 6]),
        "lab-pods" => Pods(cx, cz),
        "lab-siege" => Siege(cx, cz),
        "lab-manhunter" => ManhunterFixture(cx, cz),
        "lab-prison" => Prison(cx, cz),
        "lab-infestation" => Infestation(cx, cz),
        "lab-horror" => Horror(cx, cz),
        "lab-mech" => MechFixture(cx, cz),
        "lab-mech-line" => MechLine(cx, cz),
        "lab-breach" => Breach(cx, cz),
        "lab-base" => Base(cx, cz),
        _ => throw new AcceptanceException($"combatlab: no fixture \"{name}\""),
    };

    private static LabPawn P(string side, int x, int z, int index = 0, string kind = "", string weapon = "", string weaponStuff = "") =>
        new() { Side = side, Index = index, Kind = kind, X = x, Z = z, Weapon = weapon, WeaponStuff = weaponStuff };

    private static FixtureThing T(string def, int x, int z, string stuff = "") => new() { Def = def, Stuff = stuff, X = x, Z = z };

    private static FixtureThing I(string def, int x, int z) => new() { Def = def, X = x, Z = z, Item = true };

    // horror: lab-ranged's four riflemen behind its sandbag line and no hostile; the case fires an Anomaly incident.
    private static LabFixture Horror(int cx, int cz)
    {
        var f = Ranged(cx, cz);
        f.Name = "lab-horror";
        f.Layout = null;
        f.Pawns.RemoveAll(p => p.Side == Hostile);
        return f;
    }

    // mech: one rifleman and a scyther 25 cells north, stunned for MechStunTicks at staging.
    private static LabFixture MechFixture(int cx, int cz)
    {
        var scyther = P(Mech, cx, cz + 13, kind: "Mech_Scyther");
        scyther.StunTicks = MechStunTicks;
        return new LabFixture { Name = "lab-mech", Colonists = 1, Pawns = [P(Colonist, cx, cz - 12, weapon: Rifle), scyther] };
    }

    // mechLine: lab-ranged's four riflemen and held line against two lancers 25 cells north.
    private static LabFixture MechLine(int cx, int cz)
    {
        var f = Ranged(cx, cz);
        f.Name = "lab-mech-line";
        f.Pawns.RemoveAll(p => p.Side == Hostile);
        foreach (var dx in new[] { -2, 2 })
        {
            f.Pawns.Add(P(Mech, cx + dx, cz + 16, kind: "Mech_Lancer"));
        }

        return f;
    }

    // breach: three riflemen sealed in a doorless walled room and three club slashers north of it under a sapper lord.
    private static LabFixture Breach(int cx, int cz)
    {
        var f = new LabFixture { Name = "lab-breach", Colonists = 3, Sappers = true };
        for (var x = cx - BreachHalf; x <= cx + BreachHalf; x++)
        {
            for (var z = cz - BreachHalf; z <= cz + BreachHalf; z++)
            {
                if (x == cx - BreachHalf || x == cx + BreachHalf || z == cz - BreachHalf || z == cz + BreachHalf)
                {
                    f.Things.Add(T("Wall", x, z, "BlocksGranite"));
                }
            }
        }

        var i = 0;
        foreach (var dx in new[] { -2, 0, 2 })
        {
            f.Pawns.Add(P(Colonist, cx + dx, cz - 2, index: i, weapon: Rifle));
            f.Pawns.Add(P(Hostile, cx + dx, cz + BreachHalf + BreachRaid, kind: Slasher, weapon: Club, weaponStuff: "WoodLog"));
            i++;
        }

        return f;
    }

    // base: a small defensive compound in the middle of the map, the closest fixture to a real colony's defense.
    private static LabFixture Base(int cx, int cz)
    {
        var f = new LabFixture { Name = "lab-base", Colonists = 6 };
        for (var x = cx - BaseHalf; x <= cx + BaseHalf; x++)
        {
            for (var z = cz - BaseHalf; z <= cz + BaseHalf; z++)
            {
                var edge = x == cx - BaseHalf || x == cx + BaseHalf || z == cz - BaseHalf || z == cz + BaseHalf;
                if (edge && !(x == cx && z == cz + BaseHalf))
                {
                    f.Things.Add(T("Wall", x, z, "BlocksGranite"));
                }
            }
        }

        for (var dx = -4; dx <= 4; dx++)
        {
            f.Things.Add(T("Sandbags", cx + dx, cz + 4));
        }

        foreach (var dx in new[] { -3, 3 })
        {
            f.Things.Add(T("Turret_MiniTurret", cx + dx, cz + BaseHalf - 2));
        }

        var i = 0;
        foreach (var dx in new[] { -1, 1 })
        {
            f.Pawns.Add(P(Colonist, cx + dx, cz + BaseHalf - 2, index: i, weapon: Longsword, weaponStuff: "Steel"));
            i++;
        }

        f.Layout = new LabLayout { Toward = "south", Choke = new LabCell(cx, cz + BaseHalf) };
        i = 0;
        foreach (var dx in new[] { -3, -1, 1, 3 })
        {
            f.Pawns.Add(P(Colonist, cx + dx, cz + 3, index: 2 + i, weapon: Rifle));
            f.Layout.Firing.Add(new LabCell(cx + dx, cz + 3));
            f.Layout.Retreat.Add(new LabCell(cx + dx, cz - BaseHalf + 2));
            i++;
        }

        foreach (var dx in new[] { -8, -4, 0, 4, 8 })
        {
            f.Pawns.Add(P(Hostile, cx + dx, cz + BaseHalf + 16, kind: Gunner, weapon: Rifle));
        }

        foreach (var dx in new[] { -6, 0, 6 })
        {
            f.Pawns.Add(P(Hostile, cx + dx, cz + BaseHalf + 14, kind: Slasher, weapon: Club, weaponStuff: "WoodLog"));
        }

        return f;
    }

    // open: three riflemen on an open field behind four stone chunks, three club raiders 20 cells north.
    private static LabFixture Open(int cx, int cz)
    {
        var f = new LabFixture { Name = "lab-open", Colonists = 3 };
        foreach (var dx in new[] { -3, -1, 1, 3 })
        {
            f.Things.Add(I("ChunkGranite", cx + dx, cz - 9));
        }

        var i = 0;
        foreach (var dx in new[] { -2, 0, 2 })
        {
            f.Pawns.Add(P(Colonist, cx + dx, cz - 10, index: i, weapon: Rifle));
            f.Pawns.Add(P(Hostile, cx + dx, cz + 10, kind: Slasher, weapon: Club, weaponStuff: "WoodLog"));
            i++;
        }

        return f;
    }

    // splitRanged: the ranged-only held-out fixture of the calibration gate (#2900).
    private static LabFixture SplitRanged(int cx, int cz)
    {
        var f = new LabFixture { Name = "lab-split-ranged", Colonists = 4 };
        foreach (var dx in new[] { -3, -1, 1, 3 })
        {
            f.Things.Add(I("ChunkGranite", cx + dx, cz - 9));
        }

        f.Layout = new LabLayout { Toward = "south" };
        var i = 0;
        foreach (var dx in new[] { -3, -1, 1, 3 })
        {
            f.Pawns.Add(P(Colonist, cx + dx, cz - 10, index: i, weapon: Rifle));
            f.Layout.Firing.Add(new LabCell(cx + dx, cz - 10));
            f.Layout.Retreat.Add(new LabCell(cx + dx, cz - 15));
            i++;
        }

        foreach (var dz in new[] { 14, 18 })
        {
            foreach (var dx in new[] { -12, -8, 8, 12 })
            {
                f.Pawns.Add(P(Hostile, cx + dx, cz + dz, kind: Gunner, weapon: Rifle));
            }
        }

        return f;
    }

    // choke: a 15x15 granite-walled room whose only opening is one cell in the middle of the north wall.
    private static LabFixture Choke(int cx, int cz)
    {
        var f = new LabFixture { Name = "lab-choke", Colonists = 5 };
        for (var x = cx - ChokeHalf; x <= cx + ChokeHalf; x++)
        {
            for (var z = cz - ChokeHalf; z <= cz + ChokeHalf; z++)
            {
                var edge = x == cx - ChokeHalf || x == cx + ChokeHalf || z == cz - ChokeHalf || z == cz + ChokeHalf;
                if (edge && !(x == cx && z == cz + ChokeHalf))
                {
                    f.Things.Add(T("Wall", x, z, "BlocksGranite"));
                }
            }
        }

        var i = 0;
        foreach (var dx in new[] { -1, 0, 1 })
        {
            f.Pawns.Add(P(Colonist, cx + dx, cz + ChokeHalf - 2, index: i, weapon: Longsword, weaponStuff: "Steel"));
            i++;
        }

        f.Pawns.Add(P(Colonist, cx - 2, cz, index: 3, weapon: Rifle));
        f.Pawns.Add(P(Colonist, cx + 2, cz, index: 4, weapon: Rifle));
        for (var dx = -5; dx <= 5; dx += 2)
        {
            f.Pawns.Add(P(Hostile, cx + dx, cz + ChokeHalf + 13, kind: Slasher, weapon: Club, weaponStuff: "WoodLog"));
        }

        f.Layout = new LabLayout { Toward = "south", Choke = new LabCell(cx, cz + ChokeHalf) };
        foreach (var dx in new[] { -2, 2, -4, 4 })
        {
            f.Layout.Firing.Add(new LabCell(cx + dx, cz + ChokeHalf - 4));
            f.Layout.Retreat.Add(new LabCell(cx + dx, cz - ChokeHalf + 2));
        }

        return f;
    }

    // pods: four riflemen between two walled rooms, an unarmed colonist inside the landing room, four rifle raiders dropped by pods.
    private static LabFixture Pods(int cx, int cz)
    {
        var f = new LabFixture { Name = "lab-pods", Colonists = 5, Arrival = "CenterDrop" };
        f.Things.AddRange(WalledRoom(cx, cz + PodsDrop, PodsHalf, new LabCell(cx, cz + PodsDrop - PodsHalf)));
        f.Things.AddRange(WalledRoom(cx, cz + PodsSafe, PodsSafeHalf, new LabCell(cx, cz + PodsSafe + PodsSafeHalf)));
        var i = 0;
        foreach (var dx in new[] { -3, -1, 1, 3 })
        {
            f.Pawns.Add(P(Colonist, cx + dx, cz - 3, index: i, weapon: Rifle));
            i++;
        }

        f.Pawns.Add(P(Colonist, cx + 4, cz + PodsDrop - 4, index: 4));
        for (var n = 0; n < 4; n++)
        {
            f.Pawns.Add(P(Hostile, cx, cz + PodsDrop, kind: Gunner, weapon: Rifle));
        }

        return f;
    }

    // siege: three riflemen by an unroofed player mortar, and four raiders under a real siege lord at the camp spot.
    private static LabFixture Siege(int cx, int cz)
    {
        var f = new LabFixture { Name = "lab-siege", Colonists = 3 };
        f.Things.Add(T("Turret_Mortar", cx, cz + SiegeOurMortar));
        f.Things.Add(I("Shell_HighExplosive", cx + 2, cz + SiegeOurMortar - 1));
        f.Things.Add(I("Shell_EMP", cx - 2, cz + SiegeOurMortar - 1));
        f.Siege = new LabSiege(cx, cz + SiegeCampMortar, SiegePoints);
        var i = 0;
        foreach (var dx in new[] { -2, 0, 2 })
        {
            f.Pawns.Add(P(Colonist, cx + dx, cz + SiegeOurMortar + 3, index: i, weapon: Rifle));
            i++;
        }

        foreach (var dx in new[] { -3, -1, 1, 3 })
        {
            f.Pawns.Add(P(Hostile, cx + dx, cz + SiegeCampMortar, kind: Gunner, weapon: Rifle));
        }

        return f;
    }

    // walledRoom is a granite wall ring of half edge half around (x, z) with a wooden door at door.
    public static List<FixtureThing> WalledRoom(int x, int z, int half, LabCell door)
    {
        var output = new List<FixtureThing>();
        for (var wx = x - half; wx <= x + half; wx++)
        {
            for (var wz = z - half; wz <= z + half; wz++)
            {
                if (wx == door.X && wz == door.Z)
                {
                    output.Add(T("Door", wx, wz, "WoodLog"));
                }
                else if (wx == x - half || wx == x + half || wz == z - half || wz == z + half)
                {
                    output.Add(T("Wall", wx, wz, "BlocksGranite"));
                }
            }
        }

        return output;
    }

    // ranged: four riflemen, one empty cell apart, behind a seven-cell sandbag line; four rifle raiders 25 cells north.
    private static LabFixture Ranged(int cx, int cz)
    {
        var f = new LabFixture { Name = "lab-ranged", Colonists = 4 };
        for (var dx = -3; dx <= 3; dx++)
        {
            f.Things.Add(T("Sandbags", cx + dx, cz - 8));
        }

        f.Layout = new LabLayout { Toward = "south" };
        var i = 0;
        foreach (var dx in new[] { -3, -1, 1, 3 })
        {
            f.Pawns.Add(P(Colonist, cx + dx, cz - 9, index: i, weapon: Rifle));
            f.Pawns.Add(P(Hostile, cx + dx, cz + 16, kind: Gunner, weapon: Rifle));
            f.Layout.Firing.Add(new LabCell(cx + dx, cz - 9));
            f.Layout.Retreat.Add(new LabCell(cx + dx, cz - 14));
            i++;
        }

        return f;
    }

    // rangedLine: lab-ranged's sandbag line and held layout with a rifleman at each defender offset and a rifle raider 25
    // cells north at each raider offset. The exposure calibration's fixtures (#3561) vary the counts to move the raid
    // points over defense capacity ratio: light (4 on 3) 0.90, lone (4 on 1) 0.30, overrun (2 on 7) 4.21.
    private static LabFixture RangedLine(string name, int cx, int cz, int[] defenders, int[] raiders)
    {
        var f = new LabFixture { Name = name, Colonists = defenders.Length, Layout = new LabLayout { Toward = "south" } };
        for (var dx = -3; dx <= 3; dx++)
        {
            f.Things.Add(T("Sandbags", cx + dx, cz - 8));
        }

        for (var i = 0; i < defenders.Length; i++)
        {
            var x = cx + defenders[i];
            f.Pawns.Add(P(Colonist, x, cz - 9, index: i, weapon: Rifle));
            f.Layout.Firing.Add(new LabCell(x, cz - 9));
            f.Layout.Retreat.Add(new LabCell(x, cz - 14));
        }

        foreach (var dx in raiders)
        {
            f.Pawns.Add(P(Hostile, cx + dx, cz + 16, kind: Gunner, weapon: Rifle));
        }

        return f;
    }

    // rangedShield: lab-ranged plus a fifth colonist, a longsword fighter in a charged shield belt.
    private static LabFixture RangedShield(int cx, int cz)
    {
        var f = Ranged(cx, cz);
        f.Name = "lab-ranged-shield";
        f.Colonists = 5;
        var tank = P(Colonist, cx, cz - 11, index: 4, weapon: Longsword);
        tank.Apparel = "Apparel_ShieldBelt";
        f.Pawns.Add(tank);
        return f;
    }

    // manhunter: one colonist, a release-trained husky beside her and an untrained one, two permanent-manhunter wargs.
    private static LabFixture ManhunterFixture(int cx, int cz)
    {
        var trained = P(Animal, cx + 1, cz, kind: "Husky");
        trained.Trained = ["Tameness", "Obedience", "Release"];
        return new LabFixture
        {
            Name = "lab-manhunter",
            Colonists = 1,
            Pawns =
            [
                P(Colonist, cx, cz),
                trained,
                P(Animal, cx - 1, cz, kind: "Husky"),
                P(Manhunter, cx - 2, cz + 15, kind: "Warg"),
                P(Manhunter, cx + 2, cz + 15, kind: "Warg"),
            ],
        };
    }

    // infestation: a hive in a mountain room with three insects around it.
    private static LabFixture Infestation(int cx, int cz)
    {
        var rz = cz + InfestRoom;
        var f = new LabFixture
        {
            Name = "lab-infestation",
            Colonists = 5,
            Roof = new LabRoof("RoofRockThick", cx - InfestHalf, rz - InfestHalf, cx + InfestHalf, rz + InfestHalf),
        };
        for (var x = cx - InfestHalf; x <= cx + InfestHalf; x++)
        {
            for (var z = rz - InfestHalf; z <= rz + InfestHalf; z++)
            {
                var edge = x == cx - InfestHalf || x == cx + InfestHalf || z == rz - InfestHalf || z == rz + InfestHalf;
                if (edge && !(x == cx && z == rz - InfestHalf))
                {
                    f.Things.Add(T("Granite", x, z));
                }
            }
        }

        f.Things.Add(T("Hive", cx, rz + 2));
        f.Pawns =
        [
            P(Insect, cx - 2, rz + 1, kind: "Megascarab"),
            P(Insect, cx + 2, rz + 1, kind: "Spelopede"),
            P(Insect, cx, rz, kind: "Megaspider"),
            P(Colonist, cx - 1, cz + 3, index: 0, weapon: Longsword, weaponStuff: "Steel"),
            P(Colonist, cx + 1, cz + 3, index: 1, weapon: Longsword, weaponStuff: "Steel"),
            P(Colonist, cx - 3, cz, index: 2, weapon: Rifle),
            P(Colonist, cx + 3, cz, index: 3, weapon: Rifle),
            P(Colonist, cx, cz + 1, index: 4, weapon: "Weapon_GrenadeFrag"),
        ];
        f.Layout = new LabLayout { Toward = "south", Choke = new LabCell(cx, rz - InfestHalf) };
        foreach (var dx in new[] { -3, 3 })
        {
            f.Layout.Firing.Add(new LabCell(cx + dx, cz));
            f.Layout.Retreat.Add(new LabCell(cx + dx, cz - 5));
        }

        return f;
    }

    // prison: two prisoners, one bruised, break out of a 7x7 granite cell; four wardens wait south of it.
    private static LabFixture Prison(int cx, int cz)
    {
        var f = new LabFixture { Name = "lab-prison", Colonists = 4, PrisonBreak = true };
        f.Things = WalledRoom(cx, cz + 8, 3, new LabCell(cx, cz + 5));
        var bruised = P(Prisoner, cx + 1, cz + 9, kind: Slasher);
        bruised.Injured = true;
        f.Pawns =
        [
            P(Prisoner, cx - 1, cz + 9, kind: Slasher),
            bruised,
            P(Colonist, cx - 3, cz, index: 0),
            P(Colonist, cx - 1, cz, index: 1),
            P(Colonist, cx + 1, cz, index: 2, weapon: "MeleeWeapon_Mace", weaponStuff: "Steel"),
            P(Colonist, cx + 3, cz - 2, index: 3, weapon: "Gun_Revolver"),
        ];
        return f;
    }
}
