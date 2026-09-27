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
	out, err := decodePopulation(reply.GetObserved())
	return out, raw, err
}

// decodePopulation is a population snapshot's prisoner and custody census.
func decodePopulation(observed *o.PopulationSnapshot) (PrisonerCensus, error) {
	if observed == nil {
		return PrisonerCensus{}, ErrUnavailable
	}
	seen := map[string]bool{}
	rows := make([]policy.PrisonerFacts, 0, len(observed.Persons))
	custody := make([]policy.CustodyFacts, 0, len(observed.Persons))
	for _, person := range observed.Persons {
		if person == nil || person.GetPawn().GetPawn() == nil {
			return PrisonerCensus{}, contract("population person missing pawn identity")
		}
		id := person.GetPawn().GetPawn().GetId()
		if validID(id) != nil || seen[id] {
			return PrisonerCensus{}, contract("invalid or duplicate population person")
		}
		seen[id] = true
		pawn := person.GetPawn()
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
		if person.Admitted != nil {
			custodyRow.Admitted = domain.Known(person.GetAdmitted())
		}
		if person.Guest != nil {
			custodyRow.Guest = domain.Known(person.GetGuest())
		}
		custody = append(custody, custodyRow)
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
		if person.Interaction != nil {
			if mode, ok := prisonerInteractionDefNames[person.GetInteraction()]; ok {
				f.CurrentInteraction = domain.Known(mode)
			}
		}
		rows = append(rows, f)
	}
	return PrisonerCensus{Context: observed.Context, Prisoners: domain.Known(rows), Custody: domain.Known(custody)}, nil
}
