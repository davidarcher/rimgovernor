using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One <c>acceptance fixture</c> call (also profile-capture's): the op, its arguments and evidence directory, and what the
/// call established before it ended. <see cref="ToJson"/> is the <c>-json</c> output: the facts the call reached, in order,
/// then the error when it failed.
/// </summary>
public sealed class FixtureRun
{
    public FixtureRun(string op, JsonObject args, string output)
    {
        ArgumentNullException.ThrowIfNull(args);
        Op = op;
        Args = args;
        Output = output;
    }

    public string Op { get; }

    public JsonObject Args { get; }

    /// <summary>The call's evidence directory.</summary>
    public string Output { get; }

    /// <summary>Whether the session opened, so <see cref="Start"/> (the start row, possibly null) is reported.</summary>
    public bool Started { get; private set; }

    public JsonNode? Start { get; private set; }

    /// <summary>The op's decoded reply; null until native answered.</summary>
    public JsonObject? Response { get; set; }

    /// <summary>Whether native accepted the op; null until it answered.</summary>
    public bool? Success { get; set; }

    /// <summary>The world after the op; null until it was read.</summary>
    public FixtureCensus? Census { get; set; }

    public string? Error { get; set; }

    /// <summary>Records the session's start row once the world is loaded.</summary>
    public void RecordStart(JsonNode? start)
    {
        Started = true;
        Start = start;
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            { "op", Op },
            { "args", Args.DeepClone() },
            { "output", Output },
        };
        if (Started)
        {
            o.Add("start", Start?.DeepClone());
        }

        return o.AddIfPresent("response", Response?.DeepClone())
            .AddIfPresent("success", Success)
            .AddIfPresent("census", Census?.ToJson())
            .AddIfPresent("error", Error);
    }
}

/// <summary>The census <c>acceptance fixture</c> prints after the op: the paused world's identity, authority, drafts and stock.</summary>
public sealed record FixtureCensus(
    string ColonyId,
    string LoadToken,
    double Tick,
    bool Paused,
    bool AuthorityActive,
    int Drafted,
    IReadOnlyList<KeyValuePair<string, StockCounts>> Stock)
{
    /// <summary>The census of an observed reset state, stock in name order.</summary>
    public static FixtureCensus Of(ResetState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new FixtureCensus(
            state.ColonyId,
            state.LoadToken,
            state.Tick,
            state.Paused,
            state.AuthorityActive,
            state.Drafted,
            [.. state.Stock.OrderBy(p => p.Key, StringComparer.Ordinal)]);
    }

    public JsonObject ToJson()
    {
        var stock = new JsonObject();
        foreach (var (name, counts) in Stock)
        {
            stock.Add(name, new JsonObject { { "Units", counts.Units }, { "Forbidden", counts.Forbidden } });
        }

        return new JsonObject
        {
            { "colonyId", ColonyId },
            { "loadToken", LoadToken },
            { "tick", Tick },
            { "paused", Paused },
            { "authorityActive", AuthorityActive },
            { "drafted", Drafted },
            { "stock", stock },
        };
    }
}
