using ColonistDistance = RimGovernor.Contracts.Wire.Observations.ColonistDistance;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>One census pawn joined to its pawn-table row: every fact is the row's.</summary>
public sealed record EmergencyPawn
{
    public required O.PawnState Observed { get; init; }

    /// <summary>The active native break; null when the pawn is in none.</summary>
    public O.PawnMentalState? MentalState => Observed.MentalState;

    public PawnId Id => new(Observed.Pawn.Id);

    public bool Dead => Observed.Dead;

    public bool Downed => Observed.Downed;

    public bool Bleeding => Observed.Health.Bleeding;

    public bool NeedsTend => Observed.Health.NeedsTend;

    /// <summary>
    /// Decides whether a bleeding colonist who is still on their feet is anyone's patient: WorkGiver_Tend tends a
    /// humanlike only in a bed and the ground tend needs a downed pawn.
    /// </summary>
    public bool InBed => Observed.InBed;

    /// <summary>The pawn's cell on the census map.</summary>
    public Cell Position => Observed.Pawn.Place is O.EntityPlace.OnMap at
        ? at.Cell : throw new DomainException("emergency pawn is not on a map");

    /// <summary>Need_Food: its level and CurCategory, or Lacks for a pawn without a food need.</summary>
    public FoodNeed Food => Observed.Needs.Food;
}

/// <summary>
/// HostileBuilding is a hostile-faction building the census lists as a combat target in
/// its own right (an insect hive, a crashed ship part): a squad target once no hostile pawn remains and, within
/// <see cref="EmergencyThreat.DistantThreatCells"/> of a colonist, a deficit for the military/combat Need and an
/// unsafe-threat hold like a hostile pawn, so the fight is planned under a stopped clock and run under watched
/// combat runs. A building further out is watched like a distant animal.
/// </summary>
public enum ThreatKind : byte
{
    Hostile = 1,
    HuntingPredator,
    IgnoredHunter,
    NearbyPredator,
    NearbyDowned,
    HostileBuilding,
}

/// <summary>
/// One native threat row. Animal and Distance (Chebyshev cells from the nearest
/// free colonist) decide whether a hostile or hunting animal is far enough to be watched rather than held. A
/// HostileBuilding row is never dead or downed while the census lists it; it carries its own native CAS token
/// (SnapshotToken) for the attack order and its definition name.
/// </summary>
public abstract record EmergencyThreat
{
    private EmergencyThreat() { }

    /// <summary>
    /// The nearest-colonist distance from which a hostile or hunting animal is watched rather than held: the
    /// native supervisor stops a running clock for a hostile within the watch policy's hostile_within (20 cells
    /// in serve) and for a predator hunt within 40 cells, both inside this band, so the approach re-enters the
    /// emergency before it can reach anyone. A humanlike or mechanoid threat holds at any distance. A hostile
    /// building goes nowhere, so the same band applies to it.
    /// </summary>
    public const int DistantThreatCells = 50;

    public abstract PawnId Id { get; }
    public abstract ThreatKind Kind { get; }
    public abstract bool Dead { get; }
    public abstract bool Downed { get; }
    public abstract bool Animal { get; }
    public abstract ColonistDistance Distance { get; }
    public abstract string SnapshotToken { get; }
    public abstract string Definition { get; }
    public abstract IReadOnlyList<Cell> Cells { get; }
    public abstract bool Fogged { get; }
    public abstract bool Passive { get; }
    public abstract Cell Position { get; }

    /// <summary>The actual table row and independently observed status census classification evidence.</summary>
    public sealed record PawnThreat(O.PawnState ObservedPawn, O.ThreatPawn CensusThreatPawn, ThreatKind Classification) : EmergencyThreat
    {
        public override PawnId Id => new(ObservedPawn.Pawn.Id);
        public override ThreatKind Kind => Classification;
        public override bool Dead => ObservedPawn.Dead;
        public override bool Downed => ObservedPawn.Downed;
        public override bool Animal => ObservedPawn.Animal;
        public override ColonistDistance Distance => CensusThreatPawn.NearestColonist;
        public override string SnapshotToken => "";
        public override string Definition => "";
        public override IReadOnlyList<Cell> Cells => [];
        public override bool Fogged => ObservedPawn.Fogged;
        public override bool Passive => Classification == ThreatKind.Hostile && CensusThreatPawn.Passive;
        public override Cell Position => MapPosition(ObservedPawn.Pawn.Place);
    }

    /// <summary>The census owns hostile-building evidence; no canonical building table contains these rows.</summary>
    public sealed record BuildingThreat(O.ThreatBuilding ObservedBuilding) : EmergencyThreat
    {
        public override PawnId Id => new(ObservedBuilding.Building.Id);
        public override ThreatKind Kind => ThreatKind.HostileBuilding;
        public override bool Dead => false;
        public override bool Downed => false;
        public override bool Animal => false;
        public override ColonistDistance Distance => ObservedBuilding.NearestColonist;
        public override string SnapshotToken => ObservedBuilding.Snapshot;
        public override string Definition => ObservedBuilding.Building.DefName;
        public override IReadOnlyList<Cell> Cells => CellsRules.RectCells(ObservedBuilding.Occupied);
        public override bool Fogged => ObservedBuilding.Fogged;
        public override bool Passive => ObservedBuilding.Passive;
        public override Cell Position => MapPosition(ObservedBuilding.Building.Place);
    }

    private static Cell MapPosition(O.EntityPlace place) => place is O.EntityPlace.OnMap at
        ? at.Cell : throw new DomainException("emergency threat is not on a map");

    /// <summary>Set on HostileBuilding rows only: the catalog row of the def says a turret whose verb fires mortar shells.</summary>
    public bool Mortar { get; init; }

    /// <summary>Reports a threat that is not passive.</summary>
    public bool Engaging => !Passive;

    /// <summary>Reports whether the row is a hostile building rather than a pawn.</summary>
    public bool Building => Kind == ThreatKind.HostileBuilding;

    /// <summary>
    /// Reports a hostile or hunting animal, or a hostile building, at least
    /// <see cref="DistantThreatCells"/> from every colonist. A crashed ship part is never distant, and a threat on a
    /// map without a colonist is not distant either.
    /// </summary>
    public bool DistantThreat =>
        ((Building && !ShipPart(Definition)) || Animal) && Distance is ColonistDistance.Nearest { Cells: >= DistantThreatCells };

    /// <summary>
    /// Reports a crashed ship part's definition (DefoliatorShipPart, PsychicDronerShipPart): a gunless hostile
    /// building that harms the colony from wherever it lands until destroyed.
    /// </summary>
    public static bool ShipPart(string definition) => definition.Contains("ShipPart", StringComparison.Ordinal);

    /// <summary>
    /// Reports a threat the colony has not found: a hostile standing in fog can neither reach a colonist nor be
    /// reached by one, so it is watched like a distant animal rather than held.
    /// </summary>
    public bool Undiscovered => Fogged;

    /// <summary>
    /// Whether one census row is an emergency the colony must answer before anything else:
    /// a live, standing hostile, hunting predator or hostile building it has discovered, that is not distant and
    /// that is not a passive insect or hive.
    /// </summary>
    public bool Holds() =>
        Kind is ThreatKind.Hostile or ThreatKind.HuntingPredator or ThreatKind.HostileBuilding
        && !Dead && !Downed && Engaging && !DistantThreat && !Undiscovered;
}

public sealed record EmergencyDependentPatient(EmergencyPawn Pawn, DependentPatientGroup Group, bool Rescue, bool Tend);

/// <summary>
/// The status census joined to the pawn table: the free colonists, the dependent patients and the classified
/// threats, each complete. Unseen lists the census pawns the joined pawn table has no map row for yet (a live census
/// newer than the newest frame); they hold the emergency as stale until a frame carries them.
/// </summary>
public sealed record EmergencyFacts
{
    public IReadOnlyList<EmergencyDependentPatient> DependentPatients { get; init; } = [];

    public IReadOnlyList<EmergencyPawn> Colonists { get; init; } = [];

    public IReadOnlyList<EmergencyThreat> Threats { get; init; } = [];

    public IReadOnlyList<PawnId> Unseen { get; init; } = [];

    /// <summary>Every census pawn has its pawn-table row, so the colonist list is the whole census.</summary>
    public bool AllSeen => Unseen.Count == 0;

    /// <summary>
    /// The open tick of the newest drop-pod arrival still closed at the census tick, zero with none: its raiders
    /// are in their pods, so no census row names them yet.
    /// </summary>
    public Tick PodsOpen { get; init; }
}

/// <summary>Members are declared in the order of their wire strings so the enum order is the string order.</summary>
public enum EmergencyReason
{
    CriticalMedical,
    StaleFacts,
    UnsafeThreat,
}

public readonly record struct EmergencyHold(EmergencyReason Reason, PawnId Pawn);

public sealed record EmergencyDecision(bool Clear, IReadOnlyList<EmergencyHold> Holds);

/// <summary>Owns its inputs and carries no mutation or authority API.</summary>
public sealed record EmergencySnapshot
{
    /// <summary>The zero value: invalid, so every evaluation of it holds as stale.</summary>
    public static readonly EmergencySnapshot Empty = new();

    private EmergencySnapshot()
    {
        Current = default;
        Facts = new EmergencyFacts();
    }

    private EmergencySnapshot(GenerationSnapshot current, Tick tick, EmergencyFacts facts)
    {
        Current = current;
        Tick = tick;
        Facts = facts;
        IsValid = true;
    }

    internal GenerationSnapshot Current { get; }

    internal Tick Tick { get; }

    internal EmergencyFacts Facts { get; }

    internal bool IsValid { get; }

    /// <summary>Throws <see cref="DomainException"/> when invalid.</summary>
    public static EmergencySnapshot New(GenerationSnapshot current, Tick tick, EmergencyFacts facts)
    {
        current.Validate();
        DomainException.Require(tick.Value >= 0, "invalid emergency tick or census size");
        var people = new HashSet<PawnId>();
        foreach (var pawn in facts.Colonists)
        {
            DomainException.Require(ValidId(pawn.Id) && people.Add(pawn.Id), "invalid or duplicate emergency pawn");
        }
        foreach (var patient in facts.DependentPatients)
            DomainException.Require(ValidId(patient.Pawn.Id) && people.Add(patient.Pawn.Id)
                && Enum.IsDefined(patient.Group), "invalid or duplicate dependent patient");
        var seen = new HashSet<(PawnId, ThreatKind)>();
        foreach (var threat in facts.Threats)
        {
            DomainException.Require(
                ValidId(threat.Id) && threat.Kind >= ThreatKind.Hostile && threat.Kind <= ThreatKind.HostileBuilding && seen.Add((threat.Id, threat.Kind)),
                "invalid or duplicate emergency threat"
            );
            var hasBuildingFields = threat.SnapshotToken.Length != 0 || threat.Definition.Length != 0 || threat.Cells.Count != 0;
            DomainException.Require(
                threat.Building == hasBuildingFields
                    && !(threat.Building && (!ValidId(new PawnId(threat.SnapshotToken)) || !ValidId(new PawnId(threat.Definition)) || threat.Cells.Count == 0)),
                "hostile building threat requires its snapshot token, definition and occupied cells"
            );
            DomainException.Require(threat.Distance is not ColonistDistance.Nearest { Cells: < 0 }, "invalid emergency threat distance");
        }
        var owned = facts with
        {
            Colonists = [.. facts.Colonists],
            DependentPatients = [.. facts.DependentPatients],
            Threats = [.. facts.Threats],
            Unseen = [.. facts.Unseen],
        };
        return new EmergencySnapshot(current, tick, owned);
    }

    private static bool ValidId(PawnId id) => IdText.IsValidText(id.Value);

    /// <summary>
    /// A drop-pod raid on its way down: an arrival whose pods have not opened by the
    /// census tick. It is a threat for the military/combat Need and a combat run with nothing to acknowledge
    /// until the open, so the fight forms on the arrival.
    /// </summary>
    public bool PodsPending => IsValid && Facts.PodsOpen.Value > 0 && Facts.PodsOpen >= Tick;
}

public static class EmergencyRules
{
    /// <summary>
    /// Clears only current facts. It does not decide how to treat, fight or advance time, and never grants
    /// execution authority. A census pawn the pawn table has no row for yet holds the census as stale.
    /// </summary>
    public static EmergencyDecision EvaluateEmergency(EmergencySnapshot snapshot, GenerationSnapshot current, Tick minimumTick)
    {
        if (!snapshot.IsValid || !current.IsValid || !snapshot.Current.Matches(current) || minimumTick.Value < 0 || snapshot.Tick < minimumTick)
        {
            return new EmergencyDecision(false, [new EmergencyHold(EmergencyReason.StaleFacts, default)]);
        }
        return EvaluateFacts(snapshot.Facts);
    }

    /// <summary>The same safety decision for a boundary-validated, same-frame census.</summary>
    public static EmergencyDecision EvaluateFacts(EmergencyFacts facts)
    {
        var holds = new HashSet<EmergencyHold>();
        void Hold(EmergencyReason reason, PawnId pawn) => holds.Add(new EmergencyHold(reason, pawn));
        foreach (var pawn in facts.Unseen)
        {
            Hold(EmergencyReason.StaleFacts, pawn);
        }
        foreach (var pawn in EmergencyPatientsRules.EmergencyPatients(facts))
        {
            if (BreakResponseRules.AggressiveBreak(pawn))
            {
                Hold(EmergencyReason.UnsafeThreat, pawn.Id);
            }
            // Bleeding, or downed with a tend outstanding, is the emergency: medical work the colony must do
            // now. A colonist who only needs tending is the tend planner's patient, not a reason to hold every
            // dispatch. A colonist downed with nothing to tend is the rescue planner's patient.
            if (!pawn.Dead && UrgentPatient(pawn))
            {
                Hold(EmergencyReason.CriticalMedical, pawn.Id);
            }
        }
        foreach (var threat in facts.Threats)
        {
            // A dead or downed threat, or one the colony has not discovered, is nobody's deficit; a distant animal is
            // watched by the native supervisor's radius, not held, nor is a hostile building that far out.
            if (threat.Holds())
            {
                Hold(EmergencyReason.UnsafeThreat, threat.Id);
            }
        }
        var sorted = holds.OrderBy(h => h.Reason).ThenBy(h => h.Pawn.Value, StringComparer.Ordinal).ToList();
        return new EmergencyDecision(holds.Count == 0, sorted);
    }

    /// <summary>
    /// The CriticalMedical test: a bleeding colonist, or one downed with a tend
    /// outstanding. A bleeding colonist who is up and not in a bed is excluded: no doctor can tend them, and on a
    /// colony without a bed the hold suspended the very goal that would build one.
    /// </summary>
    internal static bool UrgentPatient(EmergencyPawn pawn) =>
        pawn.Downed ? pawn.Bleeding || pawn.NeedsTend : pawn.InBed && pawn.Bleeding;

    /// <summary>
    /// Projects an immunizable condition at its current rates: losing when
    /// severity reaches 1 no later than immunity does. Ties leave no safety margin; nonpositive immunity gain
    /// loses against progressing severity, and nonprogressing severity has no deadline. Known is false when any
    /// value is missing or not finite.
    /// </summary>
    public static (bool Losing, bool Known) LosingImmunityRace(Hediff c)
    {
        if (c.Immunizable is not { Gain: ImmunityGain.PerDay gain } immune)
        {
            return (false, false);
        }
        var severity = c.Severity;
        var immunity = immune.Immunity;
        var sr = immune.SeverityPerDay;
        var ir = gain.Value;
        foreach (var v in (ReadOnlySpan<double>)[severity, immunity, sr, ir])
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                return (false, false);
            }
        }
        if (immunity >= 1 || sr <= 0)
        {
            return (false, true);
        }
        return (ir <= 0 || (1 - severity) / sr <= (1 - immunity) / ir, true);
    }

    /// <summary>The limb infection an amputation removes; non-limb infections are out of scope.</summary>
    internal const string WoundInfection = "WoundInfection";

    /// <summary>
    /// Lists, for a living colonist, the amputation of the part carrying each
    /// wound infection that is losing its immunity race. A projected loss is a projected death, so the operation
    /// needs no failure cap beyond a failure chance below certain death: vanilla's success chance must be known
    /// and positive, with an eligible doctor, no violation and no lethal outcome. Rows are ordered by part index.
    /// </summary>
    public static IReadOnlyList<Surgery> LifeSavingAmputations(CarePawn pawn)
    {
        if (pawn.Dead)
        {
            return [];
        }
        var result = new List<Surgery>();
        var seen = new HashSet<int>();
        foreach (var c in pawn.Conditions)
        {
            if (c.DefName != WoundInfection || c.Part is not { } infected || seen.Contains(infected.Index))
            {
                continue;
            }
            var part = infected.Index;
            var (losing, known) = LosingImmunityRace(c);
            if (!known || !losing)
            {
                continue;
            }
            foreach (var care in pawn.Operations)
            {
                var op = care.Operation;
                if (op.Kind != SurgeryKind.Amputate || op.Part?.Index != part || op.Chance is not SurgeryChance.Rated { Success: > 0 }
                    || op.EligibleDoctors <= 0 || op.Violation || op.Lethal)
                {
                    continue;
                }
                var recipe = op.Recipe;
                Surgery s;
                try
                {
                    s = new Surgery((Domain.PawnId)pawn.Id, recipe, part, false);
                }
                catch (DomainException)
                {
                    continue;
                }
                seen.Add(part);
                result.Add(s);
                break;
            }
        }
        return [.. result.OrderBy(s => s.Part)];
    }
}
