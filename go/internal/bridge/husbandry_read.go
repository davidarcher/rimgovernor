package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// HusbandryTarget is the fresh animal CAS evidence InspectHusbandry needs
// immediately before preview: the entity-level settings token gearEntity's
// ExpectedSnapshotToken carries, the herd-wide census token husbandryCommand's
// ExpectedCensusToken carries, and
// the training/slaughter eligibility facts a selected method is re-validated
// against. The routine candidate search itself is out of scope here, the same
// way RecoveryServiceNative needs no dedicated recovery read to plan a
// method, only to refresh CAS tokens immediately before dispatch.
type HusbandryTarget struct {
	Context              *c.ObservationContext
	Animal               string
	SettingsToken        string
	CensusToken          string
	Dead                 bool
	DeadKnown            bool
	CanTrain             bool
	CanTrainKnown        bool
	Learned              bool
	LearnedKnown         bool
	SafeToSlaughter      bool
	SafeToSlaughterKnown bool
}

// ReadHusbandryTarget reads the whole herd census via the dedicated
// ReadHusbandry observation and extracts one already-selected animal's fresh
// tokens and eligibility facts. It requires a single complete page, like
// ReadTendPawns/ReadPawns; a paginated herd is deferred to whatever candidate
// search eventually drives a routine herd planner.
func (client *Client) ReadHusbandryTarget(ctx context.Context, identity *c.Identity, animal, trainableDef string) (HusbandryTarget, Result, error) {
	if validID(animal) != nil {
		return HusbandryTarget{}, Result{}, contract("invalid husbandry target identity")
	}
	if trainableDef != "" && validID(trainableDef) != nil {
		return HusbandryTarget{}, Result{}, contract("invalid husbandry trainable def")
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.HusbandryReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_husbandry", &o.HusbandryRequest{Scope: &o.ReadScope{ExpectedIdentity: identity}}, reply)
	if err != nil {
		return HusbandryTarget{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return HusbandryTarget{}, raw, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return HusbandryTarget{}, raw, ErrUnavailable
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" {
		return HusbandryTarget{}, raw, ErrUnavailable
	}
	var row *o.HusbandryAnimal
	for _, candidate := range observed.Animals {
		if candidate == nil || candidate.Pawn.GetPawn().GetId() != animal {
			continue
		}
		if row != nil {
			return HusbandryTarget{}, raw, contract("duplicate husbandry animal")
		}
		row = candidate
	}
	if row == nil {
		return HusbandryTarget{}, raw, ErrUnavailable
	}
	settingsToken := row.GetSettingsSnapshot().GetToken()
	censusToken := row.GetCensusSnapshot().GetToken()
	if validID(settingsToken) != nil || validID(censusToken) != nil {
		return HusbandryTarget{}, raw, contract("husbandry target CAS token unavailable")
	}
	out := HusbandryTarget{Context: observed.Context, Animal: animal, SettingsToken: settingsToken, CensusToken: censusToken}
	if row.Pawn.Dead != nil {
		out.DeadKnown, out.Dead = true, row.Pawn.GetDead()
	}
	state := row.GetAnimal()
	if state != nil {
		if state.SafeToSlaughter != nil {
			out.SafeToSlaughterKnown, out.SafeToSlaughter = true, state.GetSafeToSlaughter()
		}
		if trainableDef != "" {
			for _, entry := range state.Training {
				if entry == nil || entry.GetDefName() != trainableDef {
					continue
				}
				if entry.Available != nil {
					out.CanTrainKnown, out.CanTrain = true, entry.GetAvailable()
				}
				if entry.Learned != nil {
					out.LearnedKnown, out.Learned = true, entry.GetLearned()
				}
			}
		}
	}
	return out, raw, nil
}
