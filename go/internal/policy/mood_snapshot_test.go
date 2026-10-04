package policy_test

import (
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// loadRecorded decodes a planner input recorded with the snapshot codec.
func loadRecorded(t *testing.T, path string, v any) {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if err = snapshot.Decode(data, v); err != nil {
		t.Fatalf("%s: %v (re-record it)", path, err)
	}
}

// Replaces the native mood/provision case (#255, #748). MaintainMood's
// review is not a routine planner input the case served, so the recording
// is the policy.MoodPawn the case lifted from the routine census's social
// block (observations_list_pawns with needs, schedule and social), encoded
// with snapshot.Encode: mood-provision-pressured.json right after
// test/mood_setup scenario "environment" (SleptOutside + NeedJoy),
// mood-provision-cleared.json after a day of plain native ticks with no
// relief dispatched. Recorded at 4e86b4663 from `acceptance run
// mood/provision` (passed).
func TestMoodProvisionDefersRecordedEnvironmentPressure(t *testing.T) {
	var before, after policy.MoodPawn
	loadRecorded(t, "testdata/mood-provision-pressured.json", &before)
	loadRecorded(t, "testdata/mood-provision-cleared.json", &after)
	thought := func(p policy.MoodPawn, def string) bool {
		rows, known := p.Thoughts.Value()
		if !known {
			t.Fatalf("%s: thought pressure unknown", p.ID)
		}
		for _, r := range rows {
			if r.Def == def {
				return true
			}
		}
		return false
	}
	for _, def := range []string{"SleptOutside", "NeedJoy"} {
		if !thought(before, def) {
			t.Fatalf("recorded pressure lacks %s", def)
		}
	}
	history, err := policy.ReviewMood(domain.Known([]policy.MoodPawn{before}), policy.MoodHistory{})
	if err != nil {
		t.Fatal(err)
	}
	if len(history.States) != 1 || !history.States[0].Active {
		t.Fatalf("pressure opened no active mood state: %+v", history)
	}
	staged := map[policy.ConcernID]bool{policy.EnsureComfort: true, policy.MaintainHousing: true}
	owners := map[policy.ConcernID]bool{}
	for _, p := range history.States[0].Provision {
		owners[p.Goal] = true
	}
	for goal := range staged {
		if !owners[goal] {
			t.Fatalf("no %s provisioning: %+v", goal, history.States[0].Provision)
		}
	}
	proposal, err := policy.SelectMoodMethod(history.States[0], nil)
	if err != nil {
		t.Fatal(err)
	}
	if proposal.Reason != policy.MoodProvisioned || !owners[proposal.Goal] {
		t.Fatalf("want a facility_provision proposal to an owner goal, got %+v", proposal)
	}

	if thought(after, "SleptOutside") || thought(after, "NeedJoy") {
		t.Fatalf("cleared recording still carries the staged thoughts")
	}
	history, err = policy.ReviewMood(domain.Known([]policy.MoodPawn{after}), history)
	if err != nil {
		t.Fatal(err)
	}
	if len(history.States) != 1 {
		t.Fatalf("want the fixture pawn's state alone, got %+v", history.States)
	}
	for _, p := range history.States[0].Provision {
		if staged[p.Goal] {
			t.Fatalf("cleared pressure still provisions %s", p.Goal)
		}
	}
	proposal, err = policy.SelectMoodMethod(history.States[0], nil)
	if err != nil {
		t.Fatal(err)
	}
	if proposal.Reason == policy.MoodRelief || proposal.Reason == policy.MoodProvisioned && staged[proposal.Goal] {
		t.Fatalf("pressure cleared but the review still proposes %+v", proposal)
	}
}

// Replaces the native mood/berserk case's selection half (#748). The
// subdue squad and the rescue are chosen by the defense and rescue step
// planners from reads outside the rounds, so their inputs were
// recorded by a temporary dump at the policy call (buildingruntime
// planBreak -> SelectBreakSquad, RoutineRescuePlanner -> SelectRescue)
// during `acceptance run mood/berserk` at 4e86b4663: the tribal8 fixture
// with Thing_Human728 berserk and the two melee-armed squad pawns
// Thing_Human724/726 standing beside it. That run completed the subdue and
// admitted the rescue, then stalled awaiting the rescue's observation; the
// native containment outcome (own bed, no death or prisoner) is not
// asserted here.
func TestBerserkSubdueSquadFromRecordedResponders(t *testing.T) {
	var in struct {
		Target     policy.EmergencyPawn
		Cell       domain.Fact[domain.Cell]
		Responders []policy.BreakResponder
		Chosen     []domain.PawnID
	}
	loadRecorded(t, "testdata/mood-berserk-break-squad.json", &in)
	if in.Target.ID != "Thing_Human728" || !policy.AggressiveBreak(in.Target) {
		t.Fatalf("recorded target is not the aggressive break: %+v", in.Target)
	}
	got := policy.SelectBreakSquad(in.Target, in.Cell, in.Responders)
	want := []domain.PawnID{"Thing_Human724", "Thing_Human726"}
	if len(got) != 2 || got[0] != want[0] || got[1] != want[1] {
		t.Fatalf("squad = %v, want the two melee responders %v", got, want)
	}
	// Every other colonist carries a ranged weapon or none: none may join.
	for _, r := range in.Responders {
		melee, _ := r.MeleeEquipped.Value()
		if (r.ID == want[0] || r.ID == want[1]) != melee {
			t.Fatalf("fixture melee roster changed: %s melee=%v", r.ID, melee)
		}
	}
}

func TestBerserkRescueFromRecordedCombatPawns(t *testing.T) {
	var in struct {
		Rescuers         []policy.RescuerFacts
		Patients         []policy.RescuePatientFacts
		Rescuer, Patient domain.PawnID
		OK               bool
	}
	loadRecorded(t, "testdata/mood-berserk-rescue.json", &in)
	rescuer, patient, ok := policy.SelectRescue(in.Rescuers, in.Patients)
	if !ok || patient != "Thing_Human728" {
		t.Fatalf("rescue = %s -> %s ok=%v, want the downed berserker Thing_Human728", rescuer, patient, ok)
	}
	if rescuer == patient || rescuer != in.Rescuer {
		t.Fatalf("rescuer = %s, recorded %s", rescuer, in.Rescuer)
	}
}
