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
    internal static PrisonerCensus Decode(PbObservations.PopulationSnapshot observed, Pawns pawns, BridgeCatalog? catalog)
    {
        var names = new List<P.OwnedName>(observed.OwnedNames.Count);
        foreach (var n in observed.OwnedNames)
        {
            names.Add(new P.OwnedName(n.PawnId, n.ShortName, n.ThingId));
        }

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
                WillJoinIfRescued = Known(person.WillJoinIfRescued),
                Dead = Known(pawn.Dead),
                Downed = Known(pawn.Downed),
                Hostile = Known(pawn.Hostile),
                Prisoner = Known(pawn.Prisoner),
                Recruitable = Known(person.Recruitable),
                Admitted = Known(person.Admitted),
                Guest = Known(person.Guest),
                Prospect = prospect,
                Luciferium = Known(person.LuciferiumAddicted),
                WearingApparel = Known(person.WearingApparel),
            });

            if (person.Custody is PbObservations.PersonCustody.Guest hosted && !pawn.Dead)
            {
                var (conditions, life) = CareConditionsRules.CareConditions(hosted.Conditions);
                guests.Add(new P.CarePatient { Id = person.Pawn, Care = Known(WireEnums.MedicalCareName(hosted.MedicalCare)), Conditions = conditions, LifeThreatening = life });
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

        var installed = observed.Ideology as PbObservations.ColonyIdeology.Installed;
        var colony = new P.PrisonerColony
        {
            Colonists = colonists,
            BestSkill = bestSkill,
            IdeologyActive = installed is not null,
            ClassicIdeo = installed is { Classic: true },
            Ideo = installed?.PrimaryIdeoId ?? "",
        };
        return new PrisonerCensus
        {
            Context = observed.Context,
            Prisoners = Known(EquatableArray.From(rows)),
            Custody = Known(EquatableArray.From(custody)),
            Colony = Known(colony),
            Guests = Known(EquatableArray.From(guests)),
            Names = Known(EquatableArray.From(names)),
        };
    }

    /// <summary>One colony prisoner's census row.</summary>
    private static P.PrisonerFacts PrisonerRow(PbObservations.PopulationPerson person, PbObservations.PersonCustody.Prisoner held, PbObservations.PawnState pawn, Fact<P.PrisonerProspect> prospect, BridgeCatalog? catalog)
    {
        var (parts, operations) = SurgeryFactsRules.SurgeryFacts(held.Surgery, catalog is null ? null : new DefinitionPartCatalog(catalog));
        var (conditions, life) = CareConditionsRules.CareConditions(held.Conditions);
        var f = new P.PrisonerFacts
        {
            Pawn = person.Pawn,
            Prisoner = Known(true),
            Dead = Known(pawn.Dead),
            Recruitable = Known(person.Recruitable),
            Resistance = Known(held.Resistance),
            HeldTicks = Known(held.HeldTicks),
            Ideo = person.IdeoId ?? "",
            WildMan = person.WildMan,
            Prospect = prospect,
            MissingParts = parts,
            Operations = operations,
            QueuedSurgeries = SurgeryFactsRules.QueuedSurgeries(held.Surgery),
            QueuedRecipes = SurgeryFactsRules.QueuedSurgeryRecipes(held.Surgery) ?? [],
            Kind = pawn.KindDefName,
            CreepJoiner = Unknown<bool>(),
            HarvestGoodwill = Known(held.HarvestGoodwillChange),
            MedicalCare = Known(WireEnums.MedicalCareName(held.MedicalCare)),
            Conditions = conditions,
            LifeThreatening = life,
            Executing = held.Interaction == "Execution",
        };
        if (CreepJoinerFacts.CreepJoinerPawn(pawn).CreepJoiner.TryGetValue(out var joiner))
        {
            f = f with { CreepJoiner = Known(joiner is not null) };
        }
        if (PrisonerCensusRules.TryPrisonerInteractionMode(held.Interaction, out var mode))
        {
            f = f with { CurrentInteraction = Known(mode) };
        }
        return f;
    }

    /// <summary>
    /// A person's biography and health. Each trait's worth is derived from the catalog's rows; without a catalog it
    /// is zero.
    /// </summary>
    private static Fact<P.PrisonerProspect> Prospect(PbObservations.PopulationPerson person, BridgeCatalog? catalog)
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
        return Known(new P.PrisonerProspect
        {
            Age = bio.BiologicalAgeYears,
            Health = person.HealthSummary,
            Incapable = [.. bio.IncapableWorkTypes],
            Skills = skills,
            Traits = traits,
        });
    }
}
