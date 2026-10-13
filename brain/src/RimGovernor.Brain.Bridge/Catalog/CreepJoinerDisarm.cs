using RimWorld;
using Verse;
using Domain = RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go creepjoiner_disarm.go: the creepjoiner disarming's def lookups (see P.CreepJoinerDisarm for the game rules). They read
// the defs and name no def.

public sealed partial class DefinitionCatalog
{
    private sealed record BodyPartNode(string Def, IReadOnlyList<string> Groups, int Parent);

    /// <summary>The catalog's disarming input: each pawn kind's race body sites and each install recipe's item price.</summary>
    public P.CreepJoinerDisarm CreepJoinerDisarm() => _disarm.Value;

    private P.CreepJoinerDisarm BuildCreepJoinerDisarm()
    {
        var sites = new Dictionary<string, Domain.EquatableArray<P.DisarmSite>>(StringComparer.Ordinal);
        var byRace = new Dictionary<string, List<P.DisarmSite>>(StringComparer.Ordinal);
        foreach (var kind in Rows<PawnKindDef>().Values)
        {
            var race = kind.race.defName;
            if (!byRace.TryGetValue(race, out var found))
            {
                byRace[race] = found = RaceDisarmSites(race);
            }

            if (found.Count > 0)
            {
                sites[kind.defName] = new([.. found]);
            }
        }

        var install = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var recipe in Rows<RecipeDef>().Values)
        {
            if (recipe.addsHediff is null || !recipe.appliedOnFixedBodyParts.Seq().Any())
            {
                continue;
            }

            // A def with no market value leaves the recipe unpriced: the planner then says so instead of guessing.
            try
            {
                install[recipe.defName] = InstallItemValue(recipe);
            }
            catch (Exception e) when (e is CatalogException or StatException)
            {
            }
        }

        return new P.CreepJoinerDisarm { Sites = sites, InstallValue = install };
    }

    // The market value of the items a recipe consumes by name: each ingredient slot that is exactly one def, at its count.
    // The medicine slot names categories and is priced by the operation.
    private double InstallItemValue(RecipeDef recipe)
    {
        var total = 0.0;
        foreach (var slot in recipe.ingredients.Seq())
        {
            var allowed = slot.filter.AllowedThingDefs.Take(2).ToList();
            if (allowed.Count == 1)
            {
                total += (double)StatValue(allowed[0].defName, string.Empty, StatNames.MarketValue) * slot.GetBaseCount();
            }
        }

        return total;
    }

    // The sites of a race: the race's melee tools other than the one that is always usable each link to a body part
    // group; the site is the lowest part containing every part of that group. A race with no always-usable tool (nothing
    // would be left to fight with) has none.
    private List<P.DisarmSite> RaceDisarmSites(string race)
    {
        var thing = FindThing(race) ?? throw new CatalogException($"pawn kind race {race} has no ThingDef row");
        var groups = new List<string>();
        var always = false;
        foreach (var tool in thing.tools.Seq())
        {
            if (tool.ensureLinkedBodyPartsGroupAlwaysUsable)
            {
                always = true;
            }
            else if (tool.linkedBodyPartsGroup is { } group && !groups.Contains(group.defName))
            {
                groups.Add(group.defName);
            }
        }

        if (!always || groups.Count == 0)
        {
            return [];
        }

        var body = thing.race?.body ?? throw new CatalogException($"race {race} has no BodyDef row");
        var core = body.corePart ?? throw new CatalogException($"body {body.defName} has no core part");
        var parts = new List<BodyPartNode>();
        void Walk(BodyPartRecord record, int parent)
        {
            var index = parts.Count;
            parts.Add(new BodyPartNode(record.def.defName, [.. record.groups.Seq().Select(g => g.defName)], parent));
            foreach (var child in record.parts.Seq())
            {
                Walk(child, index);
            }
        }

        Walk(core, -1);
        var sites = new List<P.DisarmSite>();
        var seen = new HashSet<int>();
        foreach (var group in groups)
        {
            var site = -1;
            for (var i = 0; i < parts.Count; i++)
            {
                if (parts[i].Groups.Contains(group))
                {
                    site = LowestCommon(parts, site, i);
                }
            }

            if (site < 0 || !seen.Add(site))
            {
                continue;
            }

            sites.Add(new P.DisarmSite { Index = site, Part = parts[site].Def, Ancestors = new([.. Ancestors(parts, site)]) });
        }

        return [.. sites.OrderBy(s => s.Index)];
    }

    // The indexes of a part's ancestors, nearest first.
    private static List<int> Ancestors(List<BodyPartNode> parts, int index)
    {
        var chain = new List<int>();
        for (var p = parts[index].Parent; p >= 0; p = parts[p].Parent)
        {
            chain.Add(p);
        }

        return chain;
    }

    // The lowest part that is a or b or an ancestor of both; a negative a is no part yet.
    private static int LowestCommon(List<BodyPartNode> parts, int a, int b)
    {
        if (a < 0)
        {
            return b;
        }

        var chainA = Ancestors(parts, a).Prepend(a).ToList();
        return Ancestors(parts, b).Prepend(b).FirstOrDefault(x => chainA.Contains(x), -1);
    }
}
