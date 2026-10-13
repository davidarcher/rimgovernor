namespace RimGovernor.Brain.Policy;

// Go: combat_decide.go
//
// Port note: CombatMemory, CombatRole and GeometryRequest are mutable classes/records here. DecideCombat threads
// one working memory through dozens of in-place planners (Go passes *CombatMemory and mutates slice elements),
// so the port keeps those mutations one-to-one and restores Go's value semantics with Copy() at the boundary
// (memory in, memory out). Everything the planners only read (CombatView and its rows) is an immutable record.

/// <summary>A combatant's stance (mirror CombatStance).</summary>
public enum CombatStance
{
    [WireName("")]
    Unknown,

    [WireName("idle")]
    Idle,

    [WireName("warmup")]
    Warmup,

    [WireName("cooldown")]
    Cooldown,

    [WireName("moving")]
    Moving,

    [WireName("melee")]
    Melee,
}

/// <summary>
/// One combatant's live state at the stop (the combat geometry SECTION_COMBAT_PAWNS row). Unknown fields stay
/// default; a default stance never guards an order and a default cell never proves arrival.
/// </summary>
public sealed partial record CombatPawnState
{
    public PawnId Id { get; init; }

    public Fact<Cell> Cell { get; init; }

    public bool Downed { get; init; }

    public bool Dead { get; init; }

    public PawnId Target { get; init; }

    public CombatStance Stance { get; init; }

    /// <summary>FireAtWill or HoldFire for a drafted colonist, else empty.</summary>
    public string FireMode { get; init; } = "";

    /// <summary>The equipped weapon def (empty unknown).</summary>
    public string Weapon { get; init; } = "";

    /// <summary>The equipped weapon's range (0 unknown).</summary>
    public double WeaponRange { get; init; }

    /// <summary>The weapon def's rows: its blast, EMP, fire and rocket facts.</summary>
    public WeaponDef WeaponFacts { get; init; } = new();

    /// <summary>The pawn kind def.</summary>
    public string Kind { get; init; } = "";

    /// <summary>A sapper or breacher at work.</summary>
    public bool Sapper { get; init; }

    /// <summary>The current job def.</summary>
    public string Job { get; init; } = "";

    /// <summary>The native fact that the job target is a mortar, or a frame that will build one.</summary>
    public bool TargetMortar { get; init; }

    public bool ShieldBelt { get; init; }

    public int MedicalSkill { get; init; }

    /// <summary>The worn shield's charge, a fraction of max; unknown without a shield.</summary>
    public Fact<double> Shield { get; init; }

    /// <summary>The pawn's MoveSpeed stat in cells/s, 0 unknown.</summary>
    public double MoveSpeed { get; init; }

    public bool GoJuice { get; init; }

    public bool Luciferium { get; init; }

    /// <summary>The complete observed inventory drug definition set. Unknown inventory cannot authorize a dose.</summary>
    public Fact<EquatableArray<string>> CarriedDrugs { get; init; }

    /// <summary>A colony animal (mirror side colony_animal).</summary>
    public bool Animal { get; init; }

    public Fact<double> BloodLoss { get; init; }

    public Fact<double> BleedRatePerDay { get; init; }

    public Fact<double> HoursUntilBleedDeath { get; init; }

    /// <summary>A prison-breaking prisoner (COMBAT_SIDE_PRISONER).</summary>
    public bool Prisoner { get; init; }

    /// <summary>The summary health fraction.</summary>
    public Fact<double> Health { get; init; }

    /// <summary>A wild animal near hostiles (COMBAT_SIDE_WILD_ANIMAL).</summary>
    public bool Wild { get; init; }

    /// <summary>A stunned pawn's ticks left, 0 awake.</summary>
    public int StunTicks { get; init; }

    /// <summary>The game's own flag of the pawn's race (RaceProperties.IsMechanoid).</summary>
    public bool Mech { get; init; }

    /// <summary>The game's own flag of the pawn's race (RaceProperties.Insect).</summary>
    public bool Insect { get; init; }
}

/// <summary>The stored, complete defense layout's line.</summary>
public sealed record CombatLayout
{
    public EquatableArray<Cell> Firing { get; init; }

    /// <summary>The inner line: Retreat[i] is Firing[i]'s fall-back cell, one step further toward Home. Empty on a record that predates it.</summary>
    public EquatableArray<Cell> Retreat { get; init; }

    public Rotation Toward { get; init; }

    /// <summary>The corridor's exit cell on our side, where blockers hold; unknown without a corridor.</summary>
    public Fact<Cell> Choke { get; init; }
}

/// <summary>
/// The fight at one stop. Defenders, Threats and Positional are Formation's inputs, the facts the one-shot
/// planner chose from; Pawns is the live state Reaction compares against; Orderable are the defenders whose
/// drafts the fight's plan owns (the caller's per-plan owned-draft check), the only pawns an order may name.
/// </summary>
public sealed partial record CombatView
{
    public Fact<CombatSiteExit> SiteExit { get; init; }

    public Tick Tick { get; init; }

    public EquatableArray<CombatPawnState> Pawns { get; init; }

    public EquatableArray<SquadDefenderFacts> Defenders { get; init; }

    public EquatableArray<SquadThreatFacts> Threats { get; init; }

    public EquatableArray<DefensiveThreatFacts> Positional { get; init; }

    public Fact<CombatLayout> Layout { get; init; }

    public EquatableArray<PawnId> Orderable { get; init; }

    /// <summary>The frame's drop-pod arrival.</summary>
    public Fact<PodArrival> Pods { get; init; }

    /// <summary>The map's standing rectangular rooms from the frame.</summary>
    public EquatableArray<CombatRoom> Rooms { get; init; }

    public Fact<EquatableArray<RoomDoor>> DoorStates { get; init; }

    /// <summary>The frame's player doors below max hit points.</summary>
    public EquatableArray<Cell> DamagedDoors { get; init; }

    /// <summary>The colony's unroofed mortars.</summary>
    public EquatableArray<CombatMortar> Mortars { get; init; }

    /// <summary>The census's standing hostile buildings.</summary>
    public EquatableArray<HostileStructure> Structures { get; init; }

    /// <summary>The load's mortar shell defs by kind.</summary>
    public MortarShells Shells { get; init; } = new();

    /// <summary>The colony's colonist count; below the population target the fight spares contained bleeders.</summary>
    public Fact<int> Population { get; init; }

    /// <summary>The map outdoor temperature in degrees Celsius from the frame; extreme cold or heat waits indoors.</summary>
    public Fact<double> OutdoorTemperatureC { get; init; }

    /// <summary>The hottest live hive's temperature in degrees Celsius from the frame: the heat-stroke hold and entry check.</summary>
    public Fact<double> HiveTemperatureC { get; init; }

    /// <summary>A burn-out's census from the combat step while it is active: what it lacks, its roof and its fuel.</summary>
    public Fact<BurnSite> Burn { get; init; }

    /// <summary>Marks a squad hunt: Threats are wild prey, not raiders.</summary>
    public bool Hunt { get; init; }

    /// <summary>The slow-refresh royalty read; unknown holds every permit call.</summary>
    public Fact<RoyaltyFacts> Royalty { get; init; }

    /// <summary>The catalog's combat drug definitions in preference order. Selection is restricted to each pawn's observed carried inventory.</summary>
    public EquatableArray<string> Drugs { get; init; }

    /// <summary>Whether neither dead, downed nor deathresting: every defender in the live state or the formation facts.</summary>
    internal Dictionary<PawnId, bool> Live()
    {
        var output = new Dictionary<PawnId, bool>();
        foreach (var d in Defenders.Items)
        {
            output[d.Id] = !Positive(d.Dead) && !Positive(d.Downed) && !d.Deathresting;
        }

        foreach (var p in Pawns.Items)
        {
            if (p.Dead || p.Downed)
            {
                output[p.Id] = false;
            }
        }

        return output;
    }

    /// <summary>
    /// The view with every list in id order: the selectors take the first eligible in input order, so input
    /// order must not matter.
    /// </summary>
    internal CombatView Sorted() =>
        this with
        {
            Pawns = EquatableArray.From(PolicySort.ByLess(Pawns.Items, (a, b) => a.Id < b.Id)),
            Defenders = EquatableArray.From(PolicySort.ByLess(Defenders.Items, (a, b) => a.Id < b.Id)),
            Threats = EquatableArray.From(PolicySort.ByLess(Threats.Items, (a, b) => ThreatPawn(a) < ThreatPawn(b))),
            Positional = EquatableArray.From(PolicySort.ByLess(Positional.Items, (a, b) => a.Id < b.Id)),
            Orderable = EquatableArray.From(Orderable.Items.OrderBy(x => x)),
        };

    /// <summary>
    /// Drops the spared hostiles from the view's threats, so no tactic assigns or retargets an attacker onto
    /// them. Their positions remain safety facts: a new fight still needs a shelter roster while waiting for
    /// downing.
    /// </summary>
    internal CombatView Spare(HashSet<PawnId> spared) =>
        spared.Count == 0 ? this : this with { Threats = EquatableArray.From(Threats.Items.Where(t => !spared.Contains(ThreatPawn(t)))) };
}

/// <summary>The combat event that stopped the clock, lower-cased (downed, raid_phase, ...); empty for the first decision or the backstop.</summary>
public readonly partial record struct CombatStopKind(string Value)
{
    public string Value { get => field ?? string.Empty; } = Value;

    public static readonly CombatStopKind RaidPhase = new("raid_phase");
    public static readonly CombatStopKind Breach = new("breach");
    public static readonly CombatStopKind SeriousInjury = new("serious_injury");
    public static readonly CombatStopKind MeleeContact = new("melee_contact");
    public static readonly CombatStopKind Downed = new("downed");
    public static readonly CombatStopKind ShieldBroken = new("shield_broken");

    public bool IsEmpty => Value.Length == 0;

    public override string ToString() => Value;
}

/// <summary>The stop DecideCombat answers.</summary>
public sealed record StopEvent
{
    public CombatStopKind Kind { get; init; }

    public PawnId Pawn { get; init; }

    public PawnId Target { get; init; }
}

/// <summary>
/// A candidate-cell role Formation asks the game to propose: cells with cover behind the line, cells
/// adjacent to the choke, and firing cells.
/// </summary>
public enum FormationRole
{
    [WireName("")]
    None,

    CoverBehindLine,

    AdjacentToChoke,

    FiringCells,
}

/// <summary>
/// DecideCombat's one geometry ask per stop: the game proposes candidate cells for one role, scored against
/// the named hostiles (bridge.CombatGeometryMaxHostiles at most).
/// </summary>
public sealed record GeometryRequest
{
    public FormationRole Propose { get; set; }

    /// <summary>
    /// Named cells to score: the shooters' cells, their lines of fire answered in GeometryReply.Lines, and
    /// Formation's computed cells (tank, peeler and blocking-hold cover cells), their standability answered in
    /// GeometryReply.Standable.
    /// </summary>
    public List<Cell> Cells { get; set; } = [];

    /// <summary>The cover_behind_line role's anchor.</summary>
    public List<Cell> Line { get; set; } = [];

    public List<PawnId> Hostiles { get; set; } = [];

    /// <summary>The rescue_path role's walker.</summary>
    public PawnId Pawn { get; set; }

    /// <summary>The rescue_path role's goal.</summary>
    public Cell To { get; set; }

    /// <summary>Anchors the adjacent_to_choke role.</summary>
    public Cell Choke { get; set; }

    /// <summary>Anchors the adjacent_to_choke role.</summary>
    public Cell OurSide { get; set; }

    /// <summary>Anchors the firing_cells role: a walled-in gunner's cell.</summary>
    public Cell From { get; set; }

    /// <summary>Anchors the firing_cells role: the hostiles' cells it needs a line to.</summary>
    public List<Cell> Targets { get; set; } = [];
}

/// <summary>
/// Answers a GeometryRequest with the game's proposals, best first. Answered with no proposals is a valid
/// answer (the game proposed nothing, or the read failed): Formation then keeps the layout's firing line alone.
/// </summary>
public sealed record GeometryReply
{
    public bool Answered { get; init; }

    public EquatableArray<Cell> Proposals { get; init; }

    /// <summary>The answered ask's role.</summary>
    public FormationRole Role { get; init; }

    /// <summary>A rescue_path answer.</summary>
    public EquatableArray<RouteCell> Route { get; init; }

    /// <summary>The named and proposed cells' sight lines to the ask's hostiles.</summary>
    public EquatableArray<SightLine> Lines { get; init; }

    /// <summary>The game's cover for the named and proposed cells; Formation ranks its candidate cells by it.</summary>
    public EquatableArray<ScoredCell> Scored { get; init; }

    /// <summary>The named cells the game reported standable, and every proposal. A computed cell is used only when it is here.</summary>
    public EquatableArray<Cell> Standable { get; init; }

    /// <summary>Whether the reply says the cell is standable.</summary>
    internal bool Stands(Cell c) => Standable.Items.Contains(c);
}

/// <summary>The formation a fight runs.</summary>
public enum CombatTactic
{
    [WireName("")]
    None,

    Hold,

    Squad,

    DoorChoke,

    Hunt,

    Infestation,

    Manhunter,

    Pods,

    PrisonBreak,

    Sapper,

    Shelter,

    Siege,

    SiteExit,
}

/// <summary>One defender's place in the formation: a firing cell to hold (hold-the-line) and the hostile to engage.</summary>
public sealed partial record CombatRole
{
    public PawnId Pawn { get; set; }

    public Cell? Cell { get; set; }

    public PawnId Target { get; set; }

    public bool Ranged { get; set; }

    /// <summary>A brawler's formation duty.</summary>
    public CombatDuty Duty { get; set; }

    /// <summary>Marks a role pulled back to its inner-line cell, or a hurt blocker relieved by the reserve.</summary>
    public bool Retreat { get; set; }

    /// <summary>A checked standby cell: where a peeler waits between targets, or where a tank pulls back to when its shield breaks.</summary>
    public Cell? Home { get; set; }

    /// <summary>The colony mortar the pawn crews; set with Aim, they win over Cell and Target.</summary>
    public Cell? Mortar { get; set; }

    /// <summary>The cell the crewed mortar fires at.</summary>
    public Cell? Aim { get; set; }

    /// <summary>The shell the mortar fires, empty whatever is loaded.</summary>
    public string Shell { get; set; } = "";

    /// <summary>The cell a rocket carrier fires at; set, it wins over Cell and Target.</summary>
    public Cell? Ground { get; set; }

    /// <summary>A damaged door queued at a line collapse; set, the pawn repairs it.</summary>
    public Cell? Repair { get; set; }

    /// <summary>The order the role asks of a pawn in state <paramref name="s"/>.</summary>
    internal (CombatOrder Order, bool Ok) Want(CombatPawnState s)
    {
        if (Mortar is { } mortar && Aim is { } aim)
        {
            return (new CombatOrder { Pawn = Pawn, Kind = CombatOrderKind.ManMortar, Cell = mortar, Aim = aim, Shell = Shell, Reason = CombatOrderReason.CounterBattery }, true);
        }

        if (Ground is { } ground)
        {
            return (new CombatOrder { Pawn = Pawn, Kind = CombatOrderKind.AttackGround, Cell = ground, Reason = CombatOrderReason.RocketClump }, true);
        }

        if (Cell is { } cell)
        {
            if (!s.Cell.TryGetValue(out var at) || at != cell)
            {
                var reason = Retreat ? CombatOrderReason.Retreat : CombatOrderReason.Formation;
                return (new CombatOrder { Pawn = Pawn, Kind = CombatOrderKind.Move, Cell = cell, Reason = reason }, true);
            }
        }

        if (Target == default)
        {
            return (new CombatOrder(), false);
        }

        return (new CombatOrder { Pawn = Pawn, Kind = CombatOrderKind.Attack, Target = Target, Reason = CombatOrderReason.Formation }, true);
    }
}

/// <summary>The combat.orders order an order becomes.</summary>
public enum CombatOrderKind
{
    [WireName("")]
    None,

    Move,

    Attack,

    FireMode,

    Stop,

    HoldPosition,

    Release,

    AnimalArea,

    AnimalClear,

    [WireName("psycast_cast")]
    Cast,

    Repair,

    Drug,

    ManMortar,

    MortarFire,

    [WireName("permit_call")]
    Permit,

    Rescue,

    Door,

    AttackGround,
}

/// <summary>Says why an order was given; retreat and rescue pass the aim guard.</summary>
public enum CombatOrderReason
{
    [WireName("")]
    None,

    Formation,

    Retreat,

    Rescue,

    /// <summary>A fire-mode toggle (and its stop) for a hostile in melee with our blockers.</summary>
    HoldFire,

    /// <summary>A stripped downed raider not worth capturing, finished by its stripper.</summary>
    Finish,

    Animal,

    Enrage,

    [WireName("psycast")]
    Cast,

    Repair,

    Drug,

    Flank,

    CounterBattery,

    Permit,

    PrisonFire,

    RocketClump,
}

/// <summary>One changed order.</summary>
public sealed record CombatOrder
{
    public PawnId Pawn { get; init; }

    public CombatOrderKind Kind { get; init; }

    public Cell Cell { get; init; }

    public PawnId Target { get; init; }

    /// <summary>A fire_mode order's FireAtWill or HoldFire.</summary>
    public string FireMode { get; init; } = "";

    public CombatOrderReason Reason { get; init; }

    /// <summary>A door order's mode; a door order names no pawn.</summary>
    public DoorMode Door { get; init; }

    /// <summary>
    /// A mortar_fire order's target cell; Cell is the mortar's. Its man_mortar crew order carries the same Aim
    /// and Shell, so a new aim sends the pair again; only mortar_fire puts them on the wire.
    /// </summary>
    public Cell Aim { get; init; }

    /// <summary>A mortar_fire order with no target: it clears the mortar's forced target; Aim and Shell are unset.</summary>
    public bool Clear { get; init; }

    /// <summary>A mortar order's shell def, empty whatever is loaded.</summary>
    public string Shell { get; init; } = "";

    /// <summary>Names a permit_call order's permit with <see cref="Permit"/>; Cell is its target and Pawn the holder.</summary>
    public string Faction { get; init; } = "";

    public string Permit { get; init; } = "";

    /// <summary>A psycast_cast order names the psycast in Permit and the arm it takes here: self (no target), pawn (Target) or cell (Cell).</summary>
    public PsycastTarget Arm { get; init; }

    /// <summary>A drug order's carried drug def, selected from CombatView.Drugs; native validates it and the pawn carrying it.</summary>
    public string Drug { get; init; } = "";
}

/// <summary>The last order a pawn was given and the tick it went out.</summary>
public sealed record IssuedOrder(CombatOrder CombatOrder, Tick Tick)
{
    public PawnId Pawn => CombatOrder.Pawn;
}

/// <summary>An attack native refused cannot_hit: Pawn could not hit Target from From.</summary>
public readonly record struct HitRefusal(PawnId Pawn, PawnId Target, Cell From);

/// <summary>The fight's small explicit state between stops.</summary>
public sealed partial record CombatMemory
{
    public CombatTactic Tactic { get; set; }

    public EquatableArray<CombatRole> Roles { get; set; }

    /// <summary>Why the last Formation did not hold the line, when it did not.</summary>
    public string Refusal { get; set; } = "";

    public Tick Formed { get; set; }

    /// <summary>The blockers the reserve relieved, in relief order; a re-formation ranks them last among the brawlers, so it keeps the rotation.</summary>
    public List<PawnId> Relieved { get; set; } = [];

    public List<IssuedOrder> Issued { get; set; } = [];

    public Tick Tick { get; set; }

    /// <summary>The rescue under way.</summary>
    public CombatRescue? Rescue { get; set; }

    /// <summary>The drop-pod arrival this fight answers.</summary>
    public PodArrival? Pods { get; set; }

    /// <summary>The doors the pods tactic holds open or shut.</summary>
    public List<PodDoor> PodDoors { get; set; } = [];

    /// <summary>A pods fight holding behind closed doors, outmatched.</summary>
    public bool PodWait { get; set; }

    /// <summary>A pods fight that struck when the raid fled or looted.</summary>
    public bool PodStruck { get; set; }

    /// <summary>The potshot door of a manhunter pack or a squad-defense raid.</summary>
    public PodDoor? PotshotDoor { get; set; }

    public PodDoor? ChokeDoor { get; set; }

    public List<Cell> HeldDoors { get; set; } = [];

    /// <summary>The manhunter tactic's kiter.</summary>
    public PawnId Kiter { get; set; }

    /// <summary>Whether the kiter leads the chaser past the line.</summary>
    public bool Leading { get; set; }

    /// <summary>The colonist that worked the deep drill a deep-drill infestation tunnelled up by; it evacuates.</summary>
    public PawnId Driller { get; set; }

    /// <summary>A manhunter fight or humanoid raid sheltering, outmatched, behind the doors WaitDoors closes and forbids, since WaitSince.</summary>
    public bool Wait { get; set; }

    public Tick WaitSince { get; set; }

    public EquatableArray<PodDoor> WaitDoors { get; set; }

    /// <summary>Pairs each sheltering room door with its floor cell behind it, for the layout planner's hardening.</summary>
    public EquatableArray<WaitDoor> WaitRooms { get; set; }

    /// <summary>A hold whose line collapsed: every defender runs away from the raid.</summary>
    public bool Scattered { get; set; }

    /// <summary>The damaged doors queued at the collapse, repaired once it clears.</summary>
    public List<Cell> Repairs { get; set; } = [];

    /// <summary>The most hold-the-line cells defenders stood on at once this fight, the base a collapse is judged against.</summary>
    public int LineHeld { get; set; }

    /// <summary>The wall cell a sapper formation guards.</summary>
    public Cell? SapperBreach { get; set; }

    /// <summary>A sapper fight whose gunners went out to the diggers.</summary>
    public bool Intercept { get; set; }

    /// <summary>A hold whose gunners wait on the inner line for outranging raiders to close.</summary>
    public bool MechLure { get; set; }

    /// <summary>A sapper fight whose posted brawlers rush the breach.</summary>
    public bool Rushing { get; set; }

    /// <summary>The tick a siege camp was first seen.</summary>
    public Tick SiegeCamp { get; set; }

    /// <summary>The siege tactic's mode at its last formation.</summary>
    public SiegeMode SiegeMode { get; set; }

    /// <summary>Latches the first hostile mortar frame: the sortie ends there.</summary>
    public bool SiegeMortar { get; set; }

    /// <summary>
    /// The attacks native refused cannot_hit, kept while the shooter stands on the cell it was refused from,
    /// so the fight retargets or waits instead of re-sending them every stop.
    /// </summary>
    public List<HitRefusal> CannotHit { get; set; } = [];

    /// <summary>The cells native refused a move to as unreachable: the fight re-forms around them instead of sending the same order each stop.</summary>
    public List<Cell> Unreachable { get; set; } = [];

    /// <summary>The hold's flanking detachment.</summary>
    public CombatFlank? Flank { get; set; }

    /// <summary>The colony animals' last release or zone.</summary>
    public List<AnimalOrder> Animals { get; set; } = [];

    /// <summary>The animals native refused a release as untrained.</summary>
    public List<PawnId> Untrained { get; set; } = [];

    /// <summary>The split raid's side-group squads.</summary>
    public List<CombatGroup> Groups { get; set; } = [];

    /// <summary>The shells native refused a mortar order for: none in reach, or not a shell the mortar takes.</summary>
    public List<string> NoShells { get; set; } = [];

    /// <summary>Defenders whose combat drug order was attempted this fight, including native refusals; it limits attempts, not observed ingestion.</summary>
    public List<PawnId> Dosed { get; set; } = [];

    /// <summary>The permits called this fight, "pawn/faction/permit": the royalty read is slow, so a call is not repeated.</summary>
    public EquatableArray<string> Permitted { get; set; }

    /// <summary>The psycasts cast this fight.</summary>
    public List<CastMark> Casts { get; set; } = [];

    /// <summary>The mechs seen stunned, each until its EMP adaptation ends, sorted by pawn.</summary>
    public List<EmpAdaptation> EmpAdapted { get; set; } = [];

    /// <summary>An infestation's burn-out.</summary>
    public CombatBurn? Burn { get; set; }

    /// <summary>Overrides the curated combat constants for this fight (a search trial); null is the default tuning. It is cloned with the memory.</summary>
    public CombatTuning? Tuning { get; set; }

    /// <summary>Drops pawn's last order, so the next stop gives it again (native refused it).</summary>
    public CombatMemory Forget(PawnId pawn)
    {
        var m = Copy();
        m.Issued.RemoveAll(o => o.Pawn == pawn);
        if (m.Rescue is { } rescue && rescue.Rescuer == pawn)
        {
            m.Rescue = rescue with { Rescuer = default };
        }

        return m;
    }

    /// <summary>Remembers cell as one native could not path to, so the next formation picks another and a hold standing on it re-forms.</summary>
    public CombatMemory RefuseCell(Cell cell)
    {
        var m = Copy();
        if (!m.Unreachable.Contains(cell))
        {
            m.Unreachable.Add(cell);
        }

        return m;
    }

    /// <summary>Forget for a mortar order native refused for its shell: the shell is not asked for again this fight.</summary>
    public CombatMemory RefuseShell(CombatOrder order)
    {
        var m = Forget(order.Pawn);
        if (order.Shell.Length != 0 && !m.NoShells.Contains(order.Shell))
        {
            m.NoShells.Add(order.Shell);
        }

        return m;
    }

    /// <summary>
    /// A mortar_fire order native refused: the crew of its mortar is forgotten so the next stop sends the pair
    /// again, and a shell refusal is RefuseShell.
    /// </summary>
    public CombatMemory RefuseFire(CombatOrder order, bool shell)
    {
        PawnId crew = default;
        foreach (var r in Roles)
        {
            if (r.Mortar is { } mortar && mortar == order.Cell)
            {
                crew = r.Pawn;
            }
        }

        if (shell)
        {
            return RefuseShell(order with { Pawn = crew });
        }

        return Forget(crew);
    }

    /// <summary>Forget for an attack native refused cannot_hit from the shooter's cell: the pair is remembered until the shooter moves or the target is gone.</summary>
    public CombatMemory RefuseHit(CombatOrder order, Cell from)
    {
        var m = Forget(order.Pawn);
        if (order.Kind == CombatOrderKind.Attack && order.Target != default)
        {
            m.CannotHit.Add(new HitRefusal(order.Pawn, order.Target, from));
        }

        return m;
    }

    /// <summary>Whether an attack on target was refused native from pawn's cell.</summary>
    internal bool RefusedHit(PawnId pawn, PawnId target, Cell from) => CannotHit.Contains(new HitRefusal(pawn, target, from));

    public CombatMemory Copy()
    {
        var m = this with { };
        m.Roles = EquatableArray.From(Roles.Select(r => r with { }));
        m.Issued = [.. Issued];
        m.Relieved = [.. Relieved];
        m.Rescue = Rescue is null ? null : Rescue with { };
        m.PodDoors = [.. PodDoors];
        m.Repairs = [.. Repairs];
        m.CannotHit = [.. CannotHit];
        m.Unreachable = [.. Unreachable];
        m.Animals = [.. Animals];
        m.Untrained = [.. Untrained];
        m.NoShells = [.. NoShells];
        m.Dosed = [.. Dosed];
        m.Casts = [.. Casts];
        m.EmpAdapted = [.. EmpAdapted];
        m.Flank = Flank?.Clone();
        m.Tuning = Tuning is null ? null : Tuning with { };
        m.Groups = [.. Groups.Select(g => g.Copy())];
        m.PotshotDoor = PotshotDoor is null ? null : PotshotDoor with { };
        m.ChokeDoor = ChokeDoor is null ? null : ChokeDoor with { };
        m.HeldDoors = [.. HeldDoors];
        m.Burn = Burn is null ? null : Burn with { };
        m.Pods = Pods is null ? null : Pods with { };
        return m;
    }

    /// <summary>Records <paramref name="order"/> as the pawn's last, in pawn order; it updates this working memory and returns it.</summary>
    internal CombatMemory Issue(CombatOrder order, Tick tick)
    {
        Issued.RemoveAll(o => o.Pawn == order.Pawn);
        Issued.Add(new IssuedOrder(order, tick));
        Issued = PolicySort.ByLess(Issued, (a, b) => a.Pawn < b.Pawn);
        return this;
    }

    /// <summary>
    /// Whether the pawn is already doing want: at the cell for a move, on the target for an attack, or given
    /// this same order and not seen doing anything else since.
    /// </summary>
    internal bool Doing(CombatOrder want, CombatPawnState s)
    {
        if (want.Kind == CombatOrderKind.HoldPosition)
        {
            return s.Cell.TryGetValue(out var at) && at == want.Cell && s.Job == "Wait_Combat";
        }

        if (want.Kind == CombatOrderKind.Attack)
        {
            if (s.Target == want.Target)
            {
                return true;
            }
        }
        else if (want.Kind == CombatOrderKind.Move)
        {
            if (s.Cell.TryGetValue(out var at) && at == want.Cell)
            {
                return true;
            }
        }
        else if (want.Kind == CombatOrderKind.Repair)
        {
            if (s.Job == "Repair")
            {
                return true;
            }
        }
        else if (want.Kind == CombatOrderKind.ManMortar)
        {
            // Manning the same mortar at the same aim (the order carries the aim so a new one re-sends the
            // pair); the stance of a crew waiting on its gun says nothing.
            return s.Job == "ManTurret" && Issued.Any(o => o.Pawn == want.Pawn && o.CombatOrder == want);
        }

        foreach (var o in Issued)
        {
            if (o.Pawn == want.Pawn && o.CombatOrder == want)
            {
                // A live stance that contradicts the order (idle away from the cell) re-issues it; an unknown
                // stance trusts the last order.
                return s.Stance != CombatStance.Idle;
            }
        }

        return false;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>Go's <c>domain.PawnID(t.ID)</c>: the threat row's wire id as a pawn id.</summary>
    internal static PawnId ThreatPawn(SquadThreatFacts t) => new(t.Id);

    /// <summary>
    /// The fight's per-stop decision: the live combat view at a stop, at most one geometry answer, the stop
    /// that woke it and the fight's memory in; the changed orders, a geometry ask and the next memory out. It
    /// is pure and deterministic: no clock, no IO, and every input is sorted before use, so a shuffled view
    /// decides the same orders.
    ///
    /// It runs in two phases. Formation (before contact, and again on a raid phase change, a breach, a
    /// compromised hold or a squad target lost) assigns roles: the hold-the-line positions on a complete
    /// layout, otherwise the tribal or squad assignment, the choice the one-shot planner made. Reaction (every
    /// stop) turns the roles into orders, changes only: a pawn already doing what its role wants gets
    /// nothing, and an order that would interrupt aim warmup or cooldown is dropped unless it is a retreat or
    /// a rescue. Drafted pawns on fire-at-will pick their own targets once in place.
    /// </summary>
    public static (List<CombatOrder> Orders, GeometryRequest? Ask, CombatMemory Memory) DecideCombat(
        CombatView view,
        GeometryReply geometry,
        StopEvent stop,
        CombatMemory memory
    )
    {
        view = view.Sorted();

        // Contained raiders who will bleed down are left to go down.
        var spared = SparedBleeders(view);
        view = view.Spare(spared);
        var next = memory.Copy();
        next.Tick = view.Tick;
        next.CannotHit = KeepHitRefusals(view, next.CannotHit);
        var live = view.Live();

        // A downed or dead defender keeps no role; its rescue is rescueStep's (combat_rescue.go).
        next.Roles = EquatableArray.From(next.Roles.Where(r => live.GetValueOrDefault(r.Pawn)));
        var orderable = new HashSet<PawnId>(view.Orderable.Items);
        foreach (var d in view.Defenders.Items)
        {
            if (d.Deathresting)
            {
                orderable.Remove(d.Id); // a deathrester takes no order
            }
        }

        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        {
            var (exitOrders, exiting) = SiteExitTurn(view, stop, next, state, orderable);
            if (exiting)
            {
                return (exitOrders, null, next);
            }
        }

        {
            var (breakOrders, ok) = PrisonBreakTurn(view, next, state, orderable);
            if (ok)
            {
                // A prison break picks its own tactic and answers every stop itself: no geometry, no raid reactions.
                foreach (var o in breakOrders)
                {
                    next.Issue(o, view.Tick);
                }

                return (breakOrders, null, next);
            }
        }

        var formed = false; // a formation this stop picks the potshot door
        WaitTurn(view, next);
        SiegeTurn(view, next);
        if (view.Pods.TryGetValue(out var pods) && next.Pods is null)
        {
            // The arrival row may leave a later frame; the fight keeps it.
            next.Pods = pods;
        }

        if (next.Pods is not null && (next.Tactic != CombatTactic.Pods || Reform(view, stop, next)))
        {
            // A pods arrival picks its own tactic, not the squad fallback.
            var ask = PodsAsk(view, next.Pods);
            if (ask is not null && !geometry.Answered)
            {
                // The doorway flank cells are used only where the game finds them standable, asked in the
                // stop's one round trip.
                return ([], ask, memory);
            }

            next.Tactic = CombatTactic.Pods;
            next.Refusal = "";
            var (podRoles, podDoors) = PodFormation(view, next.Pods, geometry);
            IReadOnlyList<PodDoor> doors = podDoors;
            next.Roles = EquatableArray.From(podRoles);
            var (podWaited, podWaiting) = PodWait(view, stop, next);
            next = podWaited;
            if (podWaiting)
            {
                var (heldRoles, heldDoors) = HoldBehindDoors(next.Roles, doors);
                next.Roles = EquatableArray.From(heldRoles);
                doors = heldDoors;
            }

            next.PodDoors = [.. KeepSent(next.PodDoors, doors)];
            next.Formed = view.Tick;
        }
        else if (!RelieveBlocker(view, stop, next) && !FallBack(view, stop, next) && Reform(view, stop, next))
        {
            var ask = FormationAsk(view, next);
            if (ask is not null && !geometry.Answered)
            {
                // Formation asks the game for its candidate cells by role in the stop's one geometry round
                // trip; with nothing to ask (no layout) it forms at once.
                return ([], ask, memory);
            }

            if (MeleeDoorSite(view) is { } door && MeleeDoorFormation(view, geometry, door, next.Relieved).Count > 0)
            {
                next.Tactic = CombatTactic.DoorChoke;
                next.Roles = EquatableArray.From(MeleeDoorFormation(view, geometry, door, next.Relieved));
                next.Refusal = "";
                next.ChokeDoor = new PodDoor { Cell = door.Cell, Mode = DoorMode.Close };
            }
            else if (Infestation(view))
            {
                // An infestation picks its own tactic.
                next.Tactic = CombatTactic.Infestation;
                next.Roles = EquatableArray.From(InfestationFormation(view, geometry, next.Relieved));
                next.Refusal = "";
            }
            else if (ManhunterPack(view))
            {
                // A manhunter pack picks its own tactic.
                next.Tactic = CombatTactic.Manhunter;
                next.Roles = EquatableArray.From(ManhunterFormation(view, geometry, next.Relieved));
                next.Refusal = "";
                if (next.Roles.Count == 0)
                {
                    // With no armed defender the pack is sheltered from as a squadless fight is; that plan
                    // admits the combat window instead of parking the clock.
                    next.Tactic = CombatTactic.Shelter;
                    next.Roles = EquatableArray.From(ShelterRoles(view, geometry, next.Unreachable));
                }
            }
            else if (view.Hunt)
            {
                // A squad hunt picks its own tactic.
                next.Tactic = CombatTactic.Hunt;
                next.Roles = EquatableArray.From(HuntFormation(view));
                next.Refusal = "";
            }
            else if (SiegeModeOf(view, next) is var mode && mode != default)
            {
                // A siege picks its own tactic.
                next.Tactic = CombatTactic.Siege;
                next.Roles = EquatableArray.From(SiegeFormation(view, mode));
                next.Refusal = "";
                next.SiegeMode = mode;
            }
            else if (PredictBreach(view) is { } breach)
            {
                // A sapper or breacher raid posts inside its predicted breach.
                next.Tactic = CombatTactic.Sapper;
                next.Roles = EquatableArray.From(SapperFormation(view, breach));
                next.Refusal = "";
                next.SapperBreach = breach.Wall;
                next.Rushing = false;
            }
            else
            {
                next.SapperBreach = null;
                var formGeometry = geometry;
                if (geometry.Role == FormationRole.FiringCells)
                {
                    // A flank answer holds no cover candidates.
                    formGeometry = new GeometryReply { Answered = true, Lines = geometry.Lines };
                }

                var (formTactic, formRoles, formRefusal) = Formation(view, formGeometry, next.Relieved, next.Unreachable);
                next.Tactic = formTactic;
                next.Roles = EquatableArray.From(formRoles);
                next.Refusal = formRefusal;
                if (next.Tactic == CombatTactic.None && memory.Tactic == CombatTactic.Shelter)
                {
                    // Missing geometry cannot invalidate useful issued shelter. A proven unreachable
                    // destination is never retained.
                    next.Tactic = CombatTactic.Shelter;
                    next.Roles = EquatableArray.From(
                        memory.Roles
                            .Where(r => live.GetValueOrDefault(r.Pawn) && !(r.Cell is { } c && next.Unreachable.Contains(c)))
                            .Select(r => r with { })
                    );
                }
            }

            if (next.Tactic == CombatTactic.Squad)
            {
                next.Roles = EquatableArray.From(SquadFallBack(view, geometry, next.Unreachable, memory.Roles, next.Roles));
            }

            next.Formed = view.Tick;
            formed = true;
            next.Flank = null;
            next.Groups = [];
        }

        {
            var flankAsk = FlankAsk(view, next);
            if (flankAsk is not null)
            {
                // The flank detachment's cells take a later stop's round trip.
                if (!geometry.Answered)
                {
                    return ([], flankAsk, memory);
                }

                if (geometry.Role == FormationRole.FiringCells)
                {
                    FlankDetach(view, geometry, next);
                    geometry = geometry with { Role = FormationRole.None };
                }
            }
            else
            {
                var groupAsk = GroupAsk(view, next);
                if (groupAsk is not null)
                {
                    // Each side group's squad cells take a stop's round trip.
                    if (!geometry.Answered)
                    {
                        return ([], groupAsk, memory);
                    }

                    if (geometry.Role == FormationRole.FiringCells)
                    {
                        GroupDetach(view, geometry, next);
                        geometry = geometry with { Role = FormationRole.None };
                    }
                }
            }
        }

        Peel(view, stop, next);
        PullBackTank(stop, next);
        if (!next.PodWait)
        {
            // A pods fight waiting behind closed doors engages no one.
            next.Roles = EquatableArray.From(FocusFire(view, next.Roles, memory.Roles));
        }

        next.Roles = EquatableArray.From(DropMissingTargets(view, next.Roles));
        FromRange(view, next);
        ShipPartHitAndRun(view, next);
        TribalStandoff(view, next);
        SiegeHold(next);
        Lure(view, next);
        SiegeSnipe(view, next);
        Kite(view, next);
        PikemenCharge(view, next);
        ExplosivesCharge(view, next);
        SapperIntercept(view, next);
        SapperRush(view, stop, next);
        DoorPotshot(view, formed, next);
        MeleeDoorTurn(view, next);
        Shelter(view, next);
        Scatter(view, next);
        var stood = CounterBattery(view, memory.Roles, next);
        RocketClumps(view, next);
        Flank(view, next);
        GroupSquads(view, next);
        NoteEmpAdapted(view, next);
        Grenade(view, next);
        HiveGrenade(view, next);
        BurnOut(view, next);
        HeatEntry(view, next);
        DrillEvacuate(view, next, orderable);
        MechDisengage(view, next);

        // Contained raiders who will not bleed down are finished in melee.
        FinishContained(view, next);
        var (rescue, rescueAsk) = RescueStep(view, geometry, stop, next, orderable, state);
        if (rescueAsk is not null)
        {
            return ([], rescueAsk, memory);
        }

        var orders = FlankHoldFire(view, orderable, next);
        var targetExcluded = spared.Count > 0 || NearExploders(view).Count > 0;
        var suppressNativeFire = new HashSet<PawnId>();
        for (var i = 0; i < next.Roles.Count; i++)
        {
            var role = next.Roles[i] with { };
            var rs = state.GetValueOrDefault(role.Pawn) ?? new CombatPawnState();

            // A defender still ingesting its combat drug finishes it.
            if (!orderable.Contains(role.Pawn) || next.Rescue?.Carrying(role.Pawn) == true || rs.Job == "Ingest")
            {
                continue;
            }

            var cellKnown = rs.Cell.TryGetValue(out var at);
            if (next.Tactic == CombatTactic.Shelter && !cellKnown)
            {
                continue;
            }

            var (want, ok) = role.Want(rs);
            var ordinaryHolder = StationaryRangedHold(next, role, rs);
            if (ordinaryHolder && targetExcluded && role.Target == default && (rs.Job == "Wait_Combat" || rs.FireMode == HoldFire))
            {
                suppressNativeFire.Add(role.Pawn);
            }

            var nativeHold = ordinaryHolder && !targetExcluded;
            var atCell = role.Cell is { } rc && cellKnown && at == rc;
            if (nativeHold)
            {
                // Native acquires targets; no stale policy target participates in the observed-target
                // friendly melee safeguard.
                next.Roles[i].Target = default;
                if (atCell)
                {
                    (want, ok) = (new CombatOrder { Pawn = role.Pawn, Kind = CombatOrderKind.HoldPosition, Cell = role.Cell!.Value, Reason = CombatOrderReason.Formation }, true);
                }
            }

            // Target exclusions cannot be delegated to autonomous acquisition. Stop a native hold with no
            // safe target instead of leaving it active.
            var safetyTransition = ordinaryHolder && targetExcluded && atCell && rs.Job == "Wait_Combat";
            if (safetyTransition && !ok)
            {
                (want, ok) = (new CombatOrder { Pawn = role.Pawn, Kind = CombatOrderKind.Stop, Reason = CombatOrderReason.HoldFire }, true);
            }

            if (next.ChokeDoor is { } chokeDoor && chokeDoor.Opener == role.Pawn)
            {
                (want, ok) = (new CombatOrder { Pawn = role.Pawn, Kind = CombatOrderKind.Move, Cell = chokeDoor.Cell, Reason = CombatOrderReason.Formation }, true);
            }

            if (next.PotshotDoor is { } potshot && potshot.Repairer == role.Pawn)
            {
                (want, ok) = (new CombatOrder { Pawn = role.Pawn, Kind = CombatOrderKind.Repair, Cell = potshot.Cell, Reason = CombatOrderReason.Repair }, true);
            }
            else if (role.Repair is { } repair)
            {
                (want, ok) = (new CombatOrder { Pawn = role.Pawn, Kind = CombatOrderKind.Repair, Cell = repair, Reason = CombatOrderReason.Repair }, true);
            }

            if (!ok || (!safetyTransition && next.Doing(want, rs)))
            {
                continue;
            }

            if (InterruptsAim(rs) && want.Reason != CombatOrderReason.Retreat && want.Reason != CombatOrderReason.Rescue && !safetyTransition)
            {
                continue;
            }

            if (want.Kind == CombatOrderKind.ManMortar)
            {
                // The pawnless aim leads its crew order: it sets the shell filter the crew's ManTurret then loads by.
                orders.Add(new CombatOrder { Kind = CombatOrderKind.MortarFire, Cell = want.Cell, Aim = want.Aim, Shell = want.Shell, Reason = CombatOrderReason.CounterBattery });
            }

            orders.Add(want);
        }

        orders = StandDown(stood, orders, orderable, state);
        orders = HoldFireOrders(
            view,
            next.Roles.Where(r => !(next.Rescue?.Carrying(r.Pawn) == true || next.Flank?.Waiting(r.Pawn) == true)).Select(r => r with { }).ToList(),
            orders,
            memory,
            OnlySpared(view, spared),
            suppressNativeFire
        );
        if (!geometry.Answered)
        {
            // The attacks' lines of fire take the stop's geometry round trip when Formation did not.
            var lineAsk = LineAsk(view, orders, next);
            if (lineAsk is not null)
            {
                return ([], lineAsk, memory);
            }
        }

        var (clearedOrders, clearedRoles, clearedHits) = ClearLines(view, orders, geometry, next.Roles, next);
        orders = clearedOrders;
        next.Roles = EquatableArray.From(clearedRoles);
        next.CannotHit = clearedHits;

        // One gunner shoots a wild animal near the raiders.
        orders = EnrageWild(view, orders, next.Roles, orderable, state);

        // A defender with no other order this stop takes its combat drug.
        orders.AddRange(DoseOrders(view, next, orders, orderable, state));

        // An outmatched fight calls its held permits; a call names no drafted pawn and leaves its holder's other orders alone.
        orders.AddRange(PermitCalls(view, next));

        // A psycaster casts its ready combat psycast, one per caster.
        orders.AddRange(CastCalls(view, next));

        // The rescue's orders lead; a door order names no pawn to issue.
        var lead = new List<CombatOrder>(rescue);
        lead.AddRange(PodDoorOrders(view, next));
        lead.AddRange(orders);
        orders = lead;

        // The colony animals' orders name no drafted pawn.
        orders.AddRange(AnimalStep(view, next));
        foreach (var o in orders)
        {
            if (o.Pawn != default && o.Kind != CombatOrderKind.Permit && o.Kind != CombatOrderKind.Cast)
            {
                next.Issue(o, view.Tick);
            }
        }

        return (orders, null, next);
    }

    /// <summary>Whether a pawn is aiming or cooling down: an order now would throw the shot away.</summary>
    private static bool InterruptsAim(CombatPawnState s) => s.Stance == CombatStance.Warmup || s.Stance == CombatStance.Cooldown;

    /// <summary>
    /// The reaction table's formation row: no formation yet, a raid phase change or breach, a hold the raid
    /// has crossed, or a squad whose target is down or whose roster changed (a holder down or badly hurt, a
    /// fit defender without a role; combat_squad_roster.go). Every other stop kind keeps the formation; their
    /// own responses (pull back when hurt, peel, rescue, scatter) are later rows.
    /// </summary>
    private static bool Reform(CombatView view, StopEvent stop, CombatMemory m)
    {
        if (m.Roles.Count == 0 || stop.Kind == CombatStopKind.RaidPhase || stop.Kind == CombatStopKind.Breach)
        {
            return true;
        }

        foreach (var r in m.Roles)
        {
            if (r.Cell is { } c && m.Unreachable.Contains(c))
            {
                return true;
            }
        }

        if (m.Tactic == CombatTactic.Siege || SiegeModeOf(view, m) != default)
        {
            return ReformSiege(view, m);
        }

        if (m.Tactic == CombatTactic.Sapper || ReformSapper(view, m))
        {
            return ReformSapper(view, m);
        }

        if (m.Tactic == CombatTactic.Shelter)
        {
            return ShelterReconsider(view, m).Length != 0;
        }

        if (m.Tactic == CombatTactic.Hold)
        {
            return !view.Layout.TryGetValue(out var layout) || HoldCompromised(HoldLine(layout, m), layout.Toward, Unpeeled(view, stop, m));
        }

        if (m.Tactic == CombatTactic.Pods)
        {
            return ReformPods(view, m);
        }

        if (m.Tactic == CombatTactic.Manhunter || m.Tactic == CombatTactic.Infestation)
        {
            return ReformManhunter(view, m);
        }

        if (m.Tactic == CombatTactic.DoorChoke)
        {
            return MeleeDoorSite(view) is null || SquadRosterChanged(view, stop, m);
        }

        if (m.Tactic == CombatTactic.Squad)
        {
            return SquadTargetDown(view, m) || SquadRosterChanged(view, stop, m);
        }

        if (m.Tactic == CombatTactic.Hunt)
        {
            return ReformHunt(view, m);
        }

        if (m.Tactic == CombatTactic.PrisonBreak)
        {
            // The escapees are down or gone: whatever is left is a fight anew.
            return true;
        }

        return false;
    }

    /// <summary>
    /// Clears a role's target that is gone from the view's threats (fled, despawned): native refuses an attack
    /// on it as not_found, and re-ordering it every stop only gets refused again. The pawn keeps its cell and
    /// picks its own target on fire-at-will.
    /// </summary>
    private static List<CombatRole> DropMissingTargets(CombatView view, List<CombatRole> roles)
    {
        var present = new HashSet<PawnId>(view.Threats.Items.Select(t => ThreatPawn(t)));
        var output = roles.Select(r => r with { }).ToList();
        foreach (var r in output)
        {
            if (!present.Contains(r.Target))
            {
                r.Target = default;
            }
        }

        return output;
    }

    /// <summary>Whether a role's target is dead or downed.</summary>
    private static bool SquadTargetDown(CombatView view, CombatMemory m)
    {
        var down = DownPawns(view);
        foreach (var t in view.Threats.Items)
        {
            if (Positive(t.Dead) || Positive(t.Downed))
            {
                down.Add(ThreatPawn(t));
            }
        }

        return m.Roles.Any(r => down.Contains(r.Target));
    }

    /// <summary>
    /// Asks the game for covered cells behind the layout's firing line against the live threats. Without a
    /// layout or a live hostile there is nothing to ask. adjacent_to_choke and firing_cells have no caller
    /// yet: the squad formation targets pawns, not cells.
    /// </summary>
    private static GeometryRequest? FormationAsk(CombatView view, CombatMemory memory)
    {
        if (MeleeDoorSite(view) is { } door)
        {
            return MeleeDoorAsk(view, door);
        }

        var retreat = new List<Cell>();
        var (_, squad) = SquadAssignments(view);
        if (!squad || SquadHurt(view, memory.Roles).Count > 0 || memory.Roles.Any(r => r.Retreat))
        {
            retreat = ShelterChecks(view);
        }

        if (!view.Layout.TryGetValue(out var layout) || layout.Firing.Count == 0)
        {
            return retreat.Count > 0 ? new GeometryRequest { Cells = retreat } : null;
        }

        var ask = new GeometryRequest { Propose = FormationRole.CoverBehindLine, Line = [.. layout.Firing.Items] };

        // The computed cells come first so the cap drops shooters, not them.
        var named = FormationChecks(view, layout);

        // Named cells share the cells cap with the proposals: half each at most, or all but the choke's
        // neighbours for a blocking formation.
        var limit = MaxGeometryCells / 2;
        if (BlockingChoke(view) is { } blocking)
        {
            // A blocking formation spends the stop's one proposal on blocker cells and names the cells around
            // the line instead, so the game scores their cover for the riflemen.
            ask.Propose = FormationRole.AdjacentToChoke;
            ask.Choke = blocking.Choke;
            ask.OurSide = blocking.OurSide;
            named.AddRange(AroundLine(layout.Firing.Items));
            limit = MaxGeometryCells - 8 - ask.Line.Count;
        }

        foreach (var c in named.Concat(ShooterCells(view)).Concat(retreat))
        {
            if (ask.Cells.Count < limit && !ask.Cells.Contains(c))
            {
                ask.Cells.Add(c);
            }
        }

        // The top-scored hostiles first, so the cap drops the least urgent.
        foreach (var h in RankThreats(view))
        {
            if (ask.Hostiles.Count < MaxGeometryHostiles)
            {
                ask.Hostiles.Add(h.Id);
            }
        }

        return ask;
    }

    /// <summary>
    /// The cells Formation computes itself and names for the game to check: the peeler's home, the cell in
    /// front of each firing cell (every cell a tank may take ahead of it when a pawn wears a shield) and the
    /// one behind it (its pull-back cell).
    /// </summary>
    private static List<Cell> FormationChecks(CombatView view, CombatLayout layout)
    {
        var output = new List<Cell>();

        void Add(Cell c)
        {
            if (c.X >= 0 && c.Z >= 0 && !output.Contains(c) && !layout.Firing.Items.Contains(c))
            {
                output.Add(c);
            }
        }

        if (PeelerHome(view) is { } home)
        {
            Add(home);
        }

        var belted = view.Pawns.Items.Any(p => p.Shield.IsKnown);
        var (v, vok) = TowardVector(layout.Toward);
        if (vok)
        {
            foreach (var f in layout.Firing.Items)
            {
                var ahead = TankCells(f, v);
                if (!belted)
                {
                    // No shield in the fight: the front cell is only a line check.
                    ahead = [.. ahead.Take(1)];
                }

                foreach (var c in ahead)
                {
                    Add(c);
                }

                Add(new Cell(f.X + v.X, f.Z + v.Z));
            }
        }

        return output;
    }

    /// <summary>Bridge.CombatGeometryMaxHostiles.</summary>
    private const int MaxGeometryHostiles = 16;

    /// <summary>
    /// The one-shot planner's choice as the first tactic: hold the layout's firing line against an ordinary
    /// edge assault, otherwise tribal or squad defense. The hold's candidate cells are the layout's firing
    /// cells first, then the game's covered cells behind the line, so a defender the line has no room for
    /// still gets a covered cell.
    /// </summary>
    private static (CombatTactic Tactic, List<CombatRole> Roles, string Refusal) Formation(
        CombatView view,
        GeometryReply geometry,
        IReadOnlyList<PawnId> relieved,
        IReadOnlyList<Cell> unreachable
    )
    {
        var refusal = "no complete defense layout";
        if (view.Layout.TryGetValue(out var layout))
        {
            var cells = new List<Cell>(layout.Firing.Items);
            var blocking = BlockingChoke(view) is not null;
            IEnumerable<Cell> candidates = geometry.Proposals.Items;
            if (blocking)
            {
                // The proposals are blocker cells; the riflemen's covered cells are the named cells around the
                // line the game found standable and covered.
                candidates = CoveredAround(layout.Firing.Items, geometry);
            }

            foreach (var c in candidates)
            {
                if (!cells.Contains(c))
                {
                    cells.Add(c);
                }
            }

            var explosive = ExplosiveHostiles(view);
            if (explosive.Count > 0)
            {
                // Room to move over cover, out of the blasts' reach.
                cells = ExplosiveCells(view, cells, explosive);
            }
            else
            {
                cells = RankByCover(cells, geometry.Scored.Items);
            }

            cells.RemoveAll(c => unreachable.Contains(c));
            cells = SpaceCells(cells, FiringGap(view));
            var (defenders, tanks) = SplitTanks(view);
            List<DefensivePosition> positions;
            (positions, refusal) = ExplainDefensivePositions(cells, layout.Toward, InsideRooms(view), ChokeHeld(view, layout), MarkMechs(view), defenders);
            if (refusal.Length == 0)
            {
                var roles = new List<CombatRole>(positions.Count);
                foreach (var p in positions)
                {
                    roles.Add(new CombatRole { Pawn = p.Defender, Cell = p.Cell, Target = p.Target, Ranged = true });
                }

                var chokes = ChargeChokes(view);
                if (chokes.Count > 0)
                {
                    // A shielded melee charge is split across every door it approaches (the multi-choke rule).
                    roles.AddRange(WaveBlockers(chokes, Rotated(Brawlers(defenders), relieved)));
                }
                else
                {
                    roles.AddRange(BrawlerRoles(view, defenders, blocking, geometry, relieved));
                }

                var taken = roles.Where(r => r.Cell is not null).Select(r => r.Cell!.Value).ToList();
                roles.AddRange(TankRoles(view, tanks, positions, layout.Toward, geometry, taken));
                return (CombatTactic.Hold, SortRoles(roles), "");
            }
        }

        var (assignments, ok) = SquadAssignments(view);
        if (!ok)
        {
            var shelter = ShelterRoles(view, geometry, unreachable);
            if (shelter.Count > 0)
            {
                return (CombatTactic.Shelter, shelter, refusal);
            }

            return (default, [], refusal);
        }

        var squadRoles = new List<CombatRole>(assignments.Count);
        foreach (var a in assignments)
        {
            squadRoles.Add(new CombatRole { Pawn = a.Defender, Target = a.Target, Ranged = a.Mode == SquadMode.Ranged });
        }

        squadRoles = HoldBrawlerBesideGunners(view, squadRoles);
        return (CombatTactic.Squad, SortRoles(squadRoles), refusal);
    }

    /// <summary>The squad fight's defender-to-target proposal.</summary>
    private static (List<SquadAssignment> Assignments, bool Ok) SquadAssignments(CombatView view)
    {
        if (view.Threats.Count == 1)
        {
            var (tribal, ok) = SelectTribalRaiderDefense(view.Threats.Items[0], view.Defenders.Items);
            if (ok)
            {
                return (tribal, true);
            }
        }

        return SelectSquadDefense(MarkSquadMechs(view), view.Defenders.Items);
    }

    private static List<CombatRole> SortRoles(List<CombatRole> roles)
    {
        var sorted = PolicySort.ByLess(roles, (a, b) => a.Pawn != b.Pawn ? a.Pawn < b.Pawn : a.Target < b.Target);

        // One role per pawn: an order batch names a pawn once.
        var output = new List<CombatRole>();
        foreach (var r in sorted)
        {
            if (output.Count == 0 || output[^1].Pawn != r.Pawn)
            {
                output.Add(r);
            }
        }

        return output;
    }

    /// <summary>Keeps the refusals whose shooter stayed put and whose target is still among the view's threats.</summary>
    private static List<HitRefusal> KeepHitRefusals(CombatView view, IEnumerable<HitRefusal> refusals)
    {
        var cells = new Dictionary<PawnId, Cell>();
        foreach (var p in view.Pawns.Items)
        {
            if (p.Cell.TryGetValue(out var c))
            {
                cells[p.Id] = c;
            }
        }

        var present = new HashSet<PawnId>(view.Threats.Items.Select(t => ThreatPawn(t)));
        return [.. refusals.Where(r => cells.TryGetValue(r.Pawn, out var c) && c == r.From && present.Contains(r.Target))];
    }
}
