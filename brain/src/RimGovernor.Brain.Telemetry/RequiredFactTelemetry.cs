namespace RimGovernor.Brain.Telemetry;

/// <summary>
/// The <c>required_fact</c> decision (#3753 D3): a planner run skipped because a fact it needs was not read
/// (Policy's <c>RequiredFactException</c>). Level error, verdict <c>skipped</c>, reason <c>required_fact</c>, target the
/// planner, attr <see cref="FactKey"/>. The colony keeps running; every acceptance case whose log holds one fails, and
/// the xUnit test sink throws on one.
/// </summary>
public static class RequiredFactTelemetry
{
    public const string Kind = "required_fact";

    public const string Reason = "required_fact";

    public const string Verdict = "skipped";

    /// <summary>The attr naming the unread fact.</summary>
    public const string FactKey = "fact";

    /// <summary>The attr naming the throw that found the fact unread (file:line member, #3822).</summary>
    public const string SiteKey = "site";

    /// <summary>The decision of <paramref name="planner"/>'s run skipped for the unread <paramref name="fact"/>, found at <paramref name="site"/>.</summary>
    public static Decision Skipped(string component, string planner, string fact, string site) => new()
    {
        Kind = Kind,
        Component = component,
        Level = TelemetryLevel.Error,
        Verdict = Verdict,
        Reason = Reason,
        Target = planner,
        Attrs = new Dictionary<string, object?> { [FactKey] = fact, [SiteKey] = site },
    };

    /// <summary>The unread fact a sink's record names, or null when the record is no required_fact decision.</summary>
    public static string? FactOf(TelemetryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Kind != Kind)
        {
            return null;
        }
        var key = RowKeys.FieldPrefix + FactKey;
        return record.Attributes.FirstOrDefault(a => a.Key == key).Value as string ?? "";
    }
}
