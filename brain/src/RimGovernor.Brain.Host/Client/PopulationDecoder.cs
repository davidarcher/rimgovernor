using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Observation;
using static RimGovernor.Brain.Domain.Fact;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Host;

/// <summary>
/// Go <c>decodePopulation</c> (prisoner_interaction_read.go): a population snapshot's prisoner and custody census, each
/// person joined to its pawn table row. A person the table does not hold leaves the person facts (prisoners, custody,
/// colony, guests) unknown until a later frame.
/// </summary>
internal static class PopulationDecoder
{
    /// <summary>Go <c>decodePopulation</c>.</summary>
    internal static PrisonerCensus Decode(PbObservations.PopulationSnapshot? observed, Pawns pawns, BridgeCatalog? catalog)
    {
        if (observed is null)
        {
            throw new BridgeUnavailableException("population snapshot missing");
        }
        var names = new List<P.OwnedName>(observed.OwnedNames.Count);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in observed.OwnedNames)
        {
            if (n is null || !BridgeFunctions.IsValidId(n.PawnId ?? "") || !seenNames.Add(n.PawnId ?? "") || n.ShortName is null || n.ThingId is null)
            {
                throw BridgeFunctions.Contract("invalid or duplicate owned name");
            }
            names.Add(new P.OwnedName(new P.PawnId(n.PawnId ?? ""), n.ShortName, n.ThingId.GetValueOrDefault()));
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new PbObservations.PawnState[observed.Persons.Count];
        for (var i = 0; i < observed.Persons.Count; i++)
        {
            var person = observed.Persons[i];
            if (person is null || !BridgeFunctions.UniqueRef(person.Pawn, seen))
            {
                throw BridgeFunctions.Contract("invalid or duplicate population person");
            }
            if (!pawns.TryRow(person.Pawn!, out var row))
            {
                return new PrisonerCensus { Context = observed.Context, Outlook = DecodeOutlook(observed), Names = Known(EquatableArray.From(names)) };
            }
            resolved[i] = row;
        }

        var bestSkill = new Dictionary<string, int>(StringComparer.Ordinal);
        var medicine = new Dictionary<P.PawnId, int>();
        var colonists = 0;
        var rows = new List<P.PrisonerFacts>(observed.Persons.Count);
        var guests = new List<P.CarePatient>();
        var custody = new List<P.CustodyFacts>(observed.Persons.Count);
        for (var i = 0; i < observed.Persons.Count; i++)
        {
            var person = observed.Persons[i];
            var id = person.Pawn!.Id ?? "";
            var pawn = resolved[i];
            var custodyRow = new P.CustodyFacts { Pawn = new P.PawnId(id) };
            if (person.WillJoinIfRescued is { } willJoin)
            {
                custodyRow = custodyRow with { WillJoinIfRescued = Known(willJoin) };
            }
            if (pawn.Dead is { } dead)
            {
                custodyRow = custodyRow with { Dead = Known(dead) };
            }
            if (pawn.Downed is { } downed)
            {
                custodyRow = custodyRow with { Downed = Known(downed) };
            }
            if (pawn.Hostile is { } hostile)
            {
                custodyRow = custodyRow with { Hostile = Known(hostile) };
            }
            if (pawn.Prisoner is { } prisoner)
            {
                custodyRow = custodyRow with { Prisoner = Known(prisoner) };
            }
            if (person.Recruitable is { } recruitable)
            {
                custodyRow = custodyRow with { Recruitable = Known(recruitable) };
            }
            if (person.Admitted is { } admitted)
            {
                custodyRow = custodyRow with { Admitted = Known(admitted) };
            }
            if (person.Guest is { } guest)
            {
                custodyRow = custodyRow with { Guest = Known(guest) };
            }
            custodyRow = custodyRow with { Prospect = Prospect(person, catalog) };
            if (person.LuciferiumAddicted is { } luciferium)
            {
                custodyRow = custodyRow with { Luciferium = Known(luciferium) };
            }
            if (person.WearingApparel is { } wearing)
            {
                custodyRow = custodyRow with { WearingApparel = Known(wearing) };
            }
            custody.Add(custodyRow);

            if (person.Guest.GetValueOrDefault() && !pawn.Prisoner.GetValueOrDefault() && !pawn.Dead.GetValueOrDefault() && person.MedicalCare is not null)
            {
                var (conditions, life) = BridgeFunctions.CareConditions(person.Conditions);
                guests.Add(new P.CarePatient { Id = new P.PawnId(id), Care = Known(BridgeFunctions.MedicalCareName(person.MedicalCare.GetValueOrDefault())), Conditions = conditions, LifeThreatening = life });
            }
            if (person.Admitted.GetValueOrDefault() && !pawn.Dead.GetValueOrDefault())
            {
                colonists++;
                foreach (var s in person.Biography?.Skills ?? [])
                {
                    var name = s.DefName ?? "";
                    var level = s.Level.GetValueOrDefault();
                    if (!s.Disabled.GetValueOrDefault() && level > bestSkill.GetValueOrDefault(name))
                    {
                        bestSkill[name] = level;
                    }
                    if (name == "Medicine" && !s.Disabled.GetValueOrDefault())
                    {
                        medicine[new P.PawnId(id)] = level;
                    }
                }
            }
            if (pawn.Prisoner is null || !pawn.Prisoner.GetValueOrDefault())
            {
                continue; // MaintainPopulation's recruit census only ever considers colony prisoners.
            }
            rows.Add(PrisonerRow(person, pawn, id, custodyRow.Prospect, catalog));
        }

        var colony = new P.PrisonerColony
        {
            Colonists = colonists,
            BestSkill = bestSkill,
            Medicine = medicine,
            IdeologyActive = observed.IdeologyActive.GetValueOrDefault(),
            ClassicIdeo = observed.ClassicIdeoMode.GetValueOrDefault(),
            Ideo = observed.ColonyIdeoId ?? "",
        };
        return new PrisonerCensus
        {
            Context = observed.Context,
            Prisoners = Known(EquatableArray.From(rows)),
            Custody = Known(EquatableArray.From(custody)),
            Colony = Known(colony),
            Guests = Known(EquatableArray.From(guests)),
            Outlook = DecodeOutlook(observed),
            Names = Known(EquatableArray.From(names)),
        };
    }

    /// <summary>One colony prisoner's census row (the body of Go's prisoner loop after the colony prisoner check).</summary>
    private static P.PrisonerFacts PrisonerRow(PbObservations.PopulationPerson person, PbObservations.PawnState pawn, string id, Fact<P.PrisonerProspect> prospect, BridgeCatalog? catalog)
    {
        var f = new P.PrisonerFacts { Pawn = new P.PawnId(id), Prisoner = Known(true) };
        if (pawn.Dead is { } dead)
        {
            f = f with { Dead = Known(dead) };
        }
        if (person.Recruitable is { } recruitable)
        {
            f = f with { Recruitable = Known(recruitable) };
        }
        if (person.Resistance is { } resistance)
        {
            f = f with { Resistance = Known(resistance) };
        }
        if (person.PrisonerTicks is { } ticks)
        {
            f = f with { HeldTicks = Known(ticks) };
        }
        if (person.Will is { } will)
        {
            f = f with { Will = Known(will) };
        }
        f = f with { Ideo = person.IdeoId ?? "", WildMan = person.WildMan.GetValueOrDefault(), Prospect = prospect };
        if (person.Surgery is { } health)
        {
            var (parts, operations) = BridgeFunctions.SurgeryFacts(health, catalog is null ? null : new DefinitionPartCatalog(catalog));
            f = f with
            {
                MissingParts = parts,
                Operations = operations,
                QueuedSurgeries = BridgeFunctions.QueuedSurgeries(health),
                QueuedRecipes = BridgeFunctions.QueuedSurgeryRecipes(health) ?? [],
            };
        }
        f = f with { Kind = pawn.KindDefName ?? "", CreepJoiner = Unknown<bool>() };
        if (BridgeFunctions.CreepJoinerPawn(pawn).CreepJoiner.TryGetValue(out var joiner))
        {
            f = f with { CreepJoiner = Known(joiner is not null) };
        }
        f = f with { Faction = person.Faction?.Id ?? "" };
        if (person.HarvestGoodwillChange is { } goodwill)
        {
            f = f with { HarvestGoodwill = Known(goodwill) };
        }
        if (person.MedicalCare is { } care)
        {
            f = f with { MedicalCare = Known(BridgeFunctions.MedicalCareName(care)) };
        }
        var (conditions, life) = BridgeFunctions.CareConditions(person.Conditions);
        f = f with { Conditions = conditions, LifeThreatening = life, Executing = person.Interaction == "Execution" };
        FacadeNativeClient.PawnReadChecks.ValidatePolicyInputs(person.PolicyInputs);
        f = f with { PolicyInputs = BridgeFunctions.PawnPolicyInputs(person.PolicyInputs) };
        if (person.Withdrawal is { } withdrawal)
        {
            f = f with { Withdrawal = Known(withdrawal) };
        }
        if (person.Interaction is { } interaction && BridgeFunctions.TryPrisonerInteractionMode(interaction, out var mode))
        {
            f = f with { CurrentInteraction = Known(mode) };
        }
        return f;
    }

    /// <summary>
    /// Go <c>prisonerProspect</c>: a person's biography and health; unknown when either is absent. Each trait's worth is
    /// derived from the catalog's rows; without a catalog it is zero.
    /// </summary>
    private static Fact<P.PrisonerProspect> Prospect(PbObservations.PopulationPerson person, BridgeCatalog? catalog)
    {
        var bio = person.Biography;
        if (bio is null || bio.BiologicalAgeYears is null || person.HealthSummary is null)
        {
            return Unknown<P.PrisonerProspect>();
        }
        var skills = new List<P.PrisonerSkill>(bio.Skills.Count);
        foreach (var s in bio.Skills)
        {
            skills.Add(new P.PrisonerSkill { Name = s.DefName ?? "", Level = s.Level.GetValueOrDefault(), Passion = BridgeFunctions.PassionName(s.Passion.GetValueOrDefault()), Disabled = s.Disabled.GetValueOrDefault() });
        }
        var traits = new List<P.PrisonerTrait>(bio.Traits.Count);
        foreach (var t in bio.Traits)
        {
            var trait = new P.PrisonerTrait { Def = t.DefName ?? "", Degree = t.Degree.GetValueOrDefault() };
            if (catalog is not null)
            {
                trait = trait with { Worth = catalog.TraitBalance(trait.Def, trait.Degree).Worth() };
            }
            traits.Add(trait);
        }
        return Known(new P.PrisonerProspect
        {
            Age = bio.BiologicalAgeYears.GetValueOrDefault(),
            Health = person.HealthSummary.GetValueOrDefault(),
            Incapable = [.. bio.IncapableWorkTypes],
            Skills = skills,
            Traits = traits,
        });
    }

    /// <summary>Go <c>decodeOutlook</c>: the snapshot's storyteller fields; an absent field is unknown.</summary>
    private static P.PopulationOutlook DecodeOutlook(PbObservations.PopulationSnapshot observed) => new()
    {
        Intent = observed.PopulationIntent is { } intent ? Known(intent) : default,
        AdjustedPopulation = observed.AdjustedPopulation is { } adjusted ? Known(adjusted) : default,
        DeathOnDownedChance = observed.DeathOnDownedChance is { } death ? Known(death) : default,
        UnrecruitableChance = observed.UnrecruitableChance is { } unrecruitable ? Known(unrecruitable) : default,
    };
}
