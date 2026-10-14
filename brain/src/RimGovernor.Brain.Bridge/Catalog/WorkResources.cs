using RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

public sealed partial class DefinitionCatalog
{
    private static readonly string[] NoStuff = [""];
    public EquatableArray<P.WorkBuildingCost> WorkBuildingCosts() => EquatableArray.From(
        _thingDefs.Values.Where(Buildable).OrderBy(d => d.defName, StringComparer.Ordinal).SelectMany(d =>
        {
            var stuffs = AllowedStuffs(d.defName);
            return (stuffs.Count == 0 ? NoStuff : stuffs).Select(stuff => new P.WorkBuildingCost(d.defName, stuff,
                Fact.Known(EquatableArray.From(AdjustedCosts(d.defName, stuff).Select(c => new P.Amount(new(c.Def), c.Units))))));
        }));
}
