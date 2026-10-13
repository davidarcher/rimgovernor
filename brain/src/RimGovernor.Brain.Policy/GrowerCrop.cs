using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Asks which observed plant growers should sow a different crop than they do now. Choices are the same crop
/// census a field request carries; Urgent prefers the fastest crop the way an urgent site selection does.
/// (Go <c>GrowerCropRequest</c>.)
/// </summary>
public sealed record GrowerCropRequest
{
    public IReadOnlyList<CropChoice> Choices { get; init; } = [];

    public IReadOnlyList<PlantGrower> Growers { get; init; } = [];

    public bool Urgent { get; init; }

    public FieldRequest Field { get; init; } = new();
}

/// <summary>One grower whose crop should change, with the ranking that chose the crop. (Go <c>GrowerCropChoice</c>.)</summary>
public sealed record GrowerCropChoice
{
    public string Grower { get; init; } = "";

    public string Current { get; init; } = "";

    public CropChoice Crop { get; init; } = new();

    public string Reason { get; init; } = "";

    public IReadOnlyList<FieldCandidate> Ranking { get; init; } = [];
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>PlanGrowerCrops</c>: ranks, for each grower that can sow, every available edible crop carrying the
    /// grower's sow tag by its nutrition rate over the grower's fertility (the same rate a basin candidate is
    /// scored with), and returns the growers whose current crop is not the winner. A grower whose crop, sow tag,
    /// fertility or sowing state is unknown is left alone, as is one already sowing the winner; growers are
    /// returned in id order.
    /// </summary>
    public static IReadOnlyList<GrowerCropChoice> PlanGrowerCrops(GrowerCropRequest r)
    {
        var @out = new List<GrowerCropChoice>();
        foreach (var g in r.Growers)
        {
            var tk = g.SowTag.TryGetValue(out var tag);
            var ck = g.Crop.TryGetValue(out var current);
            var fk = g.Fertility.TryGetValue(out var fertility);
            if (!Positive(g.CanSow) || !tk || tag!.Length == 0 || !ck || !fk || !FieldPositive(fertility) || g.Id.Length == 0)
            {
                continue;
            }
            var ranking = GrowerCropRanking(r.Choices, tag, fertility, r.Urgent, r.Field);
            if (ranking.Count == 0 || ranking[0].Score <= 0 || ranking[0].Crop.Name == current)
            {
                continue;
            }
            @out.Add(new GrowerCropChoice { Grower = g.Id, Current = current!, Crop = ranking[0].Crop, Reason = ranking[0].Reason, Ranking = ranking });
        }
        return [.. @out.OrderBy(c => c.Grower, StringComparer.Ordinal)];
    }

    /// <summary>Scores every sowable crop for one grower; excluded crops carry a zero score and their reason.</summary>
    private static List<FieldCandidate> GrowerCropRanking(IReadOnlyList<CropChoice> choices, string tag, double fertility, bool urgent, FieldRequest field)
    {
        var ranking = new List<FieldCandidate>();
        foreach (var crop in choices)
        {
            var c = new FieldCandidate { Crop = crop };
            var ak = crop.Available.TryGetValue(out var available);
            var ek = crop.Edible.TryGetValue(out var edible);
            var gk = crop.GrowDays.TryGetValue(out var days);
            var tk = crop.SowTags.TryGetValue(out var tags);
            var yk = crop.HarvestNutrition.IsKnown;
            var sk = crop.FertilitySensitivity.IsKnown;
            if (SiteKnownFalse(crop.DietAllowed) || Positive(crop.RequiresPollution) || GrowsInDark(crop))
            {
                c = c with { Reason = "crop needs a compatible soil or dark site" };
            }
            else if (!ak || !ek || !gk || !FieldPositive(days) || !tk || !yk || !sk)
            {
                c = c with { Reason = "incomplete crop facts" };
            }
            else if (!available || !edible)
            {
                c = c with { Reason = "not an available edible crop" };
            }
            else if (!tags!.Contains(tag))
            {
                c = c with { Reason = "crop cannot be sown on this grower" };
            }
            else
            {
                var score = CropRate(crop, fertility);
                foreach (var term in CropChoiceTerms(field, crop, 1))
                {
                    score += term.Value;
                }
                c = c with { Score = score, Reason = string.Create(CultureInfo.InvariantCulture, $"{score:F4} nutrition/day per cell") };
            }
            ranking.Add(c);
        }
        int Compare(FieldCandidate a, FieldCandidate b)
        {
            if ((a.Score > 0) != (b.Score > 0))
            {
                return a.Score > 0 ? -1 : 1;
            }
            var ad = a.Crop.GrowDays.ValueOrDefault;
            var bd = b.Crop.GrowDays.ValueOrDefault;
            if (urgent && a.Score > 0 && ad != bd)
            {
                return ad < bd ? -1 : 1;
            }
            if (a.Score != b.Score)
            {
                return a.Score > b.Score ? -1 : 1;
            }
            if (ad != bd)
            {
                return ad < bd ? -1 : 1;
            }
            return string.CompareOrdinal(a.Crop.Name, b.Crop.Name);
        }
        // Stable, as Go's sort.SliceStable.
        return [.. ranking.OrderBy(c => c, Comparer<FieldCandidate>.Create(Compare))];
    }
}
