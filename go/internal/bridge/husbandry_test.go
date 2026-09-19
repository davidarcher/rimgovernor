package bridge

import (
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func TestHusbandryOperationPerMethod(t *testing.T) {
	cases := map[HusbandryMethod]func(*o.Operation) *o.EntityPrecondition{
		HusbandryMethodCancelSlaughter: func(op *o.Operation) *o.EntityPrecondition { return op.GetSlaughterAnimal().GetAnimal() },
		HusbandryMethodCancelRelease:   func(op *o.Operation) *o.EntityPrecondition { return op.GetReleaseAnimal().GetAnimal() },
		HusbandryMethodTrain:           func(op *o.Operation) *o.EntityPrecondition { return op.GetSetAnimalTraining().GetAnimal() },
		HusbandryMethodSlaughter:       func(op *o.Operation) *o.EntityPrecondition { return op.GetSlaughterAnimal().GetAnimal() },
		HusbandryMethodTame:            func(op *o.Operation) *o.EntityPrecondition { return op.GetTameAnimal().GetAnimal() },
		HusbandryMethodRelease:         func(op *o.Operation) *o.EntityPrecondition { return op.GetReleaseAnimal().GetAnimal() },
		HusbandryMethodAllowedArea:     func(op *o.Operation) *o.EntityPrecondition { return op.GetSetAnimalArea().GetAnimal() },
		HusbandryMethodMaster:          func(op *o.Operation) *o.EntityPrecondition { return op.GetSetAnimalMaster().GetAnimal() },
		HusbandryMethodFollowDrafted: func(op *o.Operation) *o.EntityPrecondition {
			return op.GetSetAnimalFollowing().GetAnimal()
		},
		HusbandryMethodFollowFieldwork: func(op *o.Operation) *o.EntityPrecondition {
			return op.GetSetAnimalFollowing().GetAnimal()
		},
	}
	arguments := map[HusbandryMethod]string{HusbandryMethodTrain: "Obedience", HusbandryMethodAllowedArea: "Area_3", HusbandryMethodMaster: "Thing_Human_1", HusbandryMethodFollowDrafted: "true", HusbandryMethodFollowFieldwork: "false"}
	for method, entity := range cases {
		trainable := arguments[method]
		if err := husbandryCommand("animal", "animal-cas", "census-cas", method, trainable); err != nil {
			t.Fatal(method, err)
		}
		op := husbandryOperation("animal", "animal-cas", "census-cas", trainable, method)
		if op.GetSlaughterAnimal().GetCancel() != (method == HusbandryMethodCancelSlaughter) || op.GetReleaseAnimal().GetCancel() != (method == HusbandryMethodCancelRelease) {
			t.Fatal("cancellation changed at wire boundary", op)
		}
		if e := entity(op); e.GetEntityId() != "animal" || e.GetExpectedSnapshotToken() != "animal-cas" {
			t.Fatal(method, op)
		}
		switch method {
		case HusbandryMethodSlaughter, HusbandryMethodTame, HusbandryMethodRelease:
			if err := husbandryCommand("animal", "animal-cas", "census-cas", method, "Obedience"); err == nil {
				t.Fatal(method, "accepted an argument")
			}
		case HusbandryMethodFollowDrafted, HusbandryMethodFollowFieldwork:
			if err := husbandryCommand("animal", "animal-cas", "census-cas", method, "yes"); err == nil {
				t.Fatal(method, "accepted a non-boolean flag")
			}
		}
	}
	// Assignments: an empty argument clears, a non-empty one names the entity.
	if v := husbandryOperation("animal", "animal-cas", "census-cas", "", HusbandryMethodAllowedArea).GetSetAnimalArea().GetArea(); v.GetClear() == nil {
		t.Fatal("empty area argument did not clear", v)
	}
	if v := husbandryOperation("animal", "animal-cas", "census-cas", "Thing_Human_1", HusbandryMethodMaster).GetSetAnimalMaster().GetMaster(); v.GetEntityId() != "Thing_Human_1" {
		t.Fatal("master argument lost", v)
	}
	// Each follow method sets only its own flag.
	if f := husbandryOperation("animal", "animal-cas", "census-cas", "true", HusbandryMethodFollowDrafted).GetSetAnimalFollowing(); f.FollowDrafted == nil || !f.GetFollowDrafted() || f.FollowFieldwork != nil {
		t.Fatal(f)
	}
	if f := husbandryOperation("animal", "animal-cas", "census-cas", "false", HusbandryMethodFollowFieldwork).GetSetAnimalFollowing(); f.FollowFieldwork == nil || f.GetFollowFieldwork() || f.FollowDrafted != nil {
		t.Fatal(f)
	}
	if err := husbandryCommand("animal", "animal-cas", "census-cas", HusbandryMethod(99), ""); err == nil {
		t.Fatal("unknown method accepted")
	}
}

func TestHusbandryEvidenceRequiresExactlyTheMethodField(t *testing.T) {
	effect := func(mutate func(*r.AnimalEffect)) *r.AnimalEffect {
		e := &r.AnimalEffect{Animal: &r.SnapshotEvidence{EntityId: proto.String("animal"), AfterToken: proto.String("after")}, CensusToken: proto.String("census-cas")}
		mutate(e)
		return e
	}
	expect := func(method HusbandryMethod) HusbandryAttempt {
		a := HusbandryAttempt{Animal: "animal", Method: method}
		if method == HusbandryMethodTrain {
			a.Argument = "Obedience"
		}
		return a
	}
	good := map[HusbandryMethod]func(*r.AnimalEffect){
		HusbandryMethodTrain:           func(e *r.AnimalEffect) { e.TrainableDef = proto.String("Obedience"); e.Wanted = proto.Bool(true) },
		HusbandryMethodSlaughter:       func(e *r.AnimalEffect) { e.SlaughterDesignated = proto.Bool(true) },
		HusbandryMethodTame:            func(e *r.AnimalEffect) { e.TameDesignated = proto.Bool(true) },
		HusbandryMethodRelease:         func(e *r.AnimalEffect) { e.ReleaseDesignated = proto.Bool(true) },
		HusbandryMethodAllowedArea:     func(e *r.AnimalEffect) { e.AllowedAreaId = proto.String("Area_3") },
		HusbandryMethodMaster:          func(e *r.AnimalEffect) { e.MasterId = proto.String("") },
		HusbandryMethodFollowDrafted:   func(e *r.AnimalEffect) { e.FollowDrafted = proto.Bool(true) },
		HusbandryMethodFollowFieldwork: func(e *r.AnimalEffect) { e.FollowFieldwork = proto.Bool(false) },
	}
	for method, mutate := range good {
		if _, err := husbandryEvidence(effect(mutate), expect(method)); err != nil {
			t.Fatal(method, err)
		}
		for other, otherMutate := range good {
			if other == method {
				continue
			}
			if _, err := husbandryEvidence(effect(otherMutate), expect(method)); err == nil {
				t.Fatal(method, "accepted", other, "evidence")
			}
		}
	}
	if _, err := husbandryEvidence(effect(func(e *r.AnimalEffect) { e.Animal.EntityId = proto.String("other") }), expect(HusbandryMethodTame)); err == nil {
		t.Fatal("animal mismatch accepted")
	}
}
