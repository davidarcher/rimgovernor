package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"testing"
)

func hospitalityOffer(id domain.QuestID, family QuestFamily) JoinerOffer {
	return JoinerOffer{Quest: id, State: "Ongoing", Profile: domain.Known(QuestProfile{Family: family}), Objectives: []QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_HOST_LODGERS, Count: domain.Known(int64(1))}}}
}

func TestHospitalityAdmissionRequiresAppropriateVacantBed(t *testing.T) {
	offer := hospitalityOffer("Quest_1", QuestFamilyHospitalityPrisoners)
	sleeping := guestCensus(nil)
	f := RoundsFacts{Sleeping: domain.Known(sleeping)}
	if got := HospitalityAdmission(offer, f); got != "guest_bed_capacity" {
		t.Fatal(got)
	}
	sleeping.Beds[0].Prisoners = domain.Known(true)
	f.Sleeping = domain.Known(sleeping)
	if got := HospitalityAdmission(offer, f); got != "" {
		t.Fatal(got)
	}
	sleeping.Beds[0].Users = []PawnID{"inmate"}
	f.Sleeping = domain.Known(sleeping)
	if got := HospitalityAdmission(offer, f); got != "guest_bed_capacity" {
		t.Fatal(got)
	}
}

func TestHospitalityPreacceptAssignsTitleBedroom(t *testing.T) {
	offer := hospitalityOffer("Quest_1", QuestFamilyHospitalityJoiners)
	offer.State = "NotYetAccepted"
	title := &RoyalTitle{Definition: "Knight", BedroomMinArea: 20, BedroomMinImpressiveness: 40, BedroomThings: []BedroomThing{{AnyOf: []Resource{"RoyalBed"}, Count: 1}}}
	f := RoundsFacts{QuestOffers: domain.Known([]JoinerOffer{offer}), Sleeping: domain.Known(guestCensus(title))}
	work, err := SelectHospitalityWork(f, 1)
	if err != nil || work.Assign == nil || work.Assign.Thing() != "b-royal" {
		t.Fatal(work, err)
	}
}

func TestHospitalityPickupUsesNativeLoadingPostcondition(t *testing.T) {
	offer := hospitalityOffer("Quest_2", QuestFamilyLaborers)
	shuttle := QuestShuttleState{AutoloadAvailable: domain.Known(true), Autoload: domain.Known(false), AllRequiredLoaded: domain.Known(false), ManualLaunchAvailable: domain.Known(false)}
	offer.Shuttles = []QuestShuttleState{shuttle}
	earlier := hospitalityOffer("Quest_1", QuestFamilyHospitalityRefugee)
	f := RoundsFacts{QuestOffers: domain.Known([]JoinerOffer{earlier, offer})}
	work, err := SelectHospitalityWork(f, 1)
	if err != nil || work.Quest != offer.Quest || work.Shuttle == nil || work.Shuttle.Loading() != domain.ShuttleAutoload || work.Shuttle.Launch() {
		t.Fatal(work, err)
	}
	shuttle.Autoload = domain.Known(true)
	offer.Shuttles[0] = shuttle
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	work, err = SelectHospitalityWork(f, 1)
	if err != nil || !work.Waiting || work.Shuttle != nil {
		t.Fatal(work, err)
	}
	shuttle.AllRequiredLoaded = domain.Known(true)
	shuttle.ManualLaunchAvailable = domain.Known(true)
	offer.Shuttles[0] = shuttle
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	work, err = SelectHospitalityWork(f, 1)
	if err != nil || work.Shuttle == nil || !work.Shuttle.Launch() {
		t.Fatal(work, err)
	}
	offer.State = "EndedSuccess"
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	work, err = SelectHospitalityWork(f, 1)
	if err != nil || work.Quest != "" {
		t.Fatal(work, err)
	}
}

func TestHospitalityMoodThresholdKeepsHostingActive(t *testing.T) {
	offer := hospitalityOffer("Quest_1", QuestFamilyHospitalityJoiners)
	offer.Objectives[0].MinimumMood = domain.Known(.5)
	offer.Objectives[0].LodgerMoods = []QuestLodgerMood{{Pawn: "guest", Mood: domain.Known(.2)}}
	f := RoundsFacts{QuestOffers: domain.Known([]JoinerOffer{offer})}
	work, err := SelectHospitalityWork(f, 1)
	if err != nil || !work.Waiting || work.Reason != "lodger_mood_upkeep" || !HospitalityDeficit(f) {
		t.Fatal(work, err)
	}
}

func TestProtectedMeetingGuestsExcludeCaptureAndSurgery(t *testing.T) {
	offer := hospitalityOffer("Quest_1", QuestFamilyHospitalityJoiners)
	offer.Profile = domain.Known(QuestProfile{Family: QuestFamilyHospitalityJoiners, ProtectGuests: true})
	offer.Objectives[0].PawnIDs = []domain.PawnID{"guest"}
	protected := ProtectedQuestGuestIDs(domain.Known([]JoinerOffer{offer}))
	if !protected["guest"] || protected["stranger"] {
		t.Fatal(protected)
	}
	offer.State = "EndedSuccess"
	if len(ProtectedQuestGuestIDs(domain.Known([]JoinerOffer{offer}))) != 0 {
		t.Fatal("ended guests protected")
	}
}
