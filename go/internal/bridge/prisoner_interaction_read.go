package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// prisonerInteractionDefNames maps the open native defName
// PopulationPerson.Interaction carries to the domain mode; any other current
// interaction (Execution, DLC modes the intent does not expose) stays
// unknown rather than misread.
var prisonerInteractionDefNames = map[string]domain.PrisonerInteractionMode{
	"AttemptRecruit":   domain.PrisonerInteractionRecruit,
	"MaintainOnly":     domain.PrisonerInteractionMaintain,
	"ReduceResistance": domain.PrisonerInteractionReduceResistance,
	"Release":          domain.PrisonerInteractionRelease,
	"Enslave":          domain.PrisonerInteractionEnslave,
	"Convert":          domain.PrisonerInteractionConvert,
}

// PrisonerCensus is one routine review cycle's whole prisoner census, read
// once per cycle so RoutinePrisonerInteractionPlanner can detect
// MaintainPopulation's deficit and select a candidate the same way the
// always-present generic colony census lets RoutineHusbandryPlanner detect
// and select from AnimalState. Unlike that generic census, prisoner facts
// live only on this dedicated rimgovernor/observations_read_population read,
// so a full-list read is issued here instead of piggybacking on ObserveColony.
type PrisonerCensus struct {
	Context   *c.ObservationContext
	Prisoners domain.Fact[[]policy.PrisonerFacts]
	// Custody carries the same read's capture/rescue candidate census: every
	// observed humanlike, not only prisoners. See ReadRoutinePopulation.
	Custody domain.Fact[[]policy.CustodyFacts]
	// Outlook is the snapshot's storyteller population outlook (#1031).
	Outlook policy.PopulationOutlook
	// Colony is the colony side of each prisoner's use: the free
	// colonists' best skills and the snapshot's Ideology facts.
	Colony domain.Fact[policy.PrisonerColony]
	// Guests are the colony guests' care cap inputs (#1301).
	Guests domain.Fact[[]policy.CarePatient]
	// Names is the owned-pawn short-name census (#1310).
	Names domain.Fact[[]policy.OwnedName]
}

// prisonerProspect decodes a person's biography and health; unknown when
// either is absent.
func prisonerProspect(person *o.PopulationPerson) domain.Fact[policy.PrisonerProspect] {
	bio := person.GetBiography()
	if bio == nil || bio.BiologicalAgeYears == nil || person.HealthSummary == nil {
		return domain.Unknown[policy.PrisonerProspect]()
	}
	p := policy.PrisonerProspect{Age: bio.GetBiologicalAgeYears(), Health: person.GetHealthSummary(), Incapable: append([]string{}, bio.IncapableWorkTypes...)}
	for _, s := range bio.Skills {
		p.Skills = append(p.Skills, policy.PrisonerSkill{Name: s.GetDefinition().GetDefName(), Level: int(s.GetLevel()), Passion: PassionName(s.GetPassion()), Disabled: s.GetDisabled()})
	}
	for _, t := range bio.Traits {
		p.Traits = append(p.Traits, policy.PrisonerTrait{Def: t.GetDefName(), Degree: int(t.GetDegree())})
	}
	return domain.Known(p)
}

// ReadRoutinePopulation reads the whole population census and extracts every
// living-or-dead prisoner's recruit/maintain facts. It requires a single
// complete page.
// populationRequest is the exact request the population reads issue, the
// key the bundle seeds its population section under.
func populationRequest(identity *c.Identity) *o.PopulationRequest {
	return &o.PopulationRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
}

func (client *Client) ReadRoutinePopulation(ctx context.Context, identity *c.Identity) (PrisonerCensus, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return PrisonerCensus{}, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PopulationReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_population", populationRequest(identity), reply)
	if err != nil {
		return PrisonerCensus{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return PrisonerCensus{}, raw, err
	}
	pawns, err := client.FramePawns(ctx, identity)
	if err != nil {
		return PrisonerCensus{}, raw, err
	}
	out, err := decodePopulation(reply.GetObserved(), pawns)
	return out, raw, err
}

// decodePopulation is a population snapshot's prisoner and custody census,
// each person joined to its pawn table row (#1343). A person the table
// does not hold leaves the person facts (prisoners, custody, colony,
// guests) unknown until a later frame.
func decodePopulation(observed *o.PopulationSnapshot, pawns Pawns) (PrisonerCensus, error) {
	if observed == nil {
		return PrisonerCensus{}, ErrUnavailable
	}
	names := make([]policy.OwnedName, 0, len(observed.OwnedNames))
	seenNames := map[string]bool{}
	for _, n := range observed.OwnedNames {
		if validID(n.GetPawnId()) != nil || seenNames[n.GetPawnId()] || n.ShortName == nil || n.ThingId == nil {
			return PrisonerCensus{}, contract("invalid or duplicate owned name")
		}
		seenNames[n.GetPawnId()] = true
		names = append(names, policy.OwnedName{Pawn: policy.PawnID(n.GetPawnId()), Short: n.GetShortName(), ThingID: int(n.GetThingId())})
	}
	seen := map[string]bool{}
	resolved := make([]*o.PawnState, len(observed.Persons))
	for i, person := range observed.Persons {
		if person == nil || !pawnRef(person.Pawn, seen) {
			return PrisonerCensus{}, contract("invalid or duplicate population person")
		}
		row, ok := pawns.Row(person.Pawn)
		if !ok {
			return PrisonerCensus{Context: observed.Context, Outlook: decodeOutlook(observed), Names: domain.Known(names)}, nil
		}
		resolved[i] = row
	}
	colony := policy.PrisonerColony{BestSkill: map[string]int{}, Medicine: map[domain.PawnID]int{}, IdeologyActive: observed.GetIdeologyActive(), ClassicIdeo: observed.GetClassicIdeoMode(), Ideo: observed.GetColonyIdeoId(), SlaveryPrecept: observed.GetSlaveryPrecept(), OrganUsePrecept: observed.GetOrganUsePrecept()}
	rows := make([]policy.PrisonerFacts, 0, len(observed.Persons))
	guests := []policy.CarePatient{}
	custody := make([]policy.CustodyFacts, 0, len(observed.Persons))
	for i, person := range observed.Persons {
		id := person.GetPawn().GetId()
		pawn := resolved[i]
		custodyRow := policy.CustodyFacts{Pawn: domain.PawnID(id)}
		if pawn.Dead != nil {
			custodyRow.Dead = domain.Known(pawn.GetDead())
		}
		if pawn.Downed != nil {
			custodyRow.Downed = domain.Known(pawn.GetDowned())
		}
		if pawn.Hostile != nil {
			custodyRow.Hostile = domain.Known(pawn.GetHostile())
		}
		if pawn.Prisoner != nil {
			custodyRow.Prisoner = domain.Known(pawn.GetPrisoner())
		}
		if person.Recruitable != nil {
			custodyRow.Recruitable = domain.Known(person.GetRecruitable())
		}
		if person.Admitted != nil {
			custodyRow.Admitted = domain.Known(person.GetAdmitted())
		}
		if person.Guest != nil {
			custodyRow.Guest = domain.Known(person.GetGuest())
		}
		custodyRow.Prospect = prisonerProspect(person)
		if person.LuciferiumAddicted != nil {
			custodyRow.Luciferium = domain.Known(person.GetLuciferiumAddicted())
		}
		if person.WearingApparel != nil {
			custodyRow.WearingApparel = domain.Known(person.GetWearingApparel())
		}
		custody = append(custody, custodyRow)
		if person.GetGuest() && !pawn.GetPrisoner() && !pawn.GetDead() && person.MedicalCare != nil {
			conditions, life := CareConditions(person.GetConditions())
			guests = append(guests, policy.CarePatient{ID: policy.PawnID(id), Care: domain.Known(MedicalCareName(person.GetMedicalCare())), Conditions: conditions, LifeThreatening: life})
		}
		if person.GetAdmitted() && !pawn.GetDead() {
			colony.Colonists++
			for _, s := range person.GetBiography().GetSkills() {
				if name := s.GetDefinition().GetDefName(); !s.GetDisabled() && int(s.GetLevel()) > colony.BestSkill[name] {
					colony.BestSkill[name] = int(s.GetLevel())
				}
				if s.GetDefinition().GetDefName() == "Medicine" && !s.GetDisabled() {
					colony.Medicine[domain.PawnID(id)] = int(s.GetLevel())
				}
			}
		}
		if pawn.Prisoner == nil || !pawn.GetPrisoner() {
			continue // MaintainPopulation's recruit census only ever considers colony prisoners.
		}
		f := policy.PrisonerFacts{Pawn: domain.PawnID(id), Prisoner: domain.Known(true)}
		if pawn.Dead != nil {
			f.Dead = domain.Known(pawn.GetDead())
		}
		if person.Recruitable != nil {
			f.Recruitable = domain.Known(person.GetRecruitable())
		}
		if person.Resistance != nil {
			f.Resistance = domain.Known(person.GetResistance())
		}
		if person.PrisonerTicks != nil {
			f.HeldTicks = domain.Known(person.GetPrisonerTicks())
		}
		if person.Will != nil {
			f.Will = domain.Known(person.GetWill())
		}
		f.Ideo, f.WildMan, f.Prospect = person.GetIdeoId(), person.GetWildMan(), prisonerProspect(person)
		if h := person.GetSurgery(); h != nil {
			f.MissingParts, f.Operations = SurgeryFacts(h)
			f.QueuedSurgeries = QueuedSurgeries(h)
			f.QueuedRecipes = QueuedSurgeryRecipes(h)
		}
		f.Faction = person.GetFaction().GetId()
		if person.HarvestGoodwillChange != nil {
			f.HarvestGoodwill = domain.Known(int(person.GetHarvestGoodwillChange()))
		}
		if person.MedicalCare != nil {
			f.MedicalCare = domain.Known(MedicalCareName(person.GetMedicalCare()))
		}
		f.Conditions, f.LifeThreatening = CareConditions(person.GetConditions())
		f.Executing = person.GetInteraction() == "Execution"
		if person.Withdrawal != nil {
			f.Withdrawal = domain.Known(person.GetWithdrawal())
		}
		if person.Interaction != nil {
			if mode, ok := prisonerInteractionDefNames[person.GetInteraction()]; ok {
				f.CurrentInteraction = domain.Known(mode)
			}
		}
		rows = append(rows, f)
	}
	return PrisonerCensus{Context: observed.Context, Prisoners: domain.Known(rows), Custody: domain.Known(custody), Colony: domain.Known(colony), Guests: domain.Known(guests), Outlook: decodeOutlook(observed), Names: domain.Known(names)}, nil
}

// decodeOutlook reads the snapshot's storyteller fields; an absent field is Unknown.
func decodeOutlook(observed *o.PopulationSnapshot) policy.PopulationOutlook {
	var out policy.PopulationOutlook
	if observed.PopulationIntent != nil {
		out.Intent = domain.Known(observed.GetPopulationIntent())
	}
	if observed.AdjustedPopulation != nil {
		out.AdjustedPopulation = domain.Known(observed.GetAdjustedPopulation())
	}
	if observed.DeathOnDownedChance != nil {
		out.DeathOnDownedChance = domain.Known(observed.GetDeathOnDownedChance())
	}
	if observed.UnrecruitableChance != nil {
		out.UnrecruitableChance = domain.Known(observed.GetUnrecruitableChance())
	}
	return out
}
