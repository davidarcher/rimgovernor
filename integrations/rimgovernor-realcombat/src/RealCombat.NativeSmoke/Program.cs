using RimGovernor.RealCombat;
using RimGovernor.RealCombat.Native;
using Verse;
using Mirror = RimGovernor.Contracts.Wire.Mirror;
using Operations = RimGovernor.Contracts.Wire.Operations;

// One duel in which the colonist is observed through the mod's combat mirror and commanded through its combat order
// handler (the raider stands still: nothing drives it yet). With --bare the built-in retargeting plays both sides.
// Exit 0 when the seat observed events and applied the draft and the attack.
// With --policy the live combat policy (PolicyController) plays the colonist.
//   RimGovernor.RealCombat.NativeSmoke --game <dir> [--bare|--policy]
var game = args[Array.IndexOf(args, "--game") + 1];
var bare = args.Contains("--bare");
var policy = args.Contains("--policy");
var request = new RealFightRequest
{
    Name = "native-smoke",
    Width = 60,
    Height = 60,
    Terrain = "Soil",
    Weather = "Clear",
    Seeds = [1],
    MaxTicks = 3000,
    Pawns =
    [
        new RealPawn { Id = "col", Side = "colonist", Kind = "Colonist", X = 20, Z = 30, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
        new RealPawn { Id = "host", Side = "hostile", Kind = "Pirate", X = 45, Z = 30, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
    ],
};
// --policy fights inside a walled compound with the killbox the colony's planner lays out (DefenseStage); --open is
// the same fight on open ground, where the policy holds no layout and forms the squad fallback.
var open = args.Contains("--open");
DefenseStage? stage = null;
if (policy)
{
    // The policy fields at least two defenders for each opponent, so a duel is never one of its fights: two against one.
    var home = new RimGovernor.Brain.Domain.Cell(20, 30);
    if (!open)
    {
        var (_, _, h, _) = RimGovernor.Brain.Policy.PolicyFunctions.LayoutKillbox(RimGovernor.Brain.Policy.PolicyFunctions.OpeningPlan(RimGovernor.Brain.Domain.Rotation.West), new RimGovernor.Brain.Policy.Bounds(request.Width, request.Height));
        home = h;
    }

    request = request with
    {
        Pawns =
        [
            new RealPawn { Id = "col0", Side = "colonist", Kind = "Colonist", X = home.X, Z = home.Z - 1, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
            new RealPawn { Id = "col1", Side = "colonist", Kind = "Colonist", X = home.X, Z = home.Z + 1, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
            new RealPawn { Id = "host", Side = "hostile", Kind = "Pirate", X = 45, Z = 30, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
        ],
    };
}

// --drugs: each colonist carries go-juice; --high makes the raiders high on it and --raiders N adds raiders, either of
// which is a fight worth dosing for.
if (args.Contains("--drugs"))
{
    var extra = args.Contains("--raiders") ? int.Parse(args[Array.IndexOf(args, "--raiders") + 1]) - 1 : 0;
    var raiders = Enumerable.Range(0, extra).Select(i => request.Pawns.Last(p => p.Side == "hostile") with { Id = "host" + (i + 1), Z = 28 + (i * 2) });
    request = request with
    {
        Pawns = [.. request.Pawns.Concat(raiders).Select(p => p.Side == "colonist" ? p with { Carried = ["GoJuice"] } : args.Contains("--high") ? p with { Hediffs = ["GoJuiceHigh"] } : p)],
    };
}

// --armor: both sides in flak vest and helmet. --melee: the raiders carry swords, not rifles. --cover: sandbags in front of
// the colonists on open ground (the compound's own come from the planner).
if (args.Contains("--armor"))
{
    RealWear[] gear = [new("Apparel_FlakVest", ""), new("Apparel_AdvancedHelmet", "")];
    request = request with { Pawns = [.. request.Pawns.Select(p => p with { Worn = gear })] };
}

if (args.Contains("--melee"))
{
    request = request with { Pawns = [.. request.Pawns.Select(p => p.Side == "hostile" ? p with { Weapon = "MeleeWeapon_LongSword", WeaponStuff = "Steel", Skills = new Dictionary<string, int> { ["Melee"] = 8 } } : p)] };
}

// --psycast: the first colonist knows Stun (Royalty), and the raiders stand 18 cells off, inside the 20 cells the policy casts at.
if (args.Contains("--psycast"))
{
    var caster = true;
    request = request with { Pawns = [.. request.Pawns.Select(p => p.Side == "colonist" && caster ? CastFirst(p) : p.Side == "hostile" ? p with { X = 38 } : p)] };
    RealPawn CastFirst(RealPawn p) { caster = false; return p with { Psycasts = ["Stun"] }; }
}

// --permit: the first colonist is an Empire acolyte with the trooper squad permit and favor for it, against four raiders,
// which outmatch the two colonists so the policy calls the aid.
if (args.Contains("--permit"))
{
    var host = request.Pawns.Last(p => p.Side == "hostile");
    var raiders = Enumerable.Range(1, 3).Select(i => host with { Id = "host" + i, Z = 30 + (i * 2) });
    var holder = true;
    request = request with { Pawns = [.. request.Pawns.Concat(raiders).Select(p => p.Side == "colonist" && holder ? Holder(p) : p)] };
    RealPawn Holder(RealPawn p) { holder = false; return args.Contains("--no-aid") ? p : p with { Title = "Acolyte", Favor = 10, Permits = ["CallMilitaryAidSmall"] }; }
}

if (args.Contains("--cover"))
{
    request = request with { Things = [.. Enumerable.Range(28, 5).Select(z => new RealThing("Sandbags", "", 23, z))] };
}

// --burn (with --policy --open): an infestation. A hive at 44,30 under rock with the burn-out room the policy plans built around
// it (BurnStage), three megascarabs in it (off the corridor axis, out of sight of the corridor; the hive spawns none of its own), and three colonists west of the corridor, the first carrying a molotov.
BurnStage? burn = null;
if (args.Contains("--burn"))
{
    burn = new BurnStage(new RimGovernor.Brain.Domain.Cell(44, 30), new RimGovernor.Brain.Domain.Cell(-1, 0));
    request = request with
    {
        HostileFaction = "Insect",
        MaxTicks = 20000,
        Pawns =
        [
            new RealPawn { Id = "col0", Side = "colonist", Kind = "Colonist", X = 35, Z = 29, Weapon = "Weapon_GrenadeMolotov", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
            new RealPawn { Id = "col1", Side = "colonist", Kind = "Colonist", X = 35, Z = 30, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
            new RealPawn { Id = "col2", Side = "colonist", Kind = "Colonist", X = 35, Z = 31, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
            new RealPawn { Id = "bug0", Side = "hostile", Kind = "Megascarab", X = 41, Z = 28 },
            new RealPawn { Id = "bug1", Side = "hostile", Kind = "Megascarab", X = 41, Z = 32 },
            new RealPawn { Id = "bug2", Side = "hostile", Kind = "Megascarab", X = 42, Z = 27 },
        ],
    };
}

GameHost.Boot(game);
var controller = new SeatController();
var live = new PolicyController { Log = args.Contains("--trace") ? Console.Error.WriteLine : null };
if (policy && !open)
{
    var range = Verse.DefDatabase<Verse.ThingDef>.GetNamed("Gun_BoltActionRifle").Verbs[0].range;
    stage = new DefenseStage(RimGovernor.Brain.Domain.Rotation.West, 2, range) { Traps = !args.Contains("--no-traps") };
}

var arena = new Arena(request, burn is not null ? burn.Raise : stage is null ? null : stage.Raise) { Controller = bare ? null : policy ? live : controller };
if (stage is not null)
{
    live.Layout = stage.Layout;
    Console.Error.WriteLine($"stage: layout {(stage.Layout.IsKnown ? "held" : "none")} refusal={(stage.Failure.Length == 0 ? "none" : stage.Failure)} home={stage.Home.X},{stage.Home.Z} bed={stage.BedCell?.X},{stage.BedCell?.Z} firing={string.Join(" ", stage.Planned?.Firing.Select(f => f.Cell.X + "," + f.Cell.Z) ?? [])} tiers={stage.Planned?.Tiers.Count}");
}
var result = arena.Fight(1);
Console.Error.WriteLine($"winner={result.Winner} ticks={result.Ticks} {(bare ? "(bare)" : policy ? "policy " + live.Summary : controller.Summary)}");
foreach (var p in result.Pawns)
{
    Console.Error.WriteLine($"  {p.Id} shots={p.Shots} hitsTaken={p.HitsTaken} dead={p.Dead} downed={p.Downed}");
}

if (policy)
{
    return live.Decisions > 0 && live.Applied > 0 && live.Failure.Length == 0 ? 0 : 1;
}

return bare || controller.Applied == 2 && controller.Refused == 0 && controller.Events > 0 ? 0 : 1;

internal sealed class SeatController : IFightController
{
    private NativeSeat? _seat;
    private bool _drafted;
    private string? _target;

    internal int Captures { get; private set; }

    internal int Events { get; private set; }

    internal int Applied { get; private set; }

    internal int Refused { get; private set; }

    internal string Summary => $"captures={Captures} events={Events} applied={Applied} refused={Refused} hookErrors={_seat?.HookErrors.Count}";

    public void Begin(Map map) => _seat = NativeSeat.Open(map);

    public void Decide(Map map, int tick)
    {
        if (tick % 15 != 0)
        {
            return;
        }

        var frame = _seat!.Capture();
        Captures++;
        Events = Math.Max(Events, frame.CombatEvents.Count);
        var me = frame.CombatPawns.FirstOrDefault(p => p.Side == Mirror.CombatSide.Colonist);
        var foe = frame.CombatPawns.FirstOrDefault(p => p.Side == Mirror.CombatSide.Hostile && p.Downed != true);
        if (me is null || foe is null)
        {
            return;
        }

        if (!_drafted)
        {
            Record(_seat.Apply(Orders(me.Id!, new Operations.CombatOrderOrder.DraftCase(new Operations.Clear()))));
            _drafted = true;
        }

        if (_target != foe.Id)
        {
            _target = foe.Id;
            Record(_seat.Apply(Orders(me.Id!, new Operations.CombatOrderOrder.AttackCase(new Operations.EntityPrecondition { EntityId = foe.Id }))));
        }
    }

    private void Record(IReadOnlyList<RimGovernor.Contracts.Wire.Receipts.CombatOrderResult> results)
    {
        foreach (var r in results)
        {
            if (r.Applied == true)
            {
                Applied++;
            }
            else
            {
                Refused++;
                Console.Error.WriteLine($"refused: {r.Refusal}");
            }
        }
    }

    private static Operations.CombatOrders Orders(string pawn, Operations.CombatOrderOrder order) => new()
    {
        Orders = [new Operations.CombatOrder { Pawn = new Operations.EntityPrecondition { EntityId = pawn }, Order = order }],
    };
}
