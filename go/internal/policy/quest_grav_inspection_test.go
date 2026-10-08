package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"testing"
)

func TestGravInspectionAcceptAndNativeCompletionLifecycle(t *testing.T) {
	offer := JoinerOffer{Quest: "signal", State: "NotYetAccepted", Profile: domain.Known(QuestFamilyForRoot("MechanoidSignal")), CanAccept: true, ChoiceCount: 1, RewardChoiceParts: domain.Known(int32(1))}
	f := RoundsFacts{QuestOffers: domain.Known([]JoinerOffer{offer})}
	if choice := SelectQuestMethod(f); choice.Quest != offer.Quest {
		t.Fatal("signal not accepted", choice)
	}
	offer.State = "Ongoing"
	offer.Objectives = []QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_INSPECT_GRAV_ENGINE}}
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	if work, err := SelectQuestGravInspection(f, 1); err != nil || !work.Waiting || !GravInspectionDeficit(f) {
		t.Fatal("unknown pending chunks stalled", work, err)
	}
	engine := QuestGravEngine{ID: "engine", Map: 1, Spawned: domain.Known(false), Inspected: domain.Known(false), EligiblePawns: []domain.PawnID{"b", "a"}}
	offer.State = "Ongoing"
	offer.Objectives = []QuestObjective{{GravEngine: domain.Known(engine)}}
	check := func() QuestGravInspectionWork {
		t.Helper()
		f.QuestOffers = domain.Known([]JoinerOffer{offer})
		work, err := SelectQuestGravInspection(f, 1)
		if err != nil {
			t.Fatal(err)
		}
		return work
	}
	if work := check(); !work.Waiting || work.Service != nil {
		t.Fatal("did not await chunks", work)
	}
	engine.Spawned = domain.Known(true)
	offer.Objectives[0].GravEngine = domain.Known(engine)
	if work := check(); work.Service == nil || work.Service.Pawn() != "a" || work.Service.Thing() != "engine" || work.Service.Method() != domain.RecoveryServiceInspectGravEngine {
		t.Fatal(work)
	}
	engine.InspectingPawnIDs = []domain.PawnID{"a"}
	offer.Objectives[0].GravEngine = domain.Known(engine)
	if work := check(); !work.Waiting || work.Service != nil {
		t.Fatal("reissued running inspection", work)
	}
	engine.Inspected = domain.Known(true)
	offer.Objectives[0].GravEngine = domain.Known(engine)
	if work := check(); work.Quest != "" || GravInspectionDeficit(f) {
		t.Fatal("native completion ignored", work)
	}
}

func TestGravInspectionUnknownOffMapAndNoInspectorHold(t *testing.T) {
	engine := QuestGravEngine{ID: "engine", Map: 2, Spawned: domain.Known(true), Inspected: domain.Known(false)}
	offer := JoinerOffer{Quest: "q", State: "Ongoing", Objectives: []QuestObjective{{GravEngine: domain.Known(engine)}}}
	f := RoundsFacts{QuestOffers: domain.Known([]JoinerOffer{offer})}
	work, err := SelectQuestGravInspection(f, 1)
	if err != nil || work.Reason != "off_map" || work.Service != nil {
		t.Fatal(work, err)
	}
	engine.Map = 1
	offer.Objectives[0].GravEngine = domain.Known(engine)
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	work, err = SelectQuestGravInspection(f, 1)
	if err != nil || work.Reason != "inspector_unavailable" {
		t.Fatal(work, err)
	}
	engine.Inspected = domain.Unknown[bool]()
	offer.Objectives[0].GravEngine = domain.Known(engine)
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	work, err = SelectQuestGravInspection(f, 1)
	if err != nil || work.Reason != "inspection_unknown" {
		t.Fatal(work, err)
	}
}
