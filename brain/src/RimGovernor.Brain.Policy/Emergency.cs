using ColonistDistance = RimGovernor.Contracts.Wire.Observations.ColonistDistance;
using FoodNeed = RimGovernor.Contracts.Wire.Observations.FoodNeed;

namespace RimGovernor.Brain.Policy;

/// <summary>An observed active native break, never an inferred mood risk.</summary>
public sealed record MentalState
{
    public string DefName { get; init; } = "";

    public bool IsAggro { get; init; }

    public int TicksInState { get; init; }
}

/// <summary>One census pawn joined to its pawn-table row: every fact is the row's.</summary>
public sealed record EmergencyPawn
{
    /// <summary>The active native break; null when the pawn is in none.</summary>
    public MentalState? MentalState { get; init; }

    public required PawnId Id { get; init; }

    public required bool Dead { get; init; }

    public required bool Downed { get; init; }

    public required bool Bleeding { get; init; }

    public required bool NeedsTend { get; init; }

    /// <summary>
    /// Decides whether a bleeding colonist who is still on their feet is anyone's patient: WorkGiver_Tend tends a
    /// humanlike only in a bed and the ground tend needs a downed pawn.
    /// </summary>
    public required bool InBed { get; init; }

    /// <summary>The pawn's cell on the census map.</summary>
    public required Cell Position { get; init; }

    /// <summary>Need_Food: its level and CurCategory, or Lacks for a pawn without a food need.</summary>
    public required FoodNeed Food { get; init; }
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
public sealed record EmergencyThreat
{
    /// <summary>
    /// The nearest-colonist distance from which a hostile or hunting animal is watched rather than held: the
    /// native supervisor stops a running clock for a hostile within the watch policy's hostile_within (20 cells
    /// in serve) and for a predator hunt within 40 cells, both inside this band, so the approach re-enters the
    /// emergency before it can reach anyone. A humanlike or mechanoid threat holds at any distance. A hostile
    /// building goes nowhere, so the same band applies to it.
    /// </summary>
    public const int DistantThreatCells = 50;

    public required PawnId Id { get; init; }

    public required ThreatKind Kind { get; init; }

    public required bool Dead { get; init; }

    public required bool Downed { get; init; }

    public required bool Animal { get; init; }

    public required ColonistDistance Distance { get; init; }

    /// <summary>Set on HostileBuilding rows only.</summary>
    public string SnapshotToken { get; init; } = "";

    /// <summary>Set on HostileBuilding rows only.</summary>
    public string Definition { get; init; } = "";

    /// <summary>Set on HostileBuilding rows only: the building's occupied rect, the cells a ranged defender needs a line of fire to.</summary>
    public IReadOnlyList<Cell> Cells { get; init; } = [];

    /// <summary>The native discovery fact: the threat stands in fog the colony has not explored.</summary>
    public required bool Fogged { get; init; }

    /// <summary>
    /// Insects and hives and hostiles that can sleep, such as dormant mech clusters: true when the thing is
    /// dormant, or awake but not engaging the colony. Any other threat is never passive.
    /// </summary>
    public required bool Passive { get; init; }

    /// <summary>A pawn threat's cell, a building's position cell; the safety overlay draws a pawn's reach around it.</summary>
    public required Cell Position { get; init; }

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
            Threats = [.. facts.Threats.Select(t => t with { Cells = [.. t.Cells] })],
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
        var holds = new HashSet<EmergencyHold>();
        void Hold(EmergencyReason reason, PawnId pawn) => holds.Add(new EmergencyHold(reason, pawn));
        foreach (var pawn in snapshot.Facts.Unseen)
        {
            Hold(EmergencyReason.StaleFacts, pawn);
        }
        foreach (var pawn in EmergencyPatientsRules.EmergencyPatients(snapshot.Facts))
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
        foreach (var threat in snapshot.Facts.Threats)
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
    public static (bool Losing, bool Known) LosingImmunityRace(CareCondition c)
    {
        var sk = c.Severity.TryGetValue(out var severity);
        var ik = c.Immunity.TryGetValue(out var immunity);
        var srk = c.SeverityPerDay.TryGetValue(out var sr);
        var irk = c.ImmunityPerDay.TryGetValue(out var ir);
        if (!sk || !ik || !srk || !irk)
        {
            return (false, false);
        }
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
        if (!pawn.Dead.TryGetValue(out var dead) || dead)
        {
            return [];
        }
        var ck = pawn.Conditions.TryGetValue(out var conditions);
        var ok = pawn.Operations.TryGetValue(out var operations);
        if (!ck || !ok)
        {
            return [];
        }
        var result = new List<Surgery>();
        var seen = new HashSet<int>();
        foreach (var c in conditions!)
        {
            var nk = c.DefName.TryGetValue(out var name);
            var pk = c.PartIndex.TryGetValue(out var part);
            if (!nk || name != WoundInfection || !pk || seen.Contains(part))
            {
                continue;
            }
            var (losing, known) = LosingImmunityRace(c);
            if (!known || !losing)
            {
                continue;
            }
            foreach (var op in operations!)
            {
                var rk = op.Recipe.TryGetValue(out var recipe);
                var ak = op.PartIndex.TryGetValue(out var at);
                var sk = op.SuccessChance.TryGetValue(out var success);
                var dk = op.EligibleDoctors.TryGetValue(out var doctors);
                var vk = op.Violation.TryGetValue(out var violation);
                var lk = op.Lethal.TryGetValue(out var lethal);
                if (op.Kind != SurgeryKind.Amputate || !rk || !ak || at != part || !sk || double.IsNaN(success) || success <= 0 || !dk || doctors <= 0 || !vk || violation || !lk || lethal)
                {
                    continue;
                }
                Surgery s;
                try
                {
                    s = new Surgery((Domain.PawnId)pawn.Id, recipe!, part, false);
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
