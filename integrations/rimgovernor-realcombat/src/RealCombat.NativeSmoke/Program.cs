using RimGovernor.RealCombat;
using RimGovernor.RealCombat.Native;
using Verse;
using Mirror = RimGovernor.Contracts.Wire.Mirror;
using Operations = RimGovernor.Contracts.Wire.Operations;

// One duel in which the colonist is observed through the mod's combat mirror and commanded through its combat order
// handler (the raider stands still: nothing drives it yet). With --bare the built-in retargeting plays both sides.
// Exit 0 when the seat observed events and applied the draft and the attack.
//   RimGovernor.RealCombat.NativeSmoke --game <dir> [--bare]
var game = args[Array.IndexOf(args, "--game") + 1];
var bare = args.Contains("--bare");
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
GameHost.Boot(game);
var controller = new SeatController();
var arena = new Arena(request) { Controller = bare ? null : controller };
var result = arena.Fight(1);
Console.Error.WriteLine($"winner={result.Winner} ticks={result.Ticks} {(bare ? "(bare)" : controller.Summary)}");
foreach (var p in result.Pawns)
{
    Console.Error.WriteLine($"  {p.Id} shots={p.Shots} hitsTaken={p.HitsTaken} dead={p.Dead} downed={p.Downed}");
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
