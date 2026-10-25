using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimGovernor.Brain.Policy;

/// <summary>The role a colonist takes in a raid, as the combat sweep labels it from the staged fixture's pawns.</summary>
public static class ExposureRoles
{
    /// <summary>A ranged primary weapon.</summary>
    public const string Gunner = "gunner";

    /// <summary>A standing violence-capable colonist with a melee primary weapon.</summary>
    public const string Melee = "melee";

    /// <summary>A standing colonist incapable of violence, regardless of equipment.</summary>
    public const string Noncombatant = "noncombatant";

    /// <summary>A standing violence-capable colonist without a primary; tactics may fight with or shelter it.</summary>
    public const string Unarmed = "unarmed";

    /// <summary>A colonist already downed before the fight, independently of violence capability and equipment.</summary>
    public const string Downed = "downed";

    public static IReadOnlyList<string> All { get; } = [Gunner, Melee, Noncombatant, Unarmed, Downed];

    /// <summary>One definition shared by live forecasts and native sweep staging. Equipment never proves capability.</summary>
    public static string Of(bool downed, bool violenceCapable, bool hasPrimary, bool ranged) =>
        downed ? Downed : !violenceCapable ? Noncombatant : !hasPrimary ? Unarmed : ranged ? Gunner : Melee;
}

/// <summary>A mean and its uncertainty bounds. The harm ledger reads the range, never only the mean.</summary>
public readonly record struct HitRange(double Mean, double Lo, double Hi);

/// <summary>
/// One empirical cell: actual colonist rows (Trials), independent raids (Raids), and raids with any role pawn hit
/// (HitRaids). Means use the fixed fixture mixture; the hit 90% and down 95% ranges resample whole raids within each fixture.
/// </summary>
public sealed record ExposureCell(string Bin, string Role, int Trials, double MeanHits, double HitsLo, double HitsHi, double DownRate, double DownLo, double DownHi, int Raids, int HitRaids);

/// <summary>The exposure table file: <see cref="RaidExposure.Schema"/> and the cells that have trials.</summary>
public sealed record ExposureTable(int Schema, IReadOnlyList<ExposureCell> Cells);

/// <summary>An answer from the table: an empirical range with sufficient raid evidence, or uncalibrated; findings qualify either.</summary>
public abstract record ExposureReading
{
    private ExposureReading(IReadOnlyList<string> findings) => Findings = findings;

    public IReadOnlyList<string> Findings { get; }

    /// <summary>The cell has <see cref="RaidExposure.MinRaids"/> independent raids or more: its empirical range and evidence limits.</summary>
    public sealed record Calibrated(HitRange Range, IReadOnlyList<string> Findings) : ExposureReading(Findings);

    /// <summary>The cell has too few raids, or the ratio is outside the table; a finding names the cell.</summary>
    public sealed record Uncalibrated(IReadOnlyList<string> Findings) : ExposureReading(Findings);
}

/// <summary>
/// Raid exposure: hits a colonist takes per raid, estimated from real-game combat sweeps (harm model, step 3) and keyed by
/// raid points divided by defense capacity (the ratio bin) and the colonist's role. The defense layout is not an axis: every
/// reading reports <see cref="LayoutUnassessed"/>. Qualified cells retain empirical and unconfirmed-stability findings
/// (kept-constants.md, Exposure table cells); a zero-hit sample does not establish immunity.
/// </summary>
public static class RaidExposure
{
    /// <summary>The table file schema.</summary>
    public const int Schema = 2;

    /// <summary>A cell with fewer independent raids cannot supply an empirical estimate.</summary>
    public const int MinRaids = 20;

    public const string LayoutUnassessed = "exposure_layout_unassessed";

    public const string CellUncalibrated = "exposure_cell_uncalibrated";

    public const string EmpiricalEstimate = "exposure_empirical_estimate";

    public const string StabilityUnconfirmed = "exposure_stability_unconfirmed";

    /// <summary>The bin edges of the ratio. A ratio on an edge belongs to the bin it starts: bins are [lo, hi).</summary>
    public static IReadOnlyList<double> Edges { get; } = [0.5, 1, 2, 4];

    /// <summary>The bin names, ascending: one more than the edges.</summary>
    public static IReadOnlyList<string> Bins { get; } = ["0-0.5", "0.5-1", "1-2", "2-4", "4+"];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
    };

    private static readonly Lazy<ExposureTable> Embedded = new(LoadEmbedded);

    /// <summary>The bin a ratio falls in; null for a ratio outside the table (negative or not a number).</summary>
    public static string? BinOf(double ratio)
    {
        if (double.IsNaN(ratio) || ratio < 0)
        {
            return null;
        }
        var i = 0;
        while (i < Edges.Count && ratio >= Edges[i])
        {
            i++;
        }
        return Bins[i];
    }

    /// <summary>The embedded table, parsed once.</summary>
    public static ExposureTable Load() => Embedded.Value;

    private static ExposureTable LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("exposure.json")
            ?? throw new DomainException("exposure.json is not embedded in the policy assembly");
        return Parse(stream);
    }

    /// <summary>
    /// Parses a table file: malformed JSON or an unknown or missing field throws <see cref="JsonException"/>; another schema,
    /// an unknown bin or role, or a duplicate cell throws <see cref="DomainException"/>.
    /// </summary>
    public static ExposureTable Parse(Stream json)
    {
        var table = JsonSerializer.Deserialize<ExposureTable>(json, Options) ?? throw new JsonException("exposure table is empty");
        if (table.Schema != Schema)
        {
            throw new DomainException($"exposure.json has schema {table.Schema}, expected {Schema}");
        }
        var seen = new HashSet<(string, string)>();
        foreach (var c in table.Cells)
        {
            if (!Bins.Contains(c.Bin, StringComparer.Ordinal) || !ExposureRoles.All.Contains(c.Role, StringComparer.Ordinal))
            {
                throw new DomainException($"exposure.json cell {c.Bin}/{c.Role} names an unknown bin or role");
            }
            if (!seen.Add((c.Bin, c.Role)))
            {
                throw new DomainException($"exposure.json lists {c.Bin}/{c.Role} twice");
            }
        }
        return table;
    }

    /// <summary>The table as the file holds it.</summary>
    public static string Serialize(ExposureTable table) => JsonSerializer.Serialize(table, Options) + "\n";

    /// <summary>Hits per colonist per raid from the embedded table.</summary>
    public static ExposureReading HitsPerColonist(double ratio, string role) => HitsPerColonist(Load(), ratio, role);

    /// <summary>
    /// Hits a colonist of <paramref name="role"/> takes per raid at <paramref name="ratio"/> (raid points over defense
    /// capacity), as the cell's 90% empirical range. A cell under <see cref="MinRaids"/> independent raids, or a ratio outside the table, is
    /// uncalibrated and carries <c>exposure_cell_uncalibrated:&lt;bin&gt;/&lt;role&gt;</c>; every reading carries
    /// <see cref="LayoutUnassessed"/>.
    /// </summary>
    public static ExposureReading HitsPerColonist(ExposureTable table, double ratio, string role)
    {
        var cell = CellOf(table, ratio, role, out var finding);
        return Reading(cell is null ? null : new HitRange(cell.MeanHits, cell.HitsLo, cell.HitsHi), cell, finding);
    }

    /// <summary>The empirical share of colonists who ended down or dead, with its raid-clustered 95% range; the same findings as <see cref="HitsPerColonist(ExposureTable, double, string)"/>.</summary>
    public static ExposureReading DownRate(ExposureTable table, double ratio, string role)
    {
        var cell = CellOf(table, ratio, role, out var finding);
        return Reading(cell is null ? null : new HitRange(cell.DownRate, cell.DownLo, cell.DownHi), cell, finding);
    }

    private static ExposureReading Reading(HitRange? range, ExposureCell? cell, string finding) =>
        range is { } r && cell is not null ? new ExposureReading.Calibrated(r, Findings(cell)) : new ExposureReading.Uncalibrated([finding, LayoutUnassessed]);

    private static ExposureCell? CellOf(ExposureTable table, double ratio, string role, out string finding)
    {
        var bin = BinOf(ratio);
        var cell = bin is null ? null : table.Cells.FirstOrDefault(c => c.Bin == bin && c.Role == role);
        finding = cell is { Raids: >= MinRaids } ? "" : string.Create(CultureInfo.InvariantCulture, $"{CellUncalibrated}:{bin ?? "outside"}/{role}");
        return finding.Length == 0 ? cell : null;
    }

    private static string[] Findings(ExposureCell cell)
    {
        string[] findings = [LayoutUnassessed, EmpiricalEstimate, StabilityUnconfirmed];
        if (cell.HitRaids >= MinRaids)
        {
            return findings;
        }
        var sample = cell.HitRaids == 0 ? "exposure_zero_hit_sample" : "exposure_sparse_hit_sample";
        return [.. findings, string.Create(CultureInfo.InvariantCulture, $"{sample}:{cell.Bin}/{cell.Role}:{cell.HitRaids}/{cell.Raids}")];
    }
}
