package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// PrisonerInteractionMode names the native prisoner interaction defName pair
// PopulationTool.cs's legacy home/population write already exposed;
// PopulationPerson.Interaction carries the exact same open string.
const (
	prisonerInteractionRecruitDefName  = "AttemptRecruit"
	prisonerInteractionMaintainDefName = "MaintainOnly"
)

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
		switch row.GetInteraction() {
		case prisonerInteractionRecruitDefName:
			out.CurrentInteractionKnown, out.CurrentInteraction = true, PrisonerInteractionRecruit
		case prisonerInteractionMaintainDefName:
			out.CurrentInteractionKnown, out.CurrentInteraction = true, PrisonerInteractionMaintain
		}
	}
	return out, raw, nil
}
