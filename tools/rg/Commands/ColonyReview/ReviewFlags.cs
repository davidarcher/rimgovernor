using System.Globalization;

namespace RimGovernor.Tools.Commands.ColonyReview;

/// <summary>
/// The reviewer flags of an hour. They point a reviewer at an hour; they are not pass/fail gates.
/// </summary>
internal static class ReviewFlags
{
    private const double LowMood = 0.25; // near a minor break
    private const double LowFoodDays = 2.0;
    private const int StuckHours = 12; // a concern in deficit this long

    // How long starting supplies may remain forbidden.
    private const int ForbiddenHours = 24;

    /// <summary>The flags for the last row of <paramref name="rows"/>; <paramref name="prev"/> is the row before it, or null.</summary>
    public static List<ReviewFlag> Of(IReadOnlyList<Row> rows, Row? prev)
    {
        var r = rows[^1];
        var c = r.Census;
        var output = new List<ReviewFlag>();
        if (c.Error.Length > 0 || c.Colonists is null)
        {
            return output; // no readings this hour: the card says so, no colony fact to flag
        }
        void Add(string severity, FormattableString text) =>
            output.Add(new ReviewFlag(severity, text.ToString(CultureInfo.InvariantCulture)));

        if (prev is not null && r.Colonists < prev.Colonists)
        {
            Add("bad", $"colonists fell {prev.Colonists} → {r.Colonists}");
        }
        if (c.FoodRunwayDays is { } food && food < LowFoodDays)
        {
            Add("bad", $"food runway {food:F1} days");
        }
        foreach (var p in c.Pawns ?? [])
        {
            if (p.Downed == true)
            {
                Add("bad", $"{p.Label} downed");
            }
            if (p.Mood is { } mood && mood < LowMood)
            {
                Add("warn", $"{p.Label} mood {mood * 100:F0}%");
            }
        }
        foreach (var g in r.Concerns)
        {
            var n = DeficitRun(rows, g.Id);
            if (n == StuckHours)
            {
                Add("warn", $"{g.Id} in deficit {n} hours running ({g.Status})");
            }
        }
        var forbidden = ForbiddenRun(rows);
        if (forbidden == ForbiddenHours)
        {
            Add("warn", $"starting supplies still forbidden after {forbidden} hours");
        }
        return output;
    }

    // How many readings, ending at the last, report starting supplies forbidden.
    private static int ForbiddenRun(IReadOnlyList<Row> rows)
    {
        var n = 0;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].Census.ForbiddenSupplies != true)
            {
                break;
            }
            n++;
        }
        return n;
    }

    // How many rows, ending at the last, hold concern id in deficit.
    private static int DeficitRun(IReadOnlyList<Row> rows, string id)
    {
        var n = 0;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!rows[i].Concerns.Any(g => g.Id == id && g.Need == "unmet"))
            {
                break;
            }
            n++;
        }
        return n;
    }
}
