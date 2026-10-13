using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using PawnId = RimGovernor.Brain.Domain.PawnId;
using RimGovernor.Brain.Simulation.CombatMetrics;
using PolicyWeaponDef = RimGovernor.Brain.Policy.WeaponDef;

namespace RimGovernor.Brain.Simulation.Combat;

/// <summary>
/// A <see cref="Scenario"/> plus what the policy reads about its pawns that the simulator does not model. The
/// adapter builds the live parts of the view (cells, downed and dead, stance, target, weapon, health) from sim
/// state; everything here is the caller's, read from the native catalog and frame, and anything left out
/// reaches the policy as an unknown fact, never a default.
/// </summary>
public sealed record Fight
{
    /// <summary>Labels the Metrics row.</summary>
    public string Name { get; init; } = "";

    public Scenario Scenario { get; init; } = new();

    /// <summary>The colonist combatants the fight's plan owns: the only pawns an order may name. Null means every colonist combatant.</summary>
    public IReadOnlyList<PawnId>? Orderable { get; init; }

    /// <summary>The stored defense layout the view holds, unknown without one.</summary>
    public Fact<CombatLayout> Layout { get; init; }

    /// <summary>Per-pawn static facts by pawn id.</summary>
    public IReadOnlyDictionary<PawnId, PawnFacts> Facts { get; init; } = new Dictionary<PawnId, PawnFacts>();

    /// <summary>
    /// When set, drives the hostile ranged pawns with the ported raider decisions (EnemyAi); the runner builds
    /// a fresh <see cref="EnemyAi"/> from it for each run, so the Fight can be reused across seeds.
    /// </summary>
    public EnemyConfig? Raid { get; init; }

    /// <summary>When set, also gives orders each Step from the sim state, for scripted opponents. The adapter itself never decides for a hostile.</summary>
    public Func<SimState, IReadOnlyList<Order>>? Scripted { get; init; }

    /// <summary>
    /// When set, receives the events of every Step in order, before the next one overwrites them (a calibration
    /// read of shots, hits and arrivals; the list aliases the sim's buffer).
    /// </summary>
    public Action<IReadOnlyList<SimEvent>>? Trace { get; init; }

    /// <summary>
    /// How many ticks pass without a stop before the policy is asked again, the native tick budget's role. Zero
    /// means 300 (five game seconds), a simulator parameter and not a native value.
    /// </summary>
    public int BackstopTicks { get; init; }

    /// <summary>Ends an unresolved fight; it must be positive for the runner.</summary>
    public int MaxTicks { get; init; }
}

/// <summary>The view facts about one pawn that the simulator neither holds nor derives. A default Fact is unknown.</summary>
public sealed record PawnFacts
{
    /// <summary>The pawn kind def.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The pawn's place in FrontLine's split.</summary>
    public bool FrontLine { get; init; }

    /// <summary>The worn sharp armor rating.</summary>
    public Fact<double> Armor { get; init; }

    /// <summary>The MeleeDPS stat scaled by health.</summary>
    public Fact<double> MeleePower { get; init; }

    /// <summary>The hostile's lord evidence, which the hold-the-line gate needs to know how the raid arrives.</summary>
    public Fact<string> LordJobClass { get; init; }

    /// <summary>The hostile's lord toil evidence, as <see cref="LordJobClass"/>.</summary>
    public Fact<string> LordToilClass { get; init; }
}

/// <summary>
/// The strings of the bridge's combat order refusals the policy's memory reacts to (bridge.CombatRefusal*);
/// this package does not reference the bridge.
/// </summary>
public static class OrderRefusal
{
    public const string NotDrafted = "not_drafted";
    public const string NotFound = "not_found";
    public const string Unreachable = "unreachable";
    public const string CannotHit = "cannot_hit";
    public const string NativeRefused = "native_refused";
}

/// <summary>One policy order's outcome, as the native batch answers it. A refusal is empty when the order was applied.</summary>
public sealed record OrderResult(CombatOrder Order, bool Applied, string Refusal);

/// <summary>
/// Lets the unchanged policy <c>DecideCombat</c> drive a <see cref="Sim"/>: it builds the CombatView, the
/// StopEvent and the GeometryReply from sim state and applies the policy's orders to sim pawns. It reads the
/// sim's current world on every call, so geometry follows pawns that move; nothing is cached from an earlier
/// answer. An Adapter is not safe for concurrent use.
/// </summary>
public sealed partial class Adapter
{
    // The combat stop kind the native range scan raises.
    private static readonly CombatStopKind s_enteredRange = new("entered_range");

    // The vanilla job a hold_position order starts.
    private const string HoldJob = "Wait_Combat";

    private readonly Fight _f;
    private readonly Sim _sim;
    private readonly HashSet<PawnId> _orderable = [];
    private readonly HashSet<PawnId> _hold = []; // hold_position: Wait_Combat in place
    private readonly HashSet<PawnId> _holdFire = []; // fire mode Hold
    private readonly HashSet<PawnId> _inRange = []; // hostiles that reached a colonist
    private readonly HashSet<PawnId> _inOurs = []; // hostiles a colonist reached
    private bool _scanned;

    // Marks the pawns that could search for a target at the last Autonomy (Autonomy's edge detection, A41).
    private bool[] _waiting = [];

    // The colonists' CheckForAutoAttack target pick (A46).
    private readonly TargetPicker _picker;

    /// <summary>
    /// Wraps a Sim that has been Reset from f.Scenario. It throws <see cref="UnsupportedScenarioException"/>
    /// when the fight names pawns the scenario lacks or orders pawns that cannot fight.
    /// </summary>
    public Adapter(Fight f, Sim sim)
    {
        _f = f;
        _sim = sim;
        _picker = new TargetPicker(sim);
        sim.OnArrive = Arrived;
        foreach (var id in f.Facts.Keys)
        {
            if (sim.PawnIndex(id) < 0)
            {
                throw new UnsupportedScenarioException($"facts name pawn \"{id}\", which is not in the scenario");
            }
        }
        foreach (var p in sim.Pawns)
        {
            if (!p.Has)
            {
                continue;
            }
            var pf = FactsOf(p.Id);
            if (pf.Kind.Length == 0)
            {
                throw new UnsupportedScenarioException($"pawn \"{p.Id}\" has no kind fact");
            }
            if (!pf.MeleePower.IsKnown)
            {
                throw new UnsupportedScenarioException($"pawn \"{p.Id}\" has no melee power fact");
            }
            if (p.Side == Sides.Colonist && !pf.Armor.IsKnown)
            {
                throw new UnsupportedScenarioException($"colonist \"{p.Id}\" has no armor fact");
            }
        }
        if (f.Orderable is null)
        {
            foreach (var p in sim.Pawns)
            {
                if (p.Has && p.Side == Sides.Colonist)
                {
                    _orderable.Add(p.Id);
                }
            }
            return;
        }
        foreach (var id in f.Orderable)
        {
            var i = sim.PawnIndex(id);
            if (i < 0 || !sim.Pawns[i].Has || sim.Pawns[i].Side != Sides.Colonist)
            {
                throw new UnsupportedScenarioException($"orderable pawn \"{id}\" is not a colonist combatant");
            }
            _orderable.Add(id);
        }
    }

    private PawnFacts FactsOf(PawnId id) => _f.Facts.TryGetValue(id, out var pf) ? pf : new PawnFacts();

    private static bool Colonist(SimPawn p) => p.Has && p.Side == Sides.Colonist;

    private static Fact<double> Known(double v) => Fact.Known(v);

    /// <summary>The sim's stance as the mirror's: a burst's gaps are Stance_Cooldown in the game.</summary>
    private static CombatStance MirrorStance(SimPawn p)
    {
        if (p.Stance == Stance.Warmup)
        {
            return CombatStance.Warmup;
        }
        if (p.Stance is Stance.Burst or Stance.Cooldown)
        {
            return CombatStance.Cooldown;
        }
        return p.Moving ? CombatStance.Moving : CombatStance.Idle;
    }

    /// <summary>NativeDefenseStats.RangedDps: projectile damage times burst over one cycle of warmup, cooldown and burst gaps.</summary>
    private static double RangedDps(SimPawn p)
    {
        var w = p.Weapon;
        var cycle = (w.WarmupSeconds * p.Sh.Aim) + w.CooldownSeconds + ((double)(w.BurstShotCount - 1) * w.TicksBetweenBurstShots / 60);
        if (cycle <= 0)
        {
            return 0;
        }
        return (double)(w.DamageAmount * w.BurstShotCount) / cycle;
    }

    /// <summary>The share of the damage budget left, 1 unhurt and 0 at death (approximations.md A20).</summary>
    private static double HealthFraction(SimPawn p) => Math.Max(0, 1 - (p.Damage / p.KillAt));

    /// <summary>The fight as the policy sees it now. Pawns lists every combatant; Defenders, Threats and Positional list those still alive.</summary>
    public CombatView View()
    {
        var s = _sim;
        var pawns = new List<CombatPawnState>();
        var defenders = new List<SquadDefenderFacts>();
        var threats = new List<SquadThreatFacts>();
        var positional = new List<DefensiveThreatFacts>();
        var orderable = new List<PawnId>();
        var colonists = 0;
        foreach (var p in s.Pawns)
        {
            if (Colonist(p) && !p.Dead)
            {
                colonists++;
            }
        }
        foreach (var p in s.Pawns)
        {
            if (!p.Has)
            {
                continue;
            }
            var ps = new CombatPawnState
            {
                Id = (Policy.PawnId)p.Id,
                Cell = Fact.Known(p.Pos),
                Downed = p.Downed,
                Dead = p.Dead,
                Stance = MirrorStance(p),
                MoveSpeed = p.Speed,
                Kind = FactsOf(p.Id).Kind,
                Health = Known(HealthFraction(p)),
            };
            if (Colonist(p) && _orderable.Contains(p.Id))
            {
                ps = ps with { FireMode = _holdFire.Contains(p.Id) ? PolicyFunctions.HoldFire : PolicyFunctions.FireAtWill };
            }
            if (_hold.Contains(p.Id))
            {
                ps = ps with { Job = HoldJob };
            }
            else if (p.Target >= 0)
            {
                ps = ps with { Target = (Policy.PawnId)s.Pawns[p.Target].Id };
            }
            if (p.Armed)
            {
                var w = p.Weapon;
                ps = ps with
                {
                    Weapon = w.Name,
                    WeaponRange = w.Range,
                    WeaponFacts = new PolicyWeaponDef { Ranged = true, Range = w.Range, Reach = w.Range, DPS = RangedDps(p), AP = w.ArmorPenetration },
                };
            }
            pawns.Add(ps);
            if (p.Dead)
            {
                continue;
            }
            var facts = FactsOf(p.Id);
            if (Colonist(p))
            {
                defenders.Add(Defender(p, facts));
                if (_orderable.Contains(p.Id))
                {
                    orderable.Add(p.Id);
                }
                continue;
            }
            threats.Add(new SquadThreatFacts
            {
                Id = (Policy.PawnId)p.Id,
                Dead = Fact.Known(false),
                Downed = Fact.Known(p.Downed),
                Humanlike = Fact.Known(true),
                Animal = Fact.Known(false),
                BodySize = Known(p.BodySize),
                Manhunter = Fact.Known(false),
                Hunting = Fact.Known(false),
                RangedEquipped = Fact.Known(p.Armed),
                MeleePower = facts.MeleePower,
            });
            positional.Add(new DefensiveThreatFacts
            {
                Id = (Policy.PawnId)p.Id,
                Dead = Fact.Known(false),
                Downed = Fact.Known(p.Downed),
                Humanlike = Fact.Known(true),
                LordJobClass = facts.LordJobClass,
                LordToilClass = facts.LordToilClass,
                NearestColonistDistance = NearestColonist(p),
                Position = Fact.Known(p.Pos),
            });
        }
        orderable.Sort((x, y) => string.CompareOrdinal(x.Value, y.Value));
        return new CombatView
        {
            Tick = new Tick(s.TickCount),
            Layout = _f.Layout,
            Population = Fact.Known(colonists),
            Pawns = EquatableArray.From(pawns),
            Defenders = EquatableArray.From(defenders),
            Threats = EquatableArray.From(threats),
            Positional = EquatableArray.From(positional),
            Orderable = EquatableArray.From(orderable.Select(id => (Policy.PawnId)id)),
        };
    }

    private SquadDefenderFacts Defender(SimPawn p, PawnFacts facts) => new()
    {
        Id = (Policy.PawnId)p.Id,
        Dead = Fact.Known(false),
        Downed = Fact.Known(p.Downed),
        Drafted = Fact.Known(_orderable.Contains(p.Id)),
        MentalState = Fact.Known(false),
        PlayerForced = Fact.Known(false),
        DraftOwned = Fact.Known(false),
        QueuedJobs = Fact.Known(0u),
        ViolenceCapable = Fact.Known(true),
        NeedsTend = Fact.Known(false),
        HealthFraction = Known(HealthFraction(p)),
        RangedEquipped = Fact.Known(p.Armed),
        MeleeEquipped = Fact.Known(false),
        Armed = Fact.Known(p.Armed),
        FrontLine = facts.FrontLine,
        Armor = facts.Armor,
        MeleePower = facts.MeleePower,
        RangedDPS = Known(p.Armed ? RangedDps(p) : 0),
    };

    /// <summary>The Chebyshev distance to the nearest living colonist combatant (NativePawnObservationTools.Distance); unknown with none.</summary>
    private Fact<double> NearestColonist(SimPawn from)
    {
        var best = -1;
        foreach (var p in _sim.Pawns)
        {
            if (!Colonist(p) || p.Dead)
            {
                continue;
            }
            var d = Math.Max(Grid.Abs32(p.Pos.X - from.Pos.X), Grid.Abs32(p.Pos.Z - from.Pos.Z));
            if (best < 0 || d < best)
            {
                best = d;
            }
        }
        return best < 0 ? Fact.Unknown<double>() : Known(best);
    }

    /// <summary>
    /// The stop the events of a Step raise, or null. A colonist downed or killed raises "downed" (a hit on a
    /// colonist that is a serious hit raises "serious_injury" unless the same step also downs one); the first
    /// hostile to come within reach of a colonist, or a colonist's weapon to reach it, raises "entered_range"
    /// (once per hostile each way, the first scan only recording the state at the start). Other native stop
    /// kinds are not raised: there are no shields, lords, melee, explosives or mental states in the sim, and
    /// the vital-part serious trigger cannot fire for bullets (approximations.md A22).
    /// </summary>
    public StopEvent? Stop(IReadOnlyList<SimEvent> events)
    {
        StopEvent? stop = null;
        StopEvent? serious = null;
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case EventKind.Hit:
                    // Pawn_HealthTracker.PostApplyDamage: a serious injury to a colonist (A22).
                    var i = _sim.PawnIndex(e.Target);
                    if (serious is null && i >= 0 && Colonist(_sim.Pawns[i]) && SeriousHit(e))
                    {
                        serious = new StopEvent { Kind = CombatStopKind.SeriousInjury, Pawn = (Policy.PawnId)e.Target };
                    }
                    break;
                case EventKind.Downed:
                case EventKind.Died:
                    var j = _sim.PawnIndex(e.Pawn);
                    if (j >= 0 && Colonist(_sim.Pawns[j]))
                    {
                        stop = new StopEvent { Kind = CombatStopKind.Downed, Pawn = (Policy.PawnId)e.Pawn };
                    }
                    break;
                default:
                    break;
            }
        }
        // A hit that downs the pawn notes the downing first (the game's CheckForStateChange runs inside
        // PostApplyDamage, before the serious check), and the first event since the tick boundary wins.
        stop ??= serious;
        var hostile = ScanRange();
        if (hostile is { } h && stop is null)
        {
            return new StopEvent { Kind = s_enteredRange, Pawn = (Policy.PawnId)h };
        }
        return stop;
    }

    /// <summary>
    /// SupervisedPlayCombatStops.SeriousWhy for a hit on a colonist: one blow of at least
    /// SeriousSingleHitDamage, summary health crossing from above SeriousSummaryHealthFloor to at or under it,
    /// or the total bleed rate crossing from under SeriousBleedRateFloor to at or over it. The vital-part
    /// trigger needs DamageInfo.HitPart, which a projectile's DamageInfo leaves null (Bullet.Impact passes no
    /// part and Apply works on a copy), so it never fires for the sim's ranged hits.
    /// </summary>
    internal static bool SeriousHit(SimEvent e)
    {
        var health = (float)e.Health;
        var floor = PolicyFunctions.SeriousSummaryHealthFloor;
        var bleed = (float)e.Bleed;
        var bleedFloor = PolicyFunctions.SeriousBleedRateFloor;
        return e.Amount >= PolicyFunctions.SeriousSingleHitDamage ||
            ((float)e.HealthBefore > floor && health <= floor) ||
            ((float)e.BleedBefore < bleedFloor && bleed >= bleedFloor);
    }

    /// <summary>SupervisedPlayCombatStops.ScanCombatContact's range half for ranged pawns: distance between cells against the weapon range, no sight.</summary>
    internal PawnId? ScanRange()
    {
        var s = _sim;
        var baseline = !_scanned;
        _scanned = true;
        PawnId? first = null;

        static bool Reach(SimPawn x, SimPawn y)
        {
            if (!x.Armed)
            {
                return false;
            }
            double dx = x.Pos.X - y.Pos.X, dz = x.Pos.Z - y.Pos.Z;
            return (dx * dx) + (dz * dz) <= x.Weapon.Range * x.Weapon.Range;
        }

        foreach (var h in s.Pawns)
        {
            if (!h.Has || h.Side != Sides.Hostile || h.Dead || h.Downed)
            {
                continue;
            }
            for (var mark = 0; mark < 2; mark++)
            {
                var seen = mark == 0 ? _inRange : _inOurs;
                if (seen.Contains(h.Id))
                {
                    continue;
                }
                foreach (var c in s.Pawns)
                {
                    if (!Colonist(c) || c.Dead || c.Downed)
                    {
                        continue;
                    }
                    if (mark == 0 ? Reach(h, c) : Reach(c, h))
                    {
                        seen.Add(h.Id);
                        if (!baseline && first is null)
                        {
                            first = h.Id;
                        }
                        break;
                    }
                }
            }
        }
        return first;
    }

    /// <summary>
    /// Validates the policy's orders the way the native batch does and returns each one's result plus the sim
    /// orders to apply at the next Step. A refused order changes nothing. An order kind the ranged slice does
    /// not execute (rescue, repair, doors, mortars, drugs, permits, casts, animals) is refused native_refused
    /// rather than dropped.
    /// </summary>
    public (List<OrderResult> Results, List<Order> Orders) Apply(IReadOnlyList<CombatOrder> orders)
    {
        var results = new List<OrderResult>(orders.Count);
        var output = new List<Order>();
        foreach (var o in orders)
        {
            var (sim, refusal) = ApplyOne(o);
            results.Add(new OrderResult(o, refusal.Length == 0, refusal));
            if (refusal.Length == 0 && sim is not null)
            {
                output.Add(sim);
            }
        }
        return (results, output);
    }

    private (Order? Order, string Refusal) ApplyOne(CombatOrder o)
    {
        var s = _sim;
        var i = s.PawnIndex((PawnId)o.Pawn);
        if (i < 0 || s.Pawns[i].Dead)
        {
            return (null, OrderRefusal.NotFound);
        }
        if (!_orderable.Contains((PawnId)o.Pawn))
        {
            return (null, OrderRefusal.NotDrafted);
        }
        var p = s.Pawns[i];
        if (p.Downed)
        {
            return (null, OrderRefusal.NotDrafted); // a downed pawn is undrafted
        }
        switch (o.Kind)
        {
            case CombatOrderKind.Move:
                if (!s.G.Standable(o.Cell))
                {
                    return (null, OrderRefusal.Unreachable);
                }
                if (o.Cell != p.Pos && s.FindPath(p, o.Cell) is null)
                {
                    return (null, OrderRefusal.Unreachable);
                }
                _hold.Remove((PawnId)o.Pawn);
                return (new Order { Pawn = (PawnId)o.Pawn, Kind = OrderKind.Move, To = o.Cell }, "");
            case CombatOrderKind.Stop:
                _hold.Remove((PawnId)o.Pawn);
                return (new Order { Pawn = (PawnId)o.Pawn, Kind = OrderKind.Stop }, "");
            case CombatOrderKind.HoldPosition:
                _hold.Add((PawnId)o.Pawn);
                return (new Order { Pawn = (PawnId)o.Pawn, Kind = OrderKind.Stop }, "");
            case CombatOrderKind.FireMode:
                if (o.FireMode == PolicyFunctions.HoldFire)
                {
                    _holdFire.Add((PawnId)o.Pawn);
                }
                else
                {
                    _holdFire.Remove((PawnId)o.Pawn);
                }
                return (null, "");
            case CombatOrderKind.Attack:
                var ti = s.PawnIndex((PawnId)o.Target);
                if (ti < 0 || s.Pawns[ti].Dead)
                {
                    return (null, OrderRefusal.NotFound);
                }
                if (!p.Armed || !s.Pawns[ti].Has || s.Pawns[ti].Side == p.Side)
                {
                    return (null, OrderRefusal.CannotHit);
                }
                if (!s.Pawns[ti].Downed && !s.CanHitFrom(i, ti))
                {
                    return (null, OrderRefusal.CannotHit);
                }
                _hold.Remove((PawnId)o.Pawn);
                return (new Order { Pawn = (PawnId)o.Pawn, Kind = OrderKind.Attack, Target = (PawnId)o.Target }, "");
            default:
                return (null, OrderRefusal.NativeRefused);
        }
    }

    /// <summary>
    /// The attack orders the fire-at-will holders give themselves: Wait_Combat's auto-attack, which starts a
    /// shot at a hostile in reach of the pawn's cell whenever it has no target and is not on hold fire. The
    /// target is the game's scored random pick among the hostiles in range with a line of fire
    /// (approximations.md A21, A46).
    /// </summary>
    public List<Order> Autonomy()
    {
        var output = new List<Order>();
        if (_waiting.Length != _sim.Pawns.Count)
        {
            _waiting = new bool[_sim.Pawns.Count];
        }
        for (var i = 0; i < _sim.Pawns.Count; i++)
        {
            // CheckForAutoAttack runs when Wait_Combat starts, when the stance changes (a cooldown ends) and on
            // the pawn's own 15-tick job tracker tick (A41): the search is due when the pawn became free to
            // search since the last tick, or on its tick.
            var free = CanSearch(i);
            var due = free && (!_waiting[i] || _sim.Pawns[i].CallTick(_sim.TickCount));
            _waiting[i] = free;
            if (!due)
            {
                continue;
            }
            if (AutoAttack(i) is { } o)
            {
                output.Add(o);
            }
        }
        return output;
    }

    /// <summary>Whether pawn i is a fire-at-will holder with nothing to do: no walk, no attack order, no burst under way.</summary>
    private bool CanSearch(int i)
    {
        var p = _sim.Pawns[i];
        return _orderable.Contains(p.Id) && !_holdFire.Contains(p.Id) && p.Armed && !p.Dead && !p.Downed && !p.Moving && p.Target < 0 && p.Stance == Stance.Idle;
    }

    /// <summary>The search itself for pawn i; null when it finds nothing to shoot.</summary>
    private Order? AutoAttack(int i)
    {
        var s = _sim;
        var p = s.Pawns[i];
        if (!CanSearch(i))
        {
            return null;
        }
        // BestShootTargetFromCurrentPosition: the scored random pick over the hostiles in weapon range with a
        // line of sight (A46).
        var cands = _picker.Valid(i, p.Weapon.Range, p.Weapon.MinRange, false);
        var best = _picker.PickByScore(i, cands, s.RandomStreams.Colonist);
        if (best < 0)
        {
            return null;
        }
        return new Order { Pawn = p.Id, Kind = OrderKind.Attack, Target = s.Pawns[best].Id, Auto = true };
    }

    /// <summary>
    /// The pather tick's end of a walk: the job ends and Wait_Combat starts on the same tick, so its
    /// auto-attack warmup (started before the stance tick) counts down that tick too (A36).
    /// </summary>
    private void Arrived(int i)
    {
        if (AutoAttack(i) is { } o)
        {
            _sim.StartAttack(i, o);
        }
    }

    /// <summary>
    /// Folds the results of a stop's orders into the fight's memory the way the planner does
    /// (combatStopRecord): a refused attack that cannot hit is remembered from the shooter's cell, an
    /// unreachable move marks its cell, any other refusal makes the policy forget the pawn's order.
    /// </summary>
    public CombatMemory Remember(CombatMemory m, IReadOnlyList<OrderResult> results)
    {
        foreach (var r in results)
        {
            var o = r.Order;
            if (r.Applied || o.Pawn == default)
            {
                continue;
            }
            var i = _sim.PawnIndex((PawnId)o.Pawn);
            if (i >= 0 && r.Refusal == OrderRefusal.CannotHit)
            {
                m = m.RefuseHit(o, _sim.Pawns[i].Pos);
            }
            else if (o.Kind == CombatOrderKind.Move && r.Refusal == OrderRefusal.Unreachable)
            {
                m = m.RefuseCell(o.Cell).Forget(o.Pawn);
            }
            else
            {
                m = m.Forget(o.Pawn);
            }
        }
        return m;
    }

    /// <summary>
    /// Runs DecideCombat for the stop with the stop's one geometry round trip, as RoundsDefensePlanner does: a
    /// geometry ask is answered from the sim's world and the policy decides again from the memory it was given.
    /// </summary>
    public (List<CombatOrder> Orders, CombatMemory Memory) Decide(StopEvent stop, CombatMemory memory)
    {
        var view = View();
        var (orders, ask, next) = PolicyFunctions.DecideCombat(view, new GeometryReply(), stop, memory);
        if (ask is not null)
        {
            (orders, _, next) = PolicyFunctions.DecideCombat(view, Geometry(ask), stop, memory);
        }
        return (orders, next);
    }
}
