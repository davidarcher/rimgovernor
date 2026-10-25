using RimGovernor.Brain.Policy;
using TechLevel = RimGovernor.Brain.GameFacts.Defs.TechLevel;

namespace RimGovernor.Brain.Domain.Tests;

internal static class ResearchTestFacts
{
    internal static ResearchFacts Read(string[]? projects = null, string[]? finished = null, string current = "",
        IReadOnlyDictionary<string, ResearchProjectDefinition>? definitions = null,
        IReadOnlyDictionary<string, double>? progress = null,
        IReadOnlyDictionary<string, TechLevel>? levels = null)
    {
        var names = (projects ?? []).Concat(finished ?? []).Concat(current.Length == 0 ? [] : new[] { current })
            .Concat(definitions?.Keys ?? []).Concat(levels?.Keys ?? []).Distinct(StringComparer.Ordinal).ToArray();
        var catalog = names.ToDictionary(name => name, name => (definitions?.GetValueOrDefault(name) ?? new ResearchProjectDefinition()) with
        {
            TechLevel = levels?.GetValueOrDefault(name) ?? definitions?.GetValueOrDefault(name)?.TechLevel ?? TechLevel.Undefined,
        }, StringComparer.Ordinal);
        var snapshot = new RimGovernor.Brain.GameFacts.Observations.ResearchSnapshot
        {
            Context = ColonyFactsCoreTests.Snapshot().Context,
            Finished = [.. finished ?? []],
            Slots = [],
            Projects = [.. names.Select(name => new RimGovernor.Brain.GameFacts.Observations.ResearchProject
            {
                Project = new() { DefName = name }, Current = name == current,
                Finished = (finished ?? []).Contains(name),
                ProgressFraction = (finished ?? []).Contains(name) ? 1 : progress?.GetValueOrDefault(name) ?? 0,
                LockReasons = [],
            })],
        };
        return EconomyRules.ResearchSummary(EconomyRules.ResearchCensusOf(snapshot, catalog, new Dictionary<string, string>()));
    }
}
