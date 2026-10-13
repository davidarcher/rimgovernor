using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>One crop under one site kind with its score breakdown (Go <c>farmselect.Candidate</c>).</summary>
public sealed record FarmCandidate
{
    public string Kind { get; init; } = "";

    public string Crop { get; init; } = "";

    public int Needed { get; init; }

    public int Cells { get; init; }

    public double Score { get; init; }

    public IReadOnlyDictionary<string, double> Terms { get; init; } = new Dictionary<string, double>();

    public string Reason { get; init; } = "";
}

/// <summary>One fields_select row with the candidates it scored (Go <c>farmselect.Selection</c>).</summary>
public sealed record FarmSelection
{
    public string Kind { get; init; } = "";

    public string Crop { get; init; } = "";

    public int Cells { get; init; }

    public int Buildings { get; init; }

    public bool Urgent { get; init; }

    public IReadOnlyList<FarmCandidate> Candidates { get; init; } = [];
}

/// <summary>What a run must show in every selection (Go <c>farmselect.Expectation</c>).</summary>
public sealed record FarmExpectation
{
    public string Kind { get; init; } = "";

    public string Crop { get; init; } = "";

    public int MinCells { get; init; }

    public IReadOnlyList<string> Terms { get; init; } = [];
}

/// <summary>
/// Reads the field planner's site-type selections out of a live service's fields_select rows so a native run can
/// assert which crop and site kind the controller chose and why, independently of whether the zone or building
/// receipts later resolve (Go package <c>farmselect</c>).
/// </summary>
public static class FarmSelect
{
    /// <summary>
    /// Extracts every selection from a service's rows: a fields_select row carries the winner (reason the site kind,
    /// target the crop) and its candidates in attrs. Other rows are ignored.
    /// </summary>
    public static IReadOnlyList<FarmSelection> Parse(IEnumerable<LogRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var selections = new List<FarmSelection>();
        foreach (var row in rows)
        {
            if (row.Kind != "fields_select")
            {
                continue;
            }

            var f = row.Fields();
            var candidates = new List<FarmCandidate>();
            if (f.GetValueOrDefault("candidates") is IEnumerable<object?> items)
            {
                foreach (var item in items)
                {
                    if (item is not IReadOnlyDictionary<string, object?> m)
                    {
                        throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"fields_select row {row.Sequence}: candidate is {item?.GetType().Name ?? "null"}"));
                    }

                    var terms = new Dictionary<string, double>(StringComparer.Ordinal);
                    if (m.GetValueOrDefault("terms") is IReadOnlyDictionary<string, object?> termMap)
                    {
                        foreach (var (name, v) in termMap)
                        {
                            terms[name] = RowValue.Number(v);
                        }
                    }

                    candidates.Add(new FarmCandidate
                    {
                        Kind = RowValue.Text(m.GetValueOrDefault("kind")),
                        Crop = RowValue.Text(m.GetValueOrDefault("crop")),
                        Needed = (int)RowValue.Number(m.GetValueOrDefault("needed")),
                        Cells = (int)RowValue.Number(m.GetValueOrDefault("cells")),
                        Score = RowValue.Number(m.GetValueOrDefault("score")),
                        Terms = terms,
                        Reason = RowValue.Text(m.GetValueOrDefault("reason")),
                    });
                }
            }

            selections.Add(new FarmSelection
            {
                Kind = RowValue.Text(f.GetValueOrDefault("reason")),
                Crop = RowValue.Text(f.GetValueOrDefault("target")),
                Cells = (int)RowValue.Number(f.GetValueOrDefault("cells")),
                Buildings = (int)RowValue.Number(f.GetValueOrDefault("buildings")),
                Urgent = f.GetValueOrDefault("urgent") is true,
                Candidates = candidates,
            });
        }

        return selections;
    }

    /// <summary>
    /// Asserts every selection chose the expected kind (and crop when set), that the winning candidate carries a score
    /// breakdown, and that every unplantable candidate states its reason. Returns the last selection as evidence.
    /// </summary>
    public static FarmSelection Check(IReadOnlyList<FarmSelection> selections, FarmExpectation want)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(want);
        if (selections.Count == 0)
        {
            throw new AcceptanceException("no field selection was recorded; is the field family on?");
        }

        for (var i = 0; i < selections.Count; i++)
        {
            var s = selections[i];
            if (want.Kind.Length > 0 && s.Kind != want.Kind)
            {
                throw new AcceptanceException($"selection {i} chose kind {s.Kind}, want {want.Kind}");
            }

            if (want.Crop.Length > 0 && s.Crop != want.Crop)
            {
                throw new AcceptanceException($"selection {i} chose crop {s.Crop}, want {want.Crop}");
            }

            if (s.Cells < want.MinCells)
            {
                throw new AcceptanceException($"selection {i} planted {s.Cells} cells, want at least {want.MinCells}");
            }

            if (s.Candidates.Count == 0)
            {
                throw new AcceptanceException($"selection {i} has no candidate breakdown");
            }

            var best = s.Candidates[0];
            if (best.Kind != s.Kind || best.Crop != s.Crop || best.Cells == 0 || best.Terms.Count == 0)
            {
                throw new AcceptanceException($"selection {i} winner {best.Kind} {best.Crop} lacks a term breakdown");
            }

            foreach (var name in want.Terms)
            {
                if (!best.Terms.ContainsKey(name))
                {
                    throw new AcceptanceException($"selection {i} lacks {name} term");
                }
            }

            foreach (var c in s.Candidates)
            {
                if (c.Cells == 0 && c.Reason.Length == 0)
                {
                    throw new AcceptanceException($"selection {i} candidate {c.Kind} {c.Crop} is unplantable without a reason");
                }
            }
        }

        return selections[^1];
    }
}
