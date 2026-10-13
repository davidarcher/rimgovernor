using System.Text.Json.Serialization;

namespace RimGovernor.Tools.Commands.ColonyReview;

// The data of a colony review: what result.json records (Census and its parts), the rows the report derives from it, and
// the run.json summary the site indexes (Go: cmd/colonyreview report.go, score.go, delta.go). Field names are the Go JSON
// tags: run.json files written by the Go tool are read back by the site and the baseline comparison.

/// <summary>One census pawn.</summary>
internal sealed record Pawn
{
    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("downed")]
    public bool? Downed { get; init; }

    [JsonPropertyName("mood")]
    public double? Mood { get; init; }

    [JsonPropertyName("food")]
    public double? Food { get; init; }
}

/// <summary>One role kind's owned zones in a census (general, yard, medicine, meals ...): how many, their cells and the cells holding things.</summary>
internal sealed record Stockpile
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "";

    [JsonPropertyName("zones")]
    public int Zones { get; init; }

    [JsonPropertyName("cells")]
    public int Cells { get; init; }

    [JsonPropertyName("used")]
    public int Used { get; init; }
}

/// <summary>One thought's colony total in the status route's mood ledger.</summary>
internal sealed record MoodSource
{
    [JsonPropertyName("def")]
    public string Def { get; init; } = "";

    [JsonPropertyName("pawns")]
    public int Pawns { get; init; }

    [JsonPropertyName("lost")]
    public double Lost { get; init; }

    [JsonPropertyName("owners")]
    public IReadOnlyList<string>? Owners { get; init; }

    [JsonPropertyName("unverified")]
    public int Unverified { get; init; }
}

/// <summary>How many pawns sit at an expectation level.</summary>
internal sealed record MoodExpectation
{
    [JsonPropertyName("level")]
    public string Level { get; init; } = "";

    [JsonPropertyName("pawns")]
    public int Pawns { get; init; }
}

/// <summary>The colony status route's moodLedger: sources ranked by mood lost, the unowned subset, and what could not be verified.</summary>
internal sealed record MoodLedger
{
    [JsonPropertyName("sources")]
    public IReadOnlyList<MoodSource>? Sources { get; init; }

    [JsonPropertyName("unowned")]
    public IReadOnlyList<MoodSource>? Unowned { get; init; }

    [JsonPropertyName("unknownPawns")]
    public int UnknownPawns { get; init; }

    [JsonPropertyName("expectation")]
    public IReadOnlyList<MoodExpectation>? Expectation { get; init; }
}

/// <summary>The timeline sample's colony block.</summary>
internal sealed record Census
{
    [JsonPropertyName("error")]
    public string Error { get; init; } = "";

    [JsonPropertyName("colonists")]
    public long? Colonists { get; init; }

    [JsonPropertyName("foodRunwayDays")]
    public double? FoodRunwayDays { get; init; }

    [JsonPropertyName("wealthTotal")]
    public double? WealthTotal { get; init; }

    [JsonPropertyName("moodMean")]
    public double? MoodMean { get; init; }

    [JsonPropertyName("downed")]
    public int Downed { get; init; }

    [JsonPropertyName("techTier")]
    public string? TechTier { get; init; }

    /// <summary>Null until a review has filed.</summary>
    [JsonPropertyName("stockpiles")]
    public IReadOnlyList<Stockpile>? Stockpiles { get; init; }

    /// <summary>Null until a review has filed.</summary>
    [JsonPropertyName("forbiddenSupplies")]
    public bool? ForbiddenSupplies { get; init; }

    [JsonPropertyName("pawns")]
    public IReadOnlyList<Pawn>? Pawns { get; init; }

    /// <summary>The status route's ranked mood loss; null until a review has filed.</summary>
    [JsonPropertyName("moodLedger")]
    public MoodLedger? MoodLedger { get; init; }
}

/// <summary>One sampled concern's state.</summary>
internal sealed record Concern(string Id, string Need, string Status);

/// <summary>One thing in an hour a reviewer should look at; severity is "bad" or "warn".</summary>
internal sealed record ReviewFlag(string Severity, string Text);

/// <summary>One hour of the report: a timeline sample and its screenshot.</summary>
internal sealed record Row
{
    public int Tick { get; init; }

    public int Day { get; init; }

    public int Hour { get; init; }

    public string Label { get; init; } = "";

    public Census Census { get; init; } = new();

    public IReadOnlyList<Concern> Concerns { get; init; } = [];

    /// <summary>Concern need transitions since the previous row.</summary>
    public IReadOnlyList<string> Changes { get; init; } = [];

    /// <summary>Label of the timeline sample shown, when not this hour's.</summary>
    public string Sampled { get; init; } = "";

    public string ColonyShot { get; init; } = "";

    public string MapShot { get; init; } = "";

    public IReadOnlyList<ReviewFlag> Flags { get; init; } = [];

    public string Anchor { get; init; } = "";

    /// <summary>The colonist count, 0 when the census has none (Go <c>colonists</c>).</summary>
    public int Colonists => Census.Colonists is { } n ? (int)n : 0;
}

/// <summary>One dimension of the score. Value is in Unit; null means the timeline could not supply it, and then Score is null too and the scalar ignores the component.</summary>
internal sealed record ScoreComponent
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("unit")]
    public string Unit { get; init; } = "";

    [JsonPropertyName("weight")]
    public double Weight { get; init; }

    [JsonPropertyName("value")]
    public double? Value { get; init; }

    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
}

/// <summary>The score vector and its weighted scalar (0-100, null when no component is known).</summary>
internal sealed record ColonyScore
{
    [JsonPropertyName("components")]
    public IReadOnlyList<ScoreComponent> Components { get; init; } = [];

    [JsonPropertyName("scalar")]
    public double? Scalar { get; init; }
}

/// <summary>One component's change from the baseline; both deltas are null unless Status is "ok".</summary>
internal sealed record ComponentDelta
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    /// <summary>In score units (0-1, positive is better).</summary>
    [JsonPropertyName("delta")]
    public double? Delta { get; init; }

    /// <summary>In the component's own unit.</summary>
    [JsonPropertyName("value_delta")]
    public double? ValueDelta { get; init; }
}

/// <summary>HEAD's score minus the baseline's. Scalar is in scalar points (null when either scalar is unknown).</summary>
internal sealed record ScoreDelta
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    /// <summary>The baseline run dir name.</summary>
    [JsonPropertyName("baseline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Baseline { get; init; }

    [JsonPropertyName("baseline_commit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaselineCommit { get; init; }

    [JsonPropertyName("scalar")]
    public double? Scalar { get; init; }

    [JsonPropertyName("components")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ComponentDelta>? Components { get; init; }
}

/// <summary>The run at a glance, saved in run.json for the site index.</summary>
internal sealed record RunSummary
{
    [JsonPropertyName("meta")]
    public IReadOnlyDictionary<string, string> Meta { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("hours")]
    public int Hours { get; init; }

    [JsonPropertyName("first_colonists")]
    public int FirstColonists { get; init; }

    [JsonPropertyName("last_colonists")]
    public int LastColonists { get; init; }

    [JsonPropertyName("min_mood")]
    public double MinMood { get; init; }

    [JsonPropertyName("min_food_days")]
    public double MinFoodDays { get; init; }

    [JsonPropertyName("final_wealth")]
    public double FinalWealth { get; init; }

    [JsonPropertyName("flags")]
    public int Flags { get; init; }

    /// <summary>The last reading's owned stockpile zones per role kind.</summary>
    [JsonPropertyName("zones")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Stockpile>? Zones { get; init; }

    /// <summary>Whether the last reading still had starting supplies forbidden.</summary>
    [JsonPropertyName("supplies_forbidden")]
    public bool SuppliesForbidden { get; init; }

    [JsonPropertyName("score")]
    public ColonyScore Score { get; init; } = new();

    /// <summary>The score against the previous run of the same seed; null when no baselines dir was given.</summary>
    [JsonPropertyName("delta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScoreDelta? Delta { get; init; }

    [JsonPropertyName("thumb")]
    public string Thumb { get; init; } = "";

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    /// <summary>The meta value of a key, empty when absent (a Go map index).</summary>
    public string MetaValue(string key) => Meta.TryGetValue(key, out var v) ? v : "";
}
