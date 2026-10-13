namespace RimGovernor.Brain.BuildingRuntime;

internal static class FieldsSelectLog
{
    /// <summary>
    /// The fields_select row of one site-type selection: target the winning crop, reason the winning site kind,
    /// attrs the winner's cells, buildings, need and urgency, and every candidate with its score, term breakdown
    /// and (for an unplantable one) the reason.
    /// </summary>
    public static Decision FieldsSelectDecision(SiteTypePlan selection)
    {
        var candidates = new List<Dictionary<string, object?>>(selection.Candidates.Count);
        foreach (var c in selection.Candidates)
        {
            var row = new Dictionary<string, object?>
            {
                ["kind"] = Wire.Name(c.Kind),
                ["crop"] = c.Crop.Name,
                ["needed"] = c.Needed,
                ["cells"] = c.Cells,
                ["score"] = c.Score,
            };
            if (c.Terms.Count > 0)
            {
                var terms = new SortedDictionary<string, double>(StringComparer.Ordinal);
                foreach (var t in c.Terms)
                {
                    terms[t.Name] = t.Value;
                }
                row["terms"] = terms;
            }
            if (c.Reason != "")
            {
                row["reason"] = c.Reason;
            }
            candidates.Add(row);
        }
        var cells = selection.Candidates.Count > 0 ? selection.Candidates[0].Cells : 0;
        return new Decision
        {
            Kind = "fields_select",
            Component = "clock-scheduler",
            Verdict = "selected",
            Reason = Wire.Name(selection.Kind),
            Target = selection.Crop.Name,
            Attrs = new Dictionary<string, object?>
            {
                ["cells"] = cells,
                ["buildings"] = selection.Buildings.Count,
                ["needed"] = selection.Needed,
                ["urgent"] = selection.Urgent,
                ["candidates"] = candidates,
            },
        };
    }
}
