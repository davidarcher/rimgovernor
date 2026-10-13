namespace RimGovernor.Brain.Domain;

/// <summary>
/// Names the ritual a Ritual command addresses. A start addresses a ritual kind native resolves against the
/// game (<see cref="Bestowing"/>); a begin addresses a held ritual precept by its id, so an Ideology ritual adds
/// no kind here and no list of ritual defs.
/// </summary>
public readonly record struct RitualKind(string Value)
{
    /// <summary>The Empire's bestowing ceremony of the colonist the Ritual names.</summary>
    public static readonly RitualKind Bestowing = new("bestowing");

    public string Value { get => field ?? string.Empty; } = Value;

    public bool Equals(RitualKind other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}

/// <summary>What a Ritual command asks of its ritual.</summary>
public enum RitualVerb
{
    /// <summary>The player's command that begins a ritual whose lord waits for it (the bestower's ceremony gizmo).</summary>
    Start,

    /// <summary>Starts a held ritual precept with no lord waiting, the way the game's begin-ritual dialog does.</summary>
    Begin,
}

/// <summary>
/// The pawns that fill one role slot of the ritual's behavior, named by the slot id the definition catalog
/// lists (RitualRole.id of the behavior's roles). Pawns are in priority order.
/// </summary>
public sealed record RitualSlot(string Slot, EquatableArray<PawnId> Pawns);

/// <summary>
/// Commands either a waiting bestowing ceremony or a held ritual precept. For bestowing, Pawn is the recipient.
/// For a precept, Pawn is the organizer, Spot is the target, Slots assigns behavior roles, and Spectators lists
/// other attendees. Native validates start conditions and participant eligibility.
/// </summary>
public sealed record Ritual : IActionPayload
{
    /// <summary>Go <c>NewRitual</c> (the start command); throws <see cref="DomainException"/> when invalid.</summary>
    public Ritual(PawnId pawn, RitualKind kind, RitualVerb verb)
    {
        DomainException.Require(pawn.IsValid, "ritual requires a valid pawn identity");
        DomainException.Require(kind == RitualKind.Bestowing, "unsupported ritual kind");
        DomainException.Require(verb == RitualVerb.Start, "unsupported ritual verb");
        Pawn = pawn;
        Kind = kind;
        Verb = verb;
        Spot = default;
        Slots = default;
        Spectators = default;
    }

    private Ritual(PawnId organizer, RitualKind precept, Cell spot, EquatableArray<RitualSlot> slots, EquatableArray<PawnId> spectators)
    {
        Pawn = organizer;
        Kind = precept;
        Verb = RitualVerb.Begin;
        Spot = spot;
        Slots = slots;
        Spectators = spectators;
    }

    public PawnId Pawn { get; }

    /// <summary>Go <c>Ritual()</c>: the bestowing kind or the begun precept's id.</summary>
    public RitualKind Kind { get; }

    public RitualVerb Verb { get; }

    /// <summary>A begin's target cell; default for a start.</summary>
    public Cell Spot { get; }

    /// <summary>A begin's role slot assignments, sorted by slot id.</summary>
    public EquatableArray<RitualSlot> Slots { get; }

    /// <summary>A begin's pawns attending without a role.</summary>
    public EquatableArray<PawnId> Spectators { get; }

    /// <summary>
    /// Go <c>NewRitualBegin</c>: the begin command of the ritual precept with the given id. Every pawn appears
    /// once across the slots and spectators, each slot is named once and holds a pawn.
    /// </summary>
    public static Ritual NewRitualBegin(PawnId organizer, string precept, Cell spot, IEnumerable<RitualSlot> slots, IEnumerable<PawnId> spectators)
    {
        DomainException.Require(organizer.IsValid, "ritual begin requires a valid organizer identity");
        DomainException.Require(Identity.IsValidText(precept) && new RitualKind(precept) != RitualKind.Bestowing, "ritual begin requires a ritual precept id");
        DomainException.Require(spot.X >= 0 && spot.Z >= 0, "ritual begin requires a spot cell on the map");
        var seenSlots = new HashSet<string>(StringComparer.Ordinal);
        var seenPawns = new HashSet<PawnId>();
        void Claim(PawnId pawn)
        {
            DomainException.Require(pawn.IsValid, "ritual begin names an invalid pawn identity");
            DomainException.Require(seenPawns.Add(pawn), "ritual begin names a pawn twice");
        }
        var ownedSlots = new List<RitualSlot>();
        foreach (var slot in slots)
        {
            DomainException.Require(Identity.IsValidText(slot.Slot), "ritual begin requires a valid role slot id");
            DomainException.Require(seenSlots.Add(slot.Slot), "ritual begin names a role slot twice");
            DomainException.Require(slot.Pawns.Count != 0, "ritual begin names a role slot without pawns");
            foreach (var pawn in slot.Pawns)
            {
                Claim(pawn);
            }
            ownedSlots.Add(slot);
        }
        var spectatorList = spectators.ToList();
        foreach (var pawn in spectatorList)
        {
            Claim(pawn);
        }
        var sorted = ownedSlots.OrderBy(s => s.Slot, StringComparer.Ordinal).ToList();
        return new Ritual(organizer, new RitualKind(precept), spot, EquatableArray.From(sorted), EquatableArray.From(spectatorList));
    }
}

public sealed partial record DomainAction
{
    public static DomainAction NewRitualAction(ActionId id, Ritual ritual) => Create(id, ActionKind.Ritual, ritual);

    public Ritual? Ritual => PayloadOf<Ritual>(ActionKind.Ritual);
}
