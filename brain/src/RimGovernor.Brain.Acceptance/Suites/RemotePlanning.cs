using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The suite data files embedded in the assembly (Go <c>cmd/acceptance/suites/*.json</c> and <c>remoteaccept/casetimes.json</c>).</summary>
public static class SuiteFiles
{
    private const string Prefix = "RimGovernor.Brain.Acceptance.Suites.";

    /// <summary>Reads the embedded file (e.g. <c>issue-6-matrix.json</c>, <c>casetimes.json</c>); throws <see cref="AcceptanceException"/> when the assembly does not carry it.</summary>
    public static string ReadText(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + fileName)
            ?? throw new AcceptanceException($"embedded suite file {fileName} is missing from the build");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The embedded file's suite rows (see <see cref="SuiteCommand.ParseSuiteText"/>).</summary>
    public static IReadOnlyList<SuiteEntry> ReadSuite(string fileName) => SuiteCommand.ParseSuiteText(ReadText(fileName));
}

/// <summary>One case skipped from a remote selection and why (Go <c>remoteaccept.SkippedCase</c>).</summary>
public sealed record SkippedCase(string Name, string Reason);

/// <summary>One planned shard: its id and its cases in run order (Go <c>remoteaccept.PlannedShard</c>).</summary>
public sealed record PlannedShard(string Id, IReadOnlyList<string> Cases);

/// <summary>
/// The remote shard planner (Go <c>internal/remoteaccept</c> plan.go and casetimes.go, the part <c>acceptance plan</c>
/// needs): it balances cases over shards by their cost.
/// </summary>
public static class RemotePlanning
{
    /// <summary>Round-robin over generator/consumer groups (the second version of the planner).</summary>
    public const string DependencyAlgorithm = "dependency-round-robin-v2";

    /// <summary>Longest-processing-time-first over the groups' costs.</summary>
    public const string BudgetAlgorithm = "dependency-budget-lpt-v3";

    private const string SortedRoundRobin = "sorted-round-robin-v1";
    private const string MatrixPrefix = "sustained/matrix-";

    private static readonly Lazy<IReadOnlyDictionary<string, long>> _measured = new(LoadMeasured);

    /// <summary>
    /// Plans <paramref name="names"/> (sorted, unique) over at most <paramref name="limit"/> shards. Generated saves stay in
    /// the worker profile that consumes them: each generator and its consumer form one scheduling group, generator first.
    /// Throws <see cref="AcceptanceException"/> on an empty or unsorted selection, an unknown algorithm, a consumer without
    /// its generator, or an invalid cost.
    /// </summary>
    public static IReadOnlyList<PlannedShard> PlanShards(IReadOnlyList<string> names, int limit, string algorithm, IReadOnlyDictionary<string, long> budgets)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(algorithm);
        ArgumentNullException.ThrowIfNull(budgets);
        if (limit < 1 || names.Count == 0 || !IsSorted(names))
        {
            throw new AcceptanceException("empty or unsorted selection or invalid shard limit");
        }

        if (algorithm != SortedRoundRobin && algorithm != DependencyAlgorithm && algorithm != BudgetAlgorithm)
        {
            throw new AcceptanceException($"unknown shard algorithm \"{algorithm}\"");
        }

        var consumers = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            if (i > 0 && string.Equals(name, names[i - 1], StringComparison.Ordinal))
            {
                throw new AcceptanceException($"duplicate selected case {name}");
            }

            if (name.StartsWith(MatrixPrefix, StringComparison.Ordinal) && algorithm != SortedRoundRobin)
            {
                var generator = "tools/variantsavegen-" + name[MatrixPrefix.Length..];
                if (!names.Contains(generator, StringComparer.Ordinal))
                {
                    throw new AcceptanceException($"{name} requires selected generator {generator}");
                }

                consumers[generator] = name;
            }
        }

        var count = Math.Min(names.Count - consumers.Count, limit);
        var shards = new List<string>[count];
        for (var i = 0; i < count; i++)
        {
            shards[i] = [];
        }

        var groups = new List<(List<string> Names, long Budget)>();
        long total = 0;
        foreach (var name in names)
        {
            if (algorithm != SortedRoundRobin && name.StartsWith(MatrixPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var members = new List<string> { name };
            if (consumers.TryGetValue(name, out var consumer))
            {
                members.Add(consumer);
            }

            long groupBudget = 0;
            if (algorithm == BudgetAlgorithm)
            {
                foreach (var member in members)
                {
                    var budget = budgets.GetValueOrDefault(member);
                    if (budget <= 0 || budget > long.MaxValue - total)
                    {
                        throw new AcceptanceException($"invalid or overflowing budget for {member}");
                    }

                    total += budget;
                    groupBudget += budget;
                }
            }

            groups.Add((members, groupBudget));
        }

        if (algorithm == BudgetAlgorithm)
        {
            groups = [.. groups
                .OrderByDescending(g => g.Budget)
                .ThenBy(g => g.Names[0], StringComparer.Ordinal)];
        }

        var loads = new long[count];
        for (var i = 0; i < groups.Count; i++)
        {
            var target = i % count;
            if (algorithm == BudgetAlgorithm)
            {
                target = 0;
                for (var j = 1; j < count; j++)
                {
                    if (loads[j] < loads[target])
                    {
                        target = j;
                    }
                }
            }

            shards[target].AddRange(groups[i].Names);
            loads[target] += groups[i].Budget;
        }

        return [.. shards.Select((cases, i) => new PlannedShard(string.Create(CultureInfo.InvariantCulture, $"s{i + 1}"), cases))];
    }

    /// <summary>A case's planning weight in nanoseconds: its measured wall time when the table has it, else its budget (Go <c>ShardCost</c>).</summary>
    public static long ShardCost(string name, TimeSpan budget) =>
        _measured.Value.TryGetValue(name, out var ms) && ms > 0
            ? ms * 1_000_000
            : budget.Ticks * 100;

    /// <summary>Whether the measured table has a time for <paramref name="name"/> (Go <c>Measured</c>).</summary>
    public static bool Measured(string name) => _measured.Value.TryGetValue(name, out var ms) && ms > 0;

    private static bool IsSorted(IReadOnlyList<string> names)
    {
        for (var i = 1; i < names.Count; i++)
        {
            if (string.CompareOrdinal(names[i - 1], names[i]) > 0)
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, long> LoadMeasured()
    {
        var table = new Dictionary<string, long>(StringComparer.Ordinal);
        if (JsonNode.Parse(SuiteFiles.ReadText("casetimes.json")) is JsonObject o)
        {
            foreach (var (name, value) in o)
            {
                table[name] = (long)value.AsNumber();
            }
        }

        return table;
    }
}
