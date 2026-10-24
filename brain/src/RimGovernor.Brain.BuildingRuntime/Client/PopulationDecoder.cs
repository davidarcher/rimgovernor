using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Observation;
using static RimGovernor.Brain.Domain.Fact;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// A population snapshot's prisoner and custody census, each person joined to its pawn table row. A person the
/// frame's pawn table does not hold is a contract failure: both sections read the same spawned pawns.
/// </summary>
internal static class PopulationDecoder
{
    internal static P.PrisonerCensus Decode(PbObservations.PopulationSnapshot observed, Pawns pawns, BridgeCatalog? catalog)
    {
        var bestSkill = new Dictionary<string, int>(StringComparer.Ordinal);
        var colonists = 0;
        var rows = new List<P.PrisonerFacts>(observed.Persons.Count);
        var guests = new List<P.CarePatient>();
        var custody = new List<P.CustodyFacts>(observed.Persons.Count);
        foreach (var person in observed.Persons)
        {
            if (!pawns.TryGetValue(person.Pawn.Value, out var pawn))
            {
                throw BridgeContractException.Contract($"population person {person.Pawn.Value} missing from the pawn table");
            }
            var prospect = Prospect(person, catalog);
            custody.Add(new P.CustodyFacts
            {
                Pawn = person.Pawn,
                WillJoinIfRescued = person.WillJoinIfRescued,
                Dead = pawn.Dead,
                Downed = pawn.Downed,
                Hostile = pawn.Hostile,
                Prisoner = pawn.Prisoner,
                Recruitable = person.Recruitable,
                Admitted = person.Admitted,
                Guest = person.Guest,
                Prospect = prospect,
                Luciferium = person.LuciferiumAddicted,
                WearingApparel = person.WearingApparel,
            });

            if (person.Custody is PbObservations.PersonCustody.Guest hosted && !pawn.Dead)
            {
                guests.Add(
                    new P.CarePatient
                    {
                        Id = person.Pawn,
                        Care = WireEnums.RequiredMedicalCare(hosted.MedicalCare),
                        Conditions = hosted.Conditions.Hediffs,
                        LifeThreatening = hosted.Conditions.LifeThreatening,
                    });
            }
            if (person.Admitted && !pawn.Dead)
            {
                colonists++;
                foreach (var s in person.Biography.Skills)
                {
                    if (!s.Disabled && s.Level > bestSkill.GetValueOrDefault(s.DefName))
                    {
                        bestSkill[s.DefName] = s.Level;
                    }
                }
            }
            // prisoners/recruit's census only ever considers colony prisoners.
            if (person.Custody is PbObservations.PersonCustody.Prisoner held)
            {
                rows.Add(PrisonerRow(person, held, pawn, prospect, catalog));
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

    /// <summary>One colony prisoner's census row.</summary>
    private static P.PrisonerFacts PrisonerRow(PbObservations.PopulationPerson person, PbObservations.PersonCustody.Prisoner held, PbObservations.PawnState pawn, P.PrisonerProspect prospect, BridgeCatalog? catalog)
    {
        // The part items of the prisoner's operations are the catalog's; a census without one has none to price.
        var operations = held.Surgery.Operations.Count == 0
            ? []
            : SurgeryFactsRules.CareOperations(
                held.Surgery,
                catalog is null ? throw BridgeContractException.Contract("prisoner operations read without the definition catalog") : new DefinitionPartCatalog(catalog));
        var f = new P.PrisonerFacts
        {
            Pawn = person.Pawn,
            Dead = pawn.Dead,
            Recruitable = person.Recruitable,
            CurrentInteraction = PrisonerCensusRules.PrisonerInteraction(held.Interaction),
            Resistance = held.Resistance,
            HeldTicks = held.HeldTicks,
            Ideo = person.IdeoId ?? "",
            WildMan = person.WildMan,
            Prospect = prospect,
            MissingParts = held.Surgery.MissingParts,
            Operations = operations,
            QueuedSurgeries = held.Surgery.SurgeryBills.Count,
            QueuedRecipes = SurgeryFactsRules.QueuedSurgeryRecipes(held.Surgery),
            Kind = pawn.KindDefName,
            CreepJoiner = CreepJoinerFacts.CreepJoinerPawn(pawn).CreepJoiner is not null,
            HarvestGoodwill = held.HarvestGoodwillChange,
            MedicalCare = WireEnums.RequiredMedicalCare(held.MedicalCare),
            Conditions = held.Conditions.Hediffs,
            LifeThreatening = held.Conditions.LifeThreatening,
            Executing = held.Interaction == "Execution",
        };
        return f;
    }

    /// <summary>
    /// A person's biography and health. Each trait's worth is derived from the catalog's rows; without a catalog it
    /// is zero.
    /// </summary>
    private static P.PrisonerProspect Prospect(PbObservations.PopulationPerson person, BridgeCatalog? catalog)
    {
        var bio = person.Biography;
        var skills = new List<P.PrisonerSkill>(bio.Skills.Count);
        foreach (var s in bio.Skills)
        {
            skills.Add(new P.PrisonerSkill { Name = s.DefName, Level = s.Level, Passion = WireEnums.PassionName(s.Passion), Disabled = s.Disabled });
        }
        var traits = new List<P.PrisonerTrait>(bio.Traits.Count);
        foreach (var t in bio.Traits)
        {
            var trait = new P.PrisonerTrait { Def = t.DefName, Degree = t.Degree };
            if (catalog is not null)
            {
                trait = trait with { Worth = catalog.TraitBalance(trait.Def, trait.Degree).Worth() };
            }
            traits.Add(trait);
        }
        return new P.PrisonerProspect
        {
            Age = bio.BiologicalAgeYears,
            Health = person.HealthSummary,
            Incapable = [.. bio.IncapableWorkTypes],
            Skills = skills,
            Traits = traits,
        };
    }
}
