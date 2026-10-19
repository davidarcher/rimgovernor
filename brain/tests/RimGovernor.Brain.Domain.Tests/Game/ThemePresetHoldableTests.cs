using System.Reflection;
using System.Xml.Linq;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3511: a preset's seeded meme and precept goals must be holdable together by the design it adopts, over the installed
// game's own Def XML. A preset meme rules a precept out when the precept names it in conflictingMemes, or when one of the
// meme's requireOne groups covers the precept's issue without listing it (Nudism requires Nudity_*_Mandatory or
// *_CoveringAnythingButGroinDisapproved, so Nudity_*_NoRules can never be held beside it). A precept with requiredMemes
// needs one the preset adopts, since the starting design takes its memes from the preset.
public sealed class ThemePresetHoldableTests
{
    private sealed record Precept(string Issue, IReadOnlyList<string> Required, IReadOnlyList<string> Conflicting);

    [Fact]
    public void EveryPresetsPreceptGoalsAreHoldableUnderItsOwnMemes()
    {
        var data = Path.Combine(
            typeof(ThemePresetHoldableTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RimWorldDir").Value!,
            "Data");
        Assert.SkipUnless(Directory.Exists(Path.Combine(data, "Ideology")), $"{data} has no Ideology data");
        var precepts = Resolved(data, "PreceptDef").ToDictionary(
            d => d.Key,
            d => new Precept(Text(d.Value, "issue"), Items(d.Value, "requiredMemes"), Items(d.Value, "conflictingMemes")),
            StringComparer.Ordinal);
        var requireOne = Resolved(data, "MemeDef").ToDictionary(
            d => d.Key,
            d => (IReadOnlyList<IReadOnlyList<string>>)[.. Field(d.Value, "requireOne")?.Elements("li").Select(g => (IReadOnlyList<string>)[.. g.Elements("li").Select(li => li.Value.Trim())]) ?? []],
            StringComparer.Ordinal);

        var faults = new List<string>();
        foreach (var preset in ThemePresetCatalog.All)
        {
            var goals = preset.Members.Select(m => m.Goal).OfType<GoalRecord>().ToList();
            var memes = goals.Where(g => g.Kind == GoalKind.Meme).Select(g => g.Target).ToList();
            foreach (var name in goals.Where(g => g.Kind == GoalKind.Precept).Select(g => g.Target))
            {
                if (!precepts.TryGetValue(name, out var precept))
                {
                    faults.Add($"{preset.Id}: {name} is no PreceptDef");
                    continue;
                }

                faults.AddRange(precept.Conflicting.Intersect(memes).Select(m => $"{preset.Id}: {m} conflicts with {name}"));
                if (precept.Required.Count > 0 && !precept.Required.Intersect(memes).Any())
                {
                    faults.Add($"{preset.Id}: {name} requires one of [{string.Join(' ', precept.Required)}]");
                }

                foreach (var meme in memes.Where(requireOne.ContainsKey))
                {
                    faults.AddRange(requireOne[meme]
                        .Where(group => !group.Contains(name) && group.Any(p => precepts.TryGetValue(p, out var other) && other.Issue == precept.Issue))
                        .Select(group => $"{preset.Id}: {meme} requires one of [{string.Join(' ', group)}] on {name}'s issue"));
                }
            }
        }

        Assert.Empty(faults);
    }

    // #3511: the catalog marks a meme unsupported when a role or an obliged ritual needs it, so no starting design could
    // hold a preset naming it. Over the installed defs, Cannibal unlocks only the anytime gladiator duel; Transhumanist,
    // AnimalPersonhood, NaturePrimacy and Rancher unlock specialist roles, so this checks the naked-cannibals preset.
    [Fact]
    public void TheNakedCannibalsMemesUnlockNoPreceptTheDesignCannotValue()
    {
        var data = Path.Combine(
            typeof(ThemePresetHoldableTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RimWorldDir").Value!,
            "Data");
        Assert.SkipUnless(Directory.Exists(Path.Combine(data, "Ideology")), $"{data} has no Ideology data");
        var patterns = Resolved(data, "RitualPatternDef").ToDictionary(
            d => d.Key,
            d => new RimWorld.RitualPatternDef
            {
                defName = d.Key,
                ritualObligationTriggers = [.. Field(d.Value, "ritualObligationTriggers")?.Elements("li").Select(_ => new RimWorld.RitualObligationTrigger_BlindingProperties()) ?? []],
            },
            StringComparer.Ordinal);
        var memes = new Dictionary<string, RimWorld.MemeDef>(StringComparer.Ordinal);
        RimWorld.MemeDef Meme(string name) => memes.TryGetValue(name, out var m) ? m : memes[name] = new RimWorld.MemeDef { defName = name };
        var precepts = Resolved(data, "PreceptDef").Select(d => new RimWorld.PreceptDef
        {
            defName = d.Key,
            preceptClass = Text(d.Value, "preceptClass") is { Length: > 0 } kind
                ? typeof(RimWorld.Precept).Assembly.GetType("RimWorld." + kind, throwOnError: true)!
                : typeof(RimWorld.Precept),
            requiredMemes = [.. Items(d.Value, "requiredMemes").Select(Meme)],
            ritualPatternBase = patterns.GetValueOrDefault(Text(d.Value, "ritualPatternBase")),
        }).ToList();

        var preset = ThemePresetCatalog.All.Single(p => p.Id == "naked-cannibals");
        var named = preset.Members.Select(m => m.Goal).OfType<GoalRecord>().Where(g => g.Kind == GoalKind.Meme).Select(g => g.Target).ToList();

        Assert.Equal(["Nudism", "Cannibal"], named);
        Assert.DoesNotContain(named, m => DefinitionCatalog.UnlocksUnvaluedPrecept(Meme(m), precepts));
        // The rule still bites on the installed defs: a specialist role needs Rancher, an obliged blinding ceremony Blindsight.
        Assert.True(DefinitionCatalog.UnlocksUnvaluedPrecept(Meme("Rancher"), precepts));
        Assert.Contains(precepts, p => p.defName == "BlindingCeremony" && p.ritualPatternBase?.ritualObligationTriggers.Count > 0);
    }

    // Every non-abstract def of the element type under data/<pack>/Defs, with its ParentName chain (by the Name attribute).
    private static Dictionary<string, List<XElement>> Resolved(string data, string type)
    {
        var all = Directory.EnumerateDirectories(data)
            .Select(pack => Path.Combine(pack, "Defs"))
            .Where(Directory.Exists)
            .SelectMany(defs => Directory.EnumerateFiles(defs, "*.xml", SearchOption.AllDirectories))
            .SelectMany(path => XDocument.Load(path).Root?.Elements(type) ?? [])
            .ToList();
        var named = all.Where(e => e.Attribute("Name") is not null).GroupBy(e => e.Attribute("Name")!.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var result = new Dictionary<string, List<XElement>>(StringComparer.Ordinal);
        foreach (var def in all.Where(e => e.Element("defName") is not null && e.Attribute("Abstract")?.Value != "True"))
        {
            var chain = new List<XElement>();
            for (var e = def; e is not null; e = e.Attribute("ParentName") is { } parent && named.TryGetValue(parent.Value, out var p) ? p : null)
            {
                chain.Add(e);
            }

            result[def.Element("defName")!.Value.Trim()] = chain;
        }

        return result;
    }

    // The nearest definer in the chain wins; a list is replaced whole.
    private static XElement? Field(List<XElement> chain, string name) => chain.Select(e => e.Element(name)).FirstOrDefault(f => f is not null);

    private static string Text(List<XElement> chain, string name) => Field(chain, name)?.Value.Trim() ?? "";

    private static IReadOnlyList<string> Items(List<XElement> chain, string name) => [.. Field(chain, name)?.Elements("li").Select(li => li.Value.Trim()) ?? []];
}
