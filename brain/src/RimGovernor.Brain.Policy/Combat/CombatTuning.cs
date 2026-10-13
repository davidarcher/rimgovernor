using System.Globalization;
using System.Text.Json;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The curated subset of DecideCombat's constants that a search may override per trial. It rides in
/// CombatMemory.Tuning, so parallel trials each own theirs and DecideCombat stays pure; production leaves it
/// null and gets <see cref="PolicyFunctions.DefaultCombatTuning"/>. Every other combat constant stays a
/// package const. Each field documents its valid range; <see cref="Validate"/> enforces it.
/// </summary>
public sealed record CombatTuning
{
    /// <summary>A hostile this close (cells) to the potshot door shuts it. 1..DoorOpenRange.</summary>
    public int DoorCloseRange { get; init; }

    /// <summary>The nearest hostile beyond this (cells) reopens the potshot door; a choke door admits only beyond it. DoorCloseRange..30.</summary>
    public int DoorOpenRange { get; init; }

    /// <summary>How many gunners shoot from inside the potshot door. 1..8.</summary>
    public int DoorGunners { get; init; }

    /// <summary>The hold size below which no flank is detached. 2..30.</summary>
    public int FlankMinDefenders { get; init; }

    /// <summary>The flank detachment's gunner count. 1..8.</summary>
    public int FlankSize { get; init; }

    /// <summary>How far (cells) beside the choke the flank anchors. 1..20.</summary>
    public int FlankLateral { get; init; }

    /// <summary>The single-link gap (cells) between hostiles of one raid group. 1..30.</summary>
    public int GroupGap { get; init; }

    /// <summary>A hostile this close (cells) to the kiter starts the lead, and an infestation kiter leads this far. 1..30.</summary>
    public int KiteLeadRange { get; init; }

    /// <summary>How much faster than the fastest slow chaser a kiter must be. 1..3.</summary>
    public double KiteSpeedRatio { get; init; }

    /// <summary>How far (cells) a scattering defender runs from the raid. 1..60.</summary>
    public int ScatterCells { get; init; }

    /// <summary>The cells beyond weapon range a combat drug is dosed. 0..60.</summary>
    public double DoseMargin { get; init; }

    /// <summary>A hostile this close (cells) to the sapper breach sends the posted brawlers at it. 1..30.</summary>
    public int RushRange { get; init; }

    /// <summary>The first field outside its documented range, or empty when every field is in range.</summary>
    public string? Violation()
    {
        (string Name, int V, int Lo, int Hi)[] ints =
        [
            ("DoorCloseRange", DoorCloseRange, 1, Math.Max(1, DoorOpenRange)),
            ("DoorOpenRange", DoorOpenRange, Math.Max(1, DoorCloseRange), 30),
            ("DoorGunners", DoorGunners, 1, 8),
            ("FlankMinDefenders", FlankMinDefenders, 2, 30),
            ("FlankSize", FlankSize, 1, 8),
            ("FlankLateral", FlankLateral, 1, 20),
            ("GroupGap", GroupGap, 1, 30),
            ("KiteLeadRange", KiteLeadRange, 1, 30),
            ("ScatterCells", ScatterCells, 1, 60),
            ("RushRange", RushRange, 1, 30),
        ];
        foreach (var (name, v, lo, hi) in ints)
        {
            if (v < lo || v > hi)
            {
                return string.Create(CultureInfo.InvariantCulture, $"combat tuning {name} = {v} outside {lo}..{hi}");
            }
        }
        (string Name, double V, double Lo, double Hi)[] floats =
        [
            ("KiteSpeedRatio", KiteSpeedRatio, 1, 3),
            ("DoseMargin", DoseMargin, 0, 60),
        ];
        foreach (var (name, v, lo, hi) in floats)
        {
            if (!(v >= lo && v <= hi))
            {
                return string.Create(CultureInfo.InvariantCulture, $"combat tuning {name} = {v} outside {lo}..{hi}");
            }
        }
        return null;
    }

    /// <summary>Whether every field is inside its documented range.</summary>
    public bool IsValid => Violation() is null;

    /// <summary>Throws <see cref="DomainException"/> naming the first field outside its documented range.</summary>
    public void Validate()
    {
        if (Violation() is { } message)
        {
            throw new DomainException(message);
        }
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The test-only environment variable the live service reads a CombatTuning from. The value is a JSON
    /// object whose fields override <see cref="DefaultCombatTuning"/>; the result must validate. Unset or empty,
    /// the override is inert and production keeps the default. Policy never reads the environment.
    /// </summary>
    public const string CombatTuningEnv = "RIMGOVERNOR_COMBAT_TUNING";

    /// <summary>
    /// Decodes CombatTuningEnv's value over the default tuning. An empty value is no override (null); unknown
    /// fields, trailing data and out-of-range values throw <see cref="DomainException"/>. The JSON text is the
    /// real external input here, so it keeps the Go field names (matched ignoring case, as Go's decoder does).
    /// </summary>
    public static CombatTuning? ParseCombatTuning(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var t = DefaultCombatTuning();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException e)
        {
            throw new DomainException($"{CombatTuningEnv}: {e.Message}");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Null)
            {
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new DomainException($"{CombatTuningEnv}: json: cannot unmarshal {root.ValueKind} into CombatTuning");
                }
                foreach (var property in root.EnumerateObject())
                {
                    t = ApplyTuningField(t, property);
                }
            }
        }
        try
        {
            t.Validate();
        }
        catch (DomainException e)
        {
            throw new DomainException($"{CombatTuningEnv}: {e.Message}");
        }
        return t;
    }

    private static CombatTuning ApplyTuningField(CombatTuning t, JsonProperty property)
    {
        int Int()
        {
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var v))
            {
                return v;
            }
            throw new DomainException($"{CombatTuningEnv}: json: cannot unmarshal {property.Value.GetRawText()} into Go struct field CombatTuning.{property.Name} of type int");
        }
        double Float()
        {
            if (property.Value.ValueKind == JsonValueKind.Number)
            {
                return property.Value.GetDouble();
            }
            throw new DomainException($"{CombatTuningEnv}: json: cannot unmarshal {property.Value.GetRawText()} into Go struct field CombatTuning.{property.Name} of type float64");
        }
        return property.Name.ToLowerInvariant() switch
        {
            "doorcloserange" => t with { DoorCloseRange = Int() },
            "dooropenrange" => t with { DoorOpenRange = Int() },
            "doorgunners" => t with { DoorGunners = Int() },
            "flankmindefenders" => t with { FlankMinDefenders = Int() },
            "flanksize" => t with { FlankSize = Int() },
            "flanklateral" => t with { FlankLateral = Int() },
            "groupgap" => t with { GroupGap = Int() },
            "kiteleadrange" => t with { KiteLeadRange = Int() },
            "kitespeedratio" => t with { KiteSpeedRatio = Float() },
            "scattercells" => t with { ScatterCells = Int() },
            "dosemargin" => t with { DoseMargin = Float() },
            "rushrange" => t with { RushRange = Int() },
            _ => throw new DomainException($"{CombatTuningEnv}: json: unknown field \"{property.Name}\""),
        };
    }

    /// <summary>The shipped behaviour; production always uses it.</summary>
    public static CombatTuning DefaultCombatTuning() => new()
    {
        DoorCloseRange = DoorCloseRange,
        DoorOpenRange = DoorOpenRange,
        DoorGunners = DoorGunners,
        FlankMinDefenders = FlankMinDefenders,
        FlankSize = FlankSize,
        FlankLateral = FlankLateral,
        GroupGap = GroupGap,
        KiteLeadRange = KiteLeadRange,
        KiteSpeedRatio = KiteSpeedRatio,
        ScatterCells = ScatterCells,
        DoseMargin = DoseMargin,
        RushRange = RushRange,
    };

    /// <summary>The memory's tuning, the default when none is carried (Go's <c>m.tuning()</c>; renamed because the field is <c>Tuning</c>).</summary>
    public static CombatTuning EffectiveTuning(this CombatMemory m) => m.Tuning ?? DefaultCombatTuning();

    /// <summary>Returns the memory carrying <paramref name="t"/> for the rest of the fight.</summary>
    public static CombatMemory WithTuning(this CombatMemory m, CombatTuning t) => m with { Tuning = t };
}
