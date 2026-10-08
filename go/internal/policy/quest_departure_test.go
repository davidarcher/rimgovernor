package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"reflect"
	"testing"
)

func departureFixture(family QuestFamily, count int64) (JoinerOffer, RoundsFacts) {
	offer := JoinerOffer{Quest: "quest", State: "Ongoing", Profile: domain.Known(QuestProfile{Family: family}), Objectives: []QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_LOAD_PAWNS, Count: domain.Known(count)}}}
	rows := []QuestDeparturePawn{}
	work := []PawnWorkAssignment{}
	for _, id := range []PawnID{"a", "b", "c"} {
		rows = append(rows, QuestDeparturePawn{ID: id, HealthyAdult: domain.Known(true), CanFight: domain.Known(true), DefensePoints: domain.Known(20.0)})
		work = append(work, PawnWorkAssignment{Pawn: id, Priorities: []WorkPriority{{Work: WorkType("Research"), Priority: 1}}})
	}
	f := RoundsFacts{QuestOffers: domain.Known([]JoinerOffer{offer}), QuestColonyCalm: domain.Known(true), QuestColonistsAtHome: domain.Known(6), QuestSparePawns: domain.Known([]PawnID{"a", "b", "c"}), QuestDeparturePawns: domain.Known(rows), QuestDepartureWork: domain.Known(work), WorkRoster: domain.Known([]WorkCoverage{{Work: WorkType("Research"), Owners: 3}}), DefenseCapacity: domain.Known(100.0), RaidPoints: domain.Known(50.0)}
	return offer, f
}

func TestDepartureSquadPreservesEveryLastWorkOwner(t *testing.T) {
	offer, f := departureFixture(QuestFamilyPawnLend, 2)
	ids, reason := DepartureSquad(offer, f)
	if reason != "" || !reflect.DeepEqual(ids, []domain.PawnID{"a", "b"}) {
		t.Fatal(ids, reason)
	}
	offer.Objectives[0].Count = domain.Known(int64(3))
	if _, reason = DepartureSquad(offer, f); reason != "no_spare_pawn" {
		t.Fatal(reason)
	}
	rows, _ := f.QuestDeparturePawns.Value()
	rows[0].HealthyAdult = domain.Known(false)
	f.QuestDeparturePawns = domain.Known(rows)
	offer.Objectives[0].Count = domain.Known(int64(2))
	ids, reason = DepartureSquad(offer, f)
	if reason != "" || !reflect.DeepEqual(ids, []domain.PawnID{"b", "c"}) {
		t.Fatal(ids, reason)
	}
	f.QuestColonyCalm = domain.Known(false)
	if _, reason = DepartureSquad(offer, f); reason != "colony_busy" {
		t.Fatal(reason)
	}
}

func TestDepartureSquadRetainsObservedHomeDefense(t *testing.T) {
	offer, f := departureFixture(QuestFamilyBanditCamp, 2)
	if ids, reason := DepartureSquad(offer, f); reason != "" || len(ids) != 2 {
		t.Fatal(ids, reason)
	}
	f.DefenseCapacity = domain.Known(80.0)
	if _, reason := DepartureSquad(offer, f); reason != "home_defense" {
		t.Fatal(reason)
	}
	rows, _ := f.QuestDeparturePawns.Value()
	rows[0].CanFight = domain.Known(false)
	rows[1].CanFight = domain.Known(false)
	f.QuestDeparturePawns = domain.Known(rows)
	if _, reason := DepartureSquad(offer, f); reason != "home_defense" {
		t.Fatal(reason)
	}
}

func TestDepartureSquadExcludesAnotherPendingSquad(t *testing.T) {
	offer, f := departureFixture(QuestFamilyPawnLend, 1)
	other := offer
	other.Quest = "other"
	other.Shuttles = []QuestShuttleState{{PendingPawnIDs: []domain.PawnID{"a"}}}
	f.QuestOffers = domain.Known([]JoinerOffer{offer, other})
	ids, reason := DepartureSquad(offer, f)
	if reason != "" || !reflect.DeepEqual(ids, []domain.PawnID{"b"}) {
		t.Fatal(ids, reason)
	}
	// Reserving a second primary owner leaves the third owner essential.
	other.Shuttles[0].PendingPawnIDs = []domain.PawnID{"a", "b"}
	f.QuestOffers = domain.Known([]JoinerOffer{offer, other})
	if _, reason := DepartureSquad(offer, f); reason != "no_spare_pawn" {
		t.Fatal(reason)
	}
}

func TestQuestDepartureBoardWaitLaunchReturnLifecycle(t *testing.T) {
	offer, f := departureFixture(QuestFamilyPawnLend, 2)
	shuttle := QuestShuttleState{Loading: domain.Known(false), AllRequiredLoaded: domain.Known(false), ManualLaunchAvailable: domain.Known(false)}
	offer.Shuttles = []QuestShuttleState{shuttle}
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	work, err := SelectQuestDeparture(f)
	if err != nil || work.Shuttle == nil || work.Shuttle.Loading() != domain.ShuttleExplicitPawns || len(work.Shuttle.Pawns()) != 2 {
		t.Fatal(work, err)
	}
	shuttle.Loading = domain.Known(true)
	offer.Shuttles[0] = shuttle
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	work, err = SelectQuestDeparture(f)
	if err != nil || !work.Waiting || work.Shuttle != nil {
		t.Fatal("reboarded", work, err)
	}
	shuttle.AllRequiredLoaded = domain.Known(true)
	shuttle.ManualLaunchAvailable = domain.Known(true)
	offer.Shuttles[0] = shuttle
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	work, err = SelectQuestDeparture(f)
	if err != nil || work.Shuttle == nil || !work.Shuttle.Launch() {
		t.Fatal(work, err)
	}
	offer.Shuttles = nil
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	work, err = SelectQuestDeparture(f)
	if err != nil || !work.Waiting || work.Shuttle != nil {
		t.Fatal("away spawned replacement", work, err)
	}
	offer.State = "EndedSuccess"
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	work, err = SelectQuestDeparture(f)
	if err != nil || work.Quest != "" {
		t.Fatal("return boarded again", work, err)
	}
}
