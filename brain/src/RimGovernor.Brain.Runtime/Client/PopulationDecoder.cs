using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

/// <summary>Joins the population section to the same frame's canonical pawn rows.</summary>
internal static class PopulationDecoder
{
    internal static P.PrisonerCensus Decode(RimGovernor.Brain.GameFacts.Observations.PopulationSnapshot observed, Pawns pawns, BridgeCatalog? catalog)
    {
        var bestSkill = new Dictionary<string, int>(StringComparer.Ordinal);
        var colonists = 0;
        var rows = new List<P.PrisonerFacts>();
        var guests = new List<P.CarePatient>();
        var custody = new List<P.CustodyFacts>();
        foreach (var person in observed.Persons)
        {
            if (!pawns.TryGetValue(person.Pawn.Value, out var pawn))
                throw BridgeContractException.Contract($"population person {person.Pawn.Value} missing from the pawn table");
            var prospect = Prospect(pawn, catalog);
            custody.Add(new P.CustodyFacts { Person = person, Observed = pawn, Prospect = prospect });
            if (person.Custody is RimGovernor.Brain.GameFacts.Observations.PersonCustody.Guest && !pawn.Dead)
                guests.Add(new P.CarePatient { Person = person, Observed = pawn });
            if (person.Admitted && !pawn.Dead)
            {
                colonists++;
                foreach (var skill in pawn.Biography.Skills)
                    if (!skill.Disabled && skill.Level > bestSkill.GetValueOrDefault(skill.DefName))
                        bestSkill[skill.DefName] = skill.Level;
            }
            if (person.Custody is RimGovernor.Brain.GameFacts.Observations.PersonCustody.Prisoner held)
            {
                var surgery = pawn.Health.Surgery ?? throw BridgeContractException.Contract("colony prisoner without surgery facts");
                var operations = surgery.Operations.Count == 0 ? [] : SurgeryFactsRules.CareOperations(
                    surgery, catalog is null
                        ? throw BridgeContractException.Contract("prisoner operations read without the definition catalog")
                        : new DefinitionPartCatalog(catalog));
                rows.Add(new P.PrisonerFacts
                {
                    Person = person,
                    Observed = pawn,
                    Held = held,
                    CurrentInteraction = PrisonerCensusRules.PrisonerInteraction(held.Interaction),
                    Prospect = prospect,
                    Operations = operations,
                });
            }
        }
        return new P.PrisonerCensus
        {
            Prisoners = EquatableArray.From(rows),
            Custody = EquatableArray.From(custody),
            Colony = new P.PrisonerColony { Colonists = colonists, BestSkill = bestSkill, Ideology = observed.Ideology },
            Guests = EquatableArray.From(guests),
            Names = observed.OwnedNames,
        };
    }

    private static P.PrisonerProspect Prospect(RimGovernor.Brain.GameFacts.Observations.PawnState pawn, BridgeCatalog? catalog)
    {
        var traits = new List<P.PrisonerTrait>(pawn.Biography.Traits.Count);
        foreach (var trait in pawn.Biography.Traits)
        {
            if (catalog is null)
                throw BridgeContractException.Contract("population traits read without the definition catalog");
            traits.Add(new P.PrisonerTrait(catalog.TraitBalance(trait.DefName, trait.Degree).Worth()));
        }
        return new P.PrisonerProspect { Observed = pawn, Traits = traits };
    }
}
