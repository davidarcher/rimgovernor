package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"testing"
)

func crashOffer() JoinerOffer {
	return empireOffer("Quest_Crash", func(v *JoinerOffer) {
		v.ScriptDef = "ShuttleCrash_Rescue"
		v.Profile = domain.Known(QuestFamilyForRoot(v.ScriptDef))
		v.ExpiresInTicks = domain.Known(int64(24000))
		v.ThreatPoints = domain.Known(300.0)
		v.Objectives = []QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_LOAD_NAMED_PAWNS, PawnIDs: []domain.PawnID{"civilian1", "civilian2", "commander", "guard1", "guard2"}}}
	})
}

func TestShuttleCrashAdmissionWithinShortWindow(t *testing.T) {
	offer := crashOffer()
	facts := questTestFacts(domain.Known([]JoinerOffer{offer}))
	// Foreign rescue passengers do not reserve five colony workers.
	if choice := SelectQuestMethod(facts); choice.Quest != offer.Quest {
		t.Fatal(choice, QuestSkips(facts))
	}
	for _, remaining := range []int64{1, 24000, 36000} {
		offer.ExpiresInTicks = domain.Known(remaining)
		facts.QuestOffers = domain.Known([]JoinerOffer{offer})
		if choice := SelectQuestMethod(facts); choice.Quest != offer.Quest {
			t.Fatal(remaining, choice)
		}
	}
	offer.ExpiresInTicks = domain.Known(int64(0))
	facts.QuestOffers = domain.Known([]JoinerOffer{offer})
	if choice := SelectQuestMethod(facts); choice.Reason != QuestNoOffer {
		t.Fatal(choice)
	}
	if skips := QuestSkips(facts); len(skips) != 1 || skips[0].Reason != "expired" {
		t.Fatal(skips)
	}
}

func TestShuttleCrashRefusesShortDefenseAndUnknownWindow(t *testing.T) {
	offer := crashOffer()
	facts := questTestFacts(domain.Known([]JoinerOffer{offer}))
	facts.DefenseCapacity = domain.Known(299.0)
	if skips := QuestSkips(facts); len(skips) != 1 || skips[0].Reason != "defense_capacity" {
		t.Fatal(skips)
	}
	facts.DefenseCapacity = domain.Known(300.0)
	facts.QuestColonyCalm = domain.Known(false)
	if skips := QuestSkips(facts); len(skips) != 1 || skips[0].Reason != "colony_busy" {
		t.Fatal(skips)
	}
	facts.QuestColonyCalm = domain.Known(true)
	offer.ExpiresInTicks = domain.Unknown[int64]()
	facts.QuestOffers = domain.Known([]JoinerOffer{offer})
	if skips := QuestSkips(facts); len(skips) != 1 || skips[0].Reason != "expiry_unknown" {
		t.Fatal(skips)
	}
}

func TestShuttleCrashDefendThenBoardEveryoneBeforeLaunch(t *testing.T) {
	offer := crashOffer()
	offer.State = "Ongoing"
	offer.Shuttles = []QuestShuttleState{{PawnIDs: []domain.PawnID{"civilian1", "civilian2", "commander"}, LoadedPawnIDs: []domain.PawnID{"civilian1"}, AllRequiredLoaded: domain.Known(false), ManualLaunchAvailable: domain.Known(true), AutoloadAvailable: domain.Known(true), Autoload: domain.Known(false)}}
	facts := questTestFacts(domain.Known([]JoinerOffer{offer}))
	facts.QuestColonyCalm = domain.Known(false)
	work, err := SelectHospitalityWork(facts, 1)
	if err != nil || !work.Waiting || work.Shuttle != nil {
		t.Fatal(work, err)
	}
	facts.QuestColonyCalm = domain.Known(true)
	work, err = SelectHospitalityWork(facts, 1)
	if err != nil || work.Shuttle == nil || work.Shuttle.Loading() != domain.ShuttleAutoload || work.Shuttle.Launch() {
		t.Fatal(work, err)
	}
	offer.Shuttles[0].Autoload = domain.Known(true)
	facts.QuestOffers = domain.Known([]JoinerOffer{offer})
	work, err = SelectHospitalityWork(facts, 1)
	if err != nil || !work.Waiting || work.Shuttle != nil {
		t.Fatal(work, err)
	}
	offer.Shuttles[0].AllRequiredLoaded = domain.Known(true)
	offer.Shuttles[0].LoadedPawnIDs = offer.Shuttles[0].PawnIDs
	facts.QuestOffers = domain.Known([]JoinerOffer{offer})
	work, err = SelectHospitalityWork(facts, 1)
	if err != nil || work.Shuttle == nil || !work.Shuttle.Launch() {
		t.Fatal(work, err)
	}
	// Vanilla crash rescue departs automatically once satisfied; no manual
	// send command is invented when the native shuttle does not offer one.
	offer.Shuttles[0].ManualLaunchAvailable = domain.Known(false)
	facts.QuestOffers = domain.Known([]JoinerOffer{offer})
	work, err = SelectHospitalityWork(facts, 1)
	if err != nil || !work.Waiting || work.Shuttle != nil {
		t.Fatal(work, err)
	}
	if deficit, known := QuestDeficit(facts).Value(); !known || !deficit {
		t.Fatal(deficit, known)
	}
	offer.State = "EndedFailed"
	facts.QuestOffers = domain.Known([]JoinerOffer{offer})
	work, err = SelectHospitalityWork(facts, 1)
	if err != nil || work.Quest != "" {
		t.Fatal(work, err)
	}
}
