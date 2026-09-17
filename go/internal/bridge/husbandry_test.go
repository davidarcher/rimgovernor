package bridge

import (
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func TestHusbandryOperationPerMethod(t *testing.T) {
	cases := map[HusbandryMethod]func(*o.Operation) *o.EntityPrecondition{
		HusbandryMethodTrain:     func(op *o.Operation) *o.EntityPrecondition { return op.GetSetAnimalTraining().GetAnimal() },
		HusbandryMethodSlaughter: func(op *o.Operation) *o.EntityPrecondition { return op.GetSlaughterAnimal().GetAnimal() },
		HusbandryMethodTame:      func(op *o.Operation) *o.EntityPrecondition { return op.GetTameAnimal().GetAnimal() },
		HusbandryMethodRelease:   func(op *o.Operation) *o.EntityPrecondition { return op.GetReleaseAnimal().GetAnimal() },
	}
	for method, entity := range cases {
		trainable := ""
		if method == HusbandryMethodTrain {
			trainable = "Obedience"
		}
		if err := husbandryCommand("animal", "animal-cas", "census-cas", method, trainable); err != nil {
			t.Fatal(method, err)
		}
		op := husbandryOperation("animal", "animal-cas", "census-cas", trainable, method)
		if e := entity(op); e.GetEntityId() != "animal" || e.GetExpectedSnapshotToken() != "animal-cas" {
			t.Fatal(method, op)
		}
		if method != HusbandryMethodTrain {
			if err := husbandryCommand("animal", "animal-cas", "census-cas", method, "Obedience"); err == nil {
				t.Fatal(method, "accepted a trainable def")
			}
		}
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
			a.TrainableDef = "Obedience"
		}
		return a
	}
	good := map[HusbandryMethod]func(*r.AnimalEffect){
		HusbandryMethodTrain:     func(e *r.AnimalEffect) { e.TrainableDef = proto.String("Obedience"); e.Wanted = proto.Bool(true) },
		HusbandryMethodSlaughter: func(e *r.AnimalEffect) { e.SlaughterDesignated = proto.Bool(true) },
		HusbandryMethodTame:      func(e *r.AnimalEffect) { e.TameDesignated = proto.Bool(true) },
		HusbandryMethodRelease:   func(e *r.AnimalEffect) { e.ReleaseDesignated = proto.Bool(true) },
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
