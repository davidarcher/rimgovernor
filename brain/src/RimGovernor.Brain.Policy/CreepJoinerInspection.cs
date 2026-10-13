using System.Text.Json;

namespace RimGovernor.Brain.Policy;

// Surgical inspection of a creepjoiner. A creepjoiner's downside is hidden at arrival; a surgical inspection
// reveals crumbling mind, organ decay and psychic agony early (the hediffs become visible) and nothing else
// (https://rimworldwiki.com/wiki/Doctoring#Surgical_inspection). The game records no "inspected" flag on the
// pawn: a clean inspection leaves a letter, a temporary Anesthetic hediff and a small surgical cut. So the
// colony keeps its own record, in ManageCreepJoiners's goal record (the save's GovernorState blob,
// persistence-contracts.md): the pawns it ordered an inspection of, and those whose inspection bill has ended.

/// <summary>Where the colony's inspection of one creepjoiner is.</summary>
public enum InspectionStage
{
    [WireName("")]
    None = 0,

    /// <summary>The inspection bill was queued; it has not ended.</summary>
    [WireName("ordered")]
    Ordered,

    /// <summary>The bill has ended. The colony inspected the pawn.</summary>
    [WireName("done")]
    Done,
}

/// <summary>ManageCreepJoiners's goal record: the colony's inspection of each creepjoiner it has ordered one for.</summary>
public sealed record CreepJoinerRecord
{
    public IReadOnlyDictionary<PawnId, InspectionStage> Inspections { get; init; } = new Dictionary<PawnId, InspectionStage>();

    /// <summary>Reads a goal record; an empty record is empty. Throws <see cref="DomainException"/> on malformed text.</summary>
    public static CreepJoinerRecord Parse(string raw)
    {
        if (raw.Length == 0)
        {
            return new CreepJoinerRecord();
        }
        var inspections = new Dictionary<PawnId, InspectionStage>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DomainException("creepjoiner record: not an object");
            }
            foreach (var member in doc.RootElement.EnumerateObject())
            {
                if (member.Name != "inspections")
                {
                    continue;
                }
                if (member.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }
                foreach (var entry in member.Value.EnumerateObject())
                {
                    var text = entry.Value.GetString() ?? "";
                    var known = Wire.TryParse<InspectionStage>(text, out var stage);
                    DomainException.Require(
                        entry.Name.Length != 0 && known && stage is InspectionStage.Ordered or InspectionStage.Done,
                        $"creepjoiner record: pawn \"{entry.Name}\" has inspection stage \"{text}\"");
                    inspections[new PawnId(entry.Name)] = stage;
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new DomainException("creepjoiner record: " + e.Message);
        }
        return new CreepJoinerRecord { Inspections = inspections };
    }

    /// <summary>The record as stored on the goal; an empty record encodes the empty string.</summary>
    public string Encode()
    {
        if (Inspections.Count == 0)
        {
            return "";
        }
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (pawn, stage) in Inspections)
        {
            sorted[pawn.Value] = Wire.Name(stage);
        }
        return JsonSerializer.Serialize(new Dictionary<string, SortedDictionary<string, string>> { ["inspections"] = sorted });
    }
}

/// <summary>One creepjoiner to queue a surgical inspection on.</summary>
public sealed record InspectionOrder(PawnId Pawn, string Recipe, int Part);

/// <summary>
/// One review's inspection work: the orders to queue, the record the review leaves (orders become ordered,
/// ended bills become done, pawns no longer in the colony drop out), and why a creepjoiner awaiting an
/// inspection cannot be ordered one (plain English, one per pawn).
/// </summary>
public sealed record Inspections
{
    public EquatableArray<InspectionOrder> Orders { get; init; }

    public CreepJoinerRecord Record { get; init; } = new();

    public EquatableArray<string> Waiting { get; init; }

    /// <summary>Whether the review has inspection work: an order to queue or a record that changed from <paramref name="was"/>.</summary>
    public bool Owed(CreepJoinerRecord was) => Orders.Count > 0 || Record.Encode() != was.Encode();
}

/// <summary>One creepjoiner's allowed-area change: Area is the Isolation area's native load id to hold it in, or empty to release it.</summary>
public sealed record IsolationMove(PawnId Pawn, string Area);
