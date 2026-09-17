package domain

import (
	"strings"
	"testing"
)

func TestHusbandryIntentAndClosedVariants(t *testing.T) {
	train, err := NewHusbandry("animal", HusbandryTrain, "Trainability_Advanced")
	if err != nil || train.Animal() != "animal" || train.Method() != HusbandryTrain || train.TrainableDef() != "Trainability_Advanced" {
		t.Fatal(train, err)
	}
	action, err := NewHusbandryAction("husbandry-1", train)
	if err != nil || action.ID() != "husbandry-1" || action.Kind() != HusbandryAction {
		t.Fatal(action, err)
	}
	if got, ok := action.Husbandry(); !ok || got != train {
		t.Fatal(got, ok)
	}
	if _, ok := action.GearReplace(); ok {
		t.Fatal("husbandry exposed gear replace")
	}
	if _, ok := action.Building(); ok {
		t.Fatal("husbandry exposed building")
	}

	slaughter, err := NewHusbandry("animal", HusbandrySlaughter, "")
	if err != nil || slaughter.Method() != HusbandrySlaughter || slaughter.TrainableDef() != "" {
		t.Fatal(slaughter, err)
	}

	if _, err := NewHusbandryAction("husbandry-1", Husbandry{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	if _, err := NewHusbandry("animal", HusbandryTrain, ""); err == nil {
		t.Fatal("training without a trainable definition accepted")
	}
	if _, err := NewHusbandry("animal", HusbandrySlaughter, "Trainability_Advanced"); err == nil {
		t.Fatal("slaughter with a trainable definition accepted")
	}
	if _, err := NewHusbandry("animal", HusbandryMethod("groom"), ""); err == nil {
		t.Fatal("invalid method accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewHusbandry(PawnID(invalid), HusbandrySlaughter, ""); err == nil {
			t.Fatal("invalid animal accepted")
		}
		if _, err := NewHusbandry("animal", HusbandryTrain, invalid); err == nil {
			t.Fatal("invalid trainable definition accepted")
		}
		if _, err := NewHusbandryAction(ActionID(invalid), train); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
}

func TestHusbandryPlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	slaughter, _ := NewHusbandry("animal", HusbandrySlaughter, "")
	action, _ := NewHusbandryAction("husbandry-1", slaughter)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "husbandry-1")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
}

func TestHusbandryHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction}); err == nil {
		t.Fatal("missing husbandry handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, HusbandryAction, HusbandryAction}); err == nil {
		t.Fatal("duplicate husbandry handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}

func TestHusbandryTameAndReleaseVariants(t *testing.T) {
	for _, method := range []HusbandryMethod{HusbandryTame, HusbandryRelease} {
		h, err := NewHusbandry("animal", method, "")
		if err != nil || h.Method() != method || h.TrainableDef() != "" {
			t.Fatal(h, err)
		}
		if _, err := NewHusbandry("animal", method, "Trainability_Advanced"); err == nil {
			t.Fatal(method, "with a trainable definition accepted")
		}
		if _, err := NewHusbandryAction("husbandry-1", h); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := NewHusbandry("animal", "pen", ""); err == nil {
		t.Fatal("unknown method accepted")
	}
}

func TestHusbandrySettingsVariants(t *testing.T) {
	for _, c := range []struct {
		method HusbandryMethod
		good   []string
		bad    []string
	}{
		{HusbandryAllowedArea, []string{"Area_Allowed_3", ""}, []string{" ", "bad\x00id"}},
		{HusbandryMaster, []string{"Thing_Human_12", ""}, []string{"bad\x00id"}},
		{HusbandryFollowDrafted, []string{"true", "false"}, []string{"", "yes", "True"}},
		{HusbandryFollowFieldwork, []string{"true", "false"}, []string{"", "1"}},
	} {
		for _, argument := range c.good {
			h, err := NewHusbandry("animal", c.method, argument)
			if err != nil || h.Argument() != argument || h.TrainableDef() != "" {
				t.Fatal(c.method, argument, h, err)
			}
			if _, err := NewHusbandryAction("husbandry-1", h); err != nil {
				t.Fatal(c.method, argument, err)
			}
		}
		for _, argument := range c.bad {
			if _, err := NewHusbandry("animal", c.method, argument); err == nil {
				t.Fatal(c.method, "accepted", argument)
			}
		}
	}
	train, _ := NewHusbandry("animal", HusbandryTrain, "Obedience")
	if train.TrainableDef() != "Obedience" || train.Argument() != "Obedience" {
		t.Fatal(train)
	}
}
