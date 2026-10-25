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

/// <summary>A mean and the 90% range around it. The harm ledger reads the range, never only the mean.</summary>
public readonly record struct HitRange(double Mean, double Lo, double Hi);

/// <summary>
/// One calibrated cell of the exposure table: a ratio bin and a role with the trials behind it (a trial is one colonist in
/// one seed's raid), the mean hits per colonist per raid and its 90% percentile-bootstrap range, and the share of trials
/// that ended down or dead with its 95% Wilson interval.
/// </summary>
public sealed record ExposureCell(string Bin, string Role, int Trials, double MeanHits, double HitsLo, double HitsHi, double DownRate, double DownLo, double DownHi);

/// <summary>The exposure table file: <see cref="RaidExposure.Schema"/> and the cells that have trials.</summary>
public sealed record ExposureTable(int Schema, IReadOnlyList<ExposureCell> Cells);

/// <summary>An answer from the table: calibrated with the cell's range, or uncalibrated; the findings qualify either.</summary>
public abstract record ExposureReading
{
    private ExposureReading(IReadOnlyList<string> findings) => Findings = findings;

    public IReadOnlyList<string> Findings { get; }

    /// <summary>The cell has <see cref="RaidExposure.MinTrials"/> trials or more: its range.</summary>
    public sealed record Calibrated(HitRange Range, IReadOnlyList<string> Findings) : ExposureReading(Findings);

    /// <summary>The cell has too few trials, or the ratio is outside the table; a finding names the cell.</summary>
    public sealed record Uncalibrated(IReadOnlyList<string> Findings) : ExposureReading(Findings);
}

/// <summary>
/// Raid exposure: hits a colonist takes per raid, calibrated by real-game combat sweeps (harm model, step 3) and keyed by
/// raid points divided by defense capacity (the ratio bin) and the colonist's role. The defense layout is not an axis: every
/// reading reports <see cref="LayoutUnassessed"/>. The table's cells are calibrated constants (kept-constants.md, Exposure table cells).
/// </summary>
public static class RaidExposure
{
    /// <summary>The table file schema.</summary>
    public const int Schema = 1;

    /// <summary>A cell with fewer trials is not calibrated.</summary>
    public const int MinTrials = 20;

    public const string LayoutUnassessed = "exposure_layout_unassessed";

    public const string CellUncalibrated = "exposure_cell_uncalibrated";

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
    /// capacity), as the cell's 90% range. A cell under <see cref="MinTrials"/> trials, or a ratio outside the table, is
    /// uncalibrated and carries <c>exposure_cell_uncalibrated:&lt;bin&gt;/&lt;role&gt;</c>; every reading carries
    /// <see cref="LayoutUnassessed"/>.
    /// </summary>
    public static ExposureReading HitsPerColonist(ExposureTable table, double ratio, string role)
    {
        var cell = CellOf(table, ratio, role, out var finding);
        return Reading(cell is null ? null : new HitRange(cell.MeanHits, cell.HitsLo, cell.HitsHi), finding);
    }

    /// <summary>The share of colonists who ended down or dead, as the cell's 95% Wilson interval; the same findings as <see cref="HitsPerColonist(ExposureTable, double, string)"/>.</summary>
    public static ExposureReading DownRate(ExposureTable table, double ratio, string role)
    {
        var cell = CellOf(table, ratio, role, out var finding);
        return Reading(cell is null ? null : new HitRange(cell.DownRate, cell.DownLo, cell.DownHi), finding);
    }

    private static ExposureReading Reading(HitRange? range, string finding) =>
        range is { } r ? new ExposureReading.Calibrated(r, Findings(finding)) : new ExposureReading.Uncalibrated(Findings(finding));

    private static ExposureCell? CellOf(ExposureTable table, double ratio, string role, out string finding)
    {
        var bin = BinOf(ratio);
        var cell = bin is null ? null : table.Cells.FirstOrDefault(c => c.Bin == bin && c.Role == role);
        finding = cell is { Trials: >= MinTrials } ? "" : string.Create(CultureInfo.InvariantCulture, $"{CellUncalibrated}:{bin ?? "outside"}/{role}");
        return finding.Length == 0 ? cell : null;
    }

    private static string[] Findings(string cellFinding) => cellFinding.Length == 0 ? [LayoutUnassessed] : [cellFinding, LayoutUnassessed];
}
