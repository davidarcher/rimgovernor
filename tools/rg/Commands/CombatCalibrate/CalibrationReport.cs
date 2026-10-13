using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimGovernor.Tools.Commands.CombatCalibrate;

/// <summary>What a recorded pass is valid for.</summary>
internal sealed record CalibrationStamp
{
    [JsonPropertyName("assemblyHash")]
    public string AssemblyHash { get; init; } = "";

    [JsonPropertyName("rulesVersion")]
    public int RulesVersion { get; init; }

    [JsonPropertyName("thresholdsVersion")]
    public int ThresholdsVersion { get; init; }

    [JsonPropertyName("sweepVersion")]
    public int SweepVersion { get; init; }
}

/// <summary>One variant's real and sim means.</summary>
internal sealed record Cell
{
    public string Variant { get; init; } = "";

    public double RealDeaths { get; init; }

    public double SimDeaths { get; init; }

    public double RealDowns { get; init; }

    public double SimDowns { get; init; }

    public double RealDamage { get; init; }

    public double SimDamage { get; init; }

    public int RealDeathSeeds { get; init; }

    public int SimDeathSeeds { get; init; }
}

/// <summary>One fixture's verdict.</summary>
internal sealed class FixtureReport
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    /// <summary>"measured" or why not.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    /// <summary>The measured variants' means, real beside sim.</summary>
    [JsonPropertyName("cells")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Cell>? Cells { get; set; }

    /// <summary>The variant pairs the real sweep orders (held-out only).</summary>
    [JsonPropertyName("pairs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Pairs { get; set; }

    /// <summary>The pairs the sim orders the same way (held-out only).</summary>
    [JsonPropertyName("agreed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Agreed { get; set; }
}

/// <summary>One sim-vs-real difference in a gated measure.</summary>
internal sealed record Gap
{
    public string Fixture { get; init; } = "";

    public string Variant { get; init; } = "";

    public string Measure { get; init; } = "";

    public double Real { get; init; }

    public double Sim { get; init; }

    public double Tolerance { get; init; }

    /// <summary>How many tolerances apart the sim and real means are.</summary>
    public double Excess() => Math.Abs(Sim - Real) / Tolerance;
}

/// <summary>A survival-critical misranking: the sim prefers Preferred, but its real sweep has more colonist deaths than Other's.</summary>
internal sealed record Misranking
{
    public string Fixture { get; init; } = "";

    public string Preferred { get; init; } = "";

    public string Other { get; init; } = "";

    public double RealDeathsPreferred { get; init; }

    public double RealDeathsOther { get; init; }
}

/// <summary>The real outcome lost by following the sim's pick on a fixture.</summary>
internal sealed record Regret
{
    public string Fixture { get; init; } = "";

    public string SimPick { get; init; } = "";

    public string RealBest { get; init; } = "";

    public double Deaths { get; init; }

    public double Downs { get; init; }

    public double Damage { get; init; }
}

/// <summary>The gate's output (Go <c>calibrate.Report</c>). Pass is true only when Failures is empty. Serialized with the Go field names so the committed report.json stays readable.</summary>
internal sealed record CalibrationReport
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private const int LargestGaps = 5;

    public CalibrationStamp Stamp { get; init; } = new();

    public bool Pass { get; init; }

    public IReadOnlyList<FixtureReport> Fixtures { get; init; } = [];

    /// <summary>Pooled over held-out fixtures; 0 with no pairs.</summary>
    public double RankAgreement { get; init; }

    public int RankPairs { get; init; }

    public IReadOnlyList<Misranking>? Misrankings { get; init; }

    public IReadOnlyList<Regret>? Regrets { get; init; }

    /// <summary>Variants the sim predicts with no colonist death in any seed where the real sweep has at least one.</summary>
    public IReadOnlyList<string>? FalseSafe { get; init; }

    /// <summary>Every gated measure whose sim mean is out of tolerance, the largest first.</summary>
    public IReadOnlyList<Gap>? Gaps { get; init; }

    public IReadOnlyList<string>? Failures { get; init; }

    /// <summary>The committed form of a report.</summary>
    public string Marshal() => JsonSerializer.Serialize(this, WriteOptions);

    /// <summary>Reads a committed report.</summary>
    public static CalibrationReport Parse(string json) =>
        JsonSerializer.Deserialize<CalibrationReport>(json, ReadOptions) ?? throw new JsonException("report is empty");

    /// <summary>The command's verdict: 0 for a pass, 1 for a fail.</summary>
    public int ExitCode() => Pass ? 0 : 1;

    /// <summary>The human report: the verdict and thresholds first, then every failure, the largest gaps, regret and false-safe variants, then the means.</summary>
    public string Text()
    {
        var b = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        b.Append(inv, $"combatsim calibration gate (ranged slice): {(Pass ? "PASS" : "FAIL")}\n");
        b.Append(inv, $"stamp: assembly {Stamp.AssemblyHash}, rules version {Stamp.RulesVersion}, thresholds version {Stamp.ThresholdsVersion}, sweep schema {Stamp.SweepVersion}\n");
        b.Append(inv, $"thresholds: rank agreement >= {CalibrationGate.MinRankAgreement:F2} on held-out fixtures; {CalibrationGate.MaxMisrankings} survival-critical misrankings; colonist deaths and downs within +/-{CalibrationGate.RelTolerance * 100:F0}% of the real mean or +/-{CalibrationGate.SmallMeanPawns:F0} pawn\n");
        b.Append("caveat: committed real sweeps predate #2989 and read a dead colonist as neither dead nor down (the native read omits the dead), so their colonist deaths are a floor of 0 and their downs leave out the dead\n");
        b.Append(inv, $"rank agreement: {RankAgreement:F2} over {RankPairs} pairs; survival-critical misrankings: {Misrankings?.Count ?? 0}\n");
        foreach (var f in Failures ?? [])
        {
            b.Append(inv, $"  FAILURE: {f}\n");
        }
        if (Gaps is { Count: > 0 } gaps)
        {
            b.Append("largest gaps (sim mean vs real mean, in tolerances):\n");
            foreach (var g in gaps.Take(LargestGaps))
            {
                b.Append(inv, $"  {g.Fixture}/{g.Variant} {g.Measure}: sim {g.Sim:F2}, real {g.Real:F2}, {g.Excess():F1}x the +/-{g.Tolerance:F2} tolerance\n");
            }
        }
        foreach (var g in Regrets ?? [])
        {
            b.Append(inv, $"native regret {g.Fixture}: following the sim picks {g.SimPick}, the real best is {g.RealBest}; deaths {Signed(g.Deaths, 2)}, downs {Signed(g.Downs, 2)}, damage {Signed(g.Damage, 1)}\n");
        }
        if (Regrets is null or { Count: 0 })
        {
            b.Append("native regret: not measured (no held-out ranking)\n");
        }
        if (FalseSafe is null or { Count: 0 })
        {
            b.Append("false-safe variants (sim sees no colonist death, real does): none\n");
        }
        else
        {
            b.Append(inv, $"false-safe variants (sim sees no colonist death, real does): {string.Join(", ", FalseSafe)}\n");
        }
        b.Append("fixtures:\n");
        foreach (var f in Fixtures)
        {
            b.Append(inv, $"  {f.Name} ({f.Role}): {f.Status}\n");
            foreach (var c in f.Cells ?? [])
            {
                b.Append(inv, $"    {c.Variant,-14} deaths real {c.RealDeaths:F2} sim {c.SimDeaths:F2} (seeds with a death real {c.RealDeathSeeds} sim {c.SimDeathSeeds}) | downs real {c.RealDowns:F2} sim {c.SimDowns:F2} | damage real {c.RealDamage:F1} sim {c.SimDamage:F1}\n");
            }
        }
        return b.ToString();
    }

    // Go "%+.Nf".
    private static string Signed(double v, int digits)
    {
        var text = Math.Abs(v).ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return (v < 0 ? "-" : "+") + text;
    }
}
