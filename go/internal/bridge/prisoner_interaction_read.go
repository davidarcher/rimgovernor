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
// PopulationPerson.Interaction carries to the mode SetPrisonerInteraction
// writes it as; any other current interaction (Execution, DLC modes this
// boundary does not expose) stays unknown rather than misread.
var prisonerInteractionDefNames = map[string]PrisonerInteractionMode{
	"AttemptRecruit":   PrisonerInteractionRecruit,
	"MaintainOnly":     PrisonerInteractionMaintain,
	"ReduceResistance": PrisonerInteractionReduceResistance,
	"Release":          PrisonerInteractionRelease,
	"Enslave":          PrisonerInteractionEnslave,
	"Convert":          PrisonerInteractionConvert,
}

var prisonerInteractionDomain = map[PrisonerInteractionMode]domain.PrisonerInteractionMode{
	PrisonerInteractionRecruit:          domain.PrisonerInteractionRecruit,
	PrisonerInteractionMaintain:         domain.PrisonerInteractionMaintain,
	PrisonerInteractionReduceResistance: domain.PrisonerInteractionReduceResistance,
	PrisonerInteractionRelease:          domain.PrisonerInteractionRelease,
	PrisonerInteractionEnslave:          domain.PrisonerInteractionEnslave,
	PrisonerInteractionConvert:          domain.PrisonerInteractionConvert,
}

// PrisonerTarget is the fresh prisoner CAS evidence InspectPrisonerInteraction
// needs immediately before preview: the per-pawn settings snapshot token
// PawnState.Snapshot carries, and the dead/prisoner/recruitable/current
// interaction eligibility facts a selected interaction is re-validated
// against. The routine candidate search itself is out of scope here, the
// same way ReadHusbandryTarget needs no dedicated herd search to dispatch
// one already-selected animal/method pair.
type PrisonerTarget struct {
	Context                 *c.ObservationContext
	Pawn                    string
	SnapshotToken           string
	Dead                    bool
	DeadKnown               bool
	Prisoner                bool
	PrisonerKnown           bool
	Recruitable             bool
	RecruitableKnown        bool
	CurrentInteraction      PrisonerInteractionMode
	CurrentInteractionKnown bool
}

// ReadPrisonerInteractionTarget reads the whole population census via the
// dedicated ReadPopulation observation and extracts one already-selected
// prisoner's fresh CAS token and eligibility facts. It requires a single
// complete page, like ReadHusbandryTarget; a paginated population is
// deferred to whatever candidate search eventually drives a routine
// Population-* planner.
func (client *Client) ReadPrisonerInteractionTarget(ctx context.Context, identity *c.Identity, pawn string) (PrisonerTarget, Result, error) {
	if validID(pawn) != nil {
		return PrisonerTarget{}, Result{}, contract("invalid prisoner interaction target identity")
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PopulationReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_population", &o.PopulationRequest{Scope: &o.ReadScope{ExpectedIdentity: identity}}, reply)
	if err != nil {
		return PrisonerTarget{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return PrisonerTarget{}, raw, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return PrisonerTarget{}, raw, ErrUnavailable
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" {
		return PrisonerTarget{}, raw, ErrUnavailable
	}
	var row *o.PopulationPerson
	for _, candidate := range observed.Persons {
		if candidate == nil || candidate.Pawn.GetPawn().GetId() != pawn {
			continue
		}
		if row != nil {
			return PrisonerTarget{}, raw, contract("duplicate population person")
		}
		row = candidate
	}
	if row == nil {
		return PrisonerTarget{}, raw, ErrUnavailable
	}
	snapshotToken := row.GetPawn().GetSnapshot().GetToken()
	if validID(snapshotToken) != nil {
		return PrisonerTarget{}, raw, contract("prisoner interaction target CAS token unavailable")
	}
	out := PrisonerTarget{Context: observed.Context, Pawn: pawn, SnapshotToken: snapshotToken}
	state := row.GetPawn()
	if state.Dead != nil {
		out.DeadKnown, out.Dead = true, state.GetDead()
	}
	if state.Prisoner != nil {
		out.PrisonerKnown, out.Prisoner = true, state.GetPrisoner()
	}
	if row.Recruitable != nil {
		out.RecruitableKnown, out.Recruitable = true, row.GetRecruitable()
	}
	if row.Interaction != nil {
		if mode, ok := prisonerInteractionDefNames[row.GetInteraction()]; ok {
			out.CurrentInteractionKnown, out.CurrentInteraction = true, mode
		}
	}
	return out, raw, nil
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
// complete page, like ReadPrisonerInteractionTarget; a paginated population
// is deferred to whatever candidate search eventually needs one.
func (client *Client) ReadRoutinePopulation(ctx context.Context, identity *c.Identity) (PrisonerCensus, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return PrisonerCensus{}, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PopulationReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_population", &o.PopulationRequest{Scope: &o.ReadScope{ExpectedIdentity: identity}}, reply)
	if err != nil {
		return PrisonerCensus{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return PrisonerCensus{}, raw, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return PrisonerCensus{}, raw, ErrUnavailable
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" {
		return PrisonerCensus{}, raw, ErrUnavailable
	}
	seen := map[string]bool{}
	rows := make([]policy.PrisonerFacts, 0, len(observed.Persons))
	custody := make([]policy.CustodyFacts, 0, len(observed.Persons))
	for _, person := range observed.Persons {
		if person == nil || person.GetPawn().GetPawn() == nil {
			return PrisonerCensus{}, raw, contract("population person missing pawn identity")
		}
		id := person.GetPawn().GetPawn().GetId()
		if validID(id) != nil || seen[id] {
			return PrisonerCensus{}, raw, contract("invalid or duplicate population person")
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
		snapshotToken := pawn.GetSnapshot().GetToken()
		if validID(snapshotToken) != nil {
			return PrisonerCensus{}, raw, contract("population person CAS token unavailable")
		}
		f := policy.PrisonerFacts{Pawn: domain.PawnID(id), SnapshotToken: snapshotToken, Prisoner: domain.Known(true)}
		if pawn.Dead != nil {
			f.Dead = domain.Known(pawn.GetDead())
		}
		if person.Recruitable != nil {
			f.Recruitable = domain.Known(person.GetRecruitable())
		}
		if person.Interaction != nil {
			if mode, ok := prisonerInteractionDefNames[person.GetInteraction()]; ok {
				f.CurrentInteraction = domain.Known(prisonerInteractionDomain[mode])
			}
		}
		rows = append(rows, f)
	}
	return PrisonerCensus{Context: observed.Context, Prisoners: domain.Known(rows), Custody: domain.Known(custody)}, raw, nil
}
