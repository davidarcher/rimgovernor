package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

func refIDs(job *o.GiveJobIntent) []string {
	var out []string
	for _, r := range job.GetTargets() {
		out = append(out, r.GetId())
	}
	return out
}

func TestUseItemIntentWire(t *testing.T) {
	use, err := domain.NewUseItem("Human1", "Apparel_PsychicShockLance7", "Human2")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewUseItemAction("lance-0", use)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("use_item is not an intent-mode kind")
	}
	wire, err := IntentAction("key-1", action)
	if err != nil {
		t.Fatal(err)
	}
	got := wire.GetGiveJob()
	ids := refIDs(got)
	if wire.GetKey() != "key-1" || got.GetPawn().GetId() != "Human1" || got.GetJob() != JobUseItem || len(ids) != 2 || ids[0] != "Apparel_PsychicShockLance7" || ids[1] != "Human2" {
		t.Fatalf("wire = %v", wire)
	}
}

func TestUseItemSelfUseWire(t *testing.T) {
	use, err := domain.NewUseItem("Human1", "PsychicAmplifier7", "Human1")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewUseItemAction("amp-0", use)
	if err != nil {
		t.Fatal(err)
	}
	wire, err := IntentAction("key-1", action)
	if err != nil {
		t.Fatal(err)
	}
	ids := refIDs(wire.GetGiveJob())
	if len(ids) != 2 || ids[0] != "PsychicAmplifier7" || ids[1] != "Human1" || wire.GetGiveJob().GetPawn().GetId() != "Human1" {
		t.Fatalf("wire = %v", wire)
	}
	// Only UseItem targets its user: every other job still refuses it.
	if _, err := giveJob("Human1", JobRepair, "Human1"); err == nil {
		t.Fatal("Repair accepted its own pawn as target")
	}
}

func TestGiveJobRefusesMalformed(t *testing.T) {
	for _, c := range [][]string{{"", "Repair", "t"}, {"p", "", "t"}, {"p", "Repair"}, {"p", "Repair", "p"}, {"p", "Repair", ""}} {
		if _, err := giveJob(domain.PawnID(c[0]), c[1], c[2:]...); err == nil {
			t.Fatalf("giveJob(%q) accepted", c)
		}
	}
}

func TestPrioritizedJobWire(t *testing.T) {
	wire, err := PrioritizedJob("Human1", "FinishFrame", "Frame_Wall7")
	if err != nil {
		t.Fatal(err)
	}
	got := wire.GetGiveJob()
	if got.GetJob() != "FinishFrame" || !got.GetOptions().GetPrioritized() || refIDs(got)[0] != "Frame_Wall7" {
		t.Fatalf("wire = %v", wire)
	}
}

func TestMoodReliefIsNeedGiverJob(t *testing.T) {
	relief, err := domain.NewMoodRelief("Human1", domain.MoodReliefJoy)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewMoodReliefAction("relief-0", relief)
	if err != nil {
		t.Fatal(err)
	}
	wire, err := IntentAction("key-1", action)
	if err != nil {
		t.Fatal(err)
	}
	got := wire.GetGiveJob()
	if got.GetPawn().GetId() != "Human1" || got.Job != nil || len(got.GetTargets()) != 0 || got.GetOptions().GetRelieveNeed() != o.Need_NEED_JOY {
		t.Fatalf("wire = %v", wire)
	}
}
