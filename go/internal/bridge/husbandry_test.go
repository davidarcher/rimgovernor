package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// Every domain method maps to its own order, and each order carries exactly
// its argument field.
func TestHusbandryActionCarriesEachOrdersArgument(t *testing.T) {
	arguments := map[domain.HusbandryMethod]string{domain.HusbandryTrain: "Obedience", domain.HusbandryAllowedArea: "Area_3", domain.HusbandryMaster: "Thing_Human_1", domain.HusbandryFollowDrafted: "true", domain.HusbandryFollowFieldwork: "false"}
	seen := map[o.HusbandryOrder]bool{}
	for method, order := range husbandryOrders {
		h, err := domain.NewHusbandry("animal", method, arguments[method])
		if err != nil {
			t.Fatal(method, err)
		}
		a, err := domain.NewHusbandryAction("a", h)
		if err != nil {
			t.Fatal(err)
		}
		action, err := husbandryAction(a)
		if err != nil {
			t.Fatal(method, err)
		}
		intent := action.GetHusbandry()
		if intent.GetAnimalId() != "animal" || intent.GetOrder() != order || seen[order] {
			t.Fatal(method, intent)
		}
		seen[order] = true
		if (intent.TrainableDef != nil) != (method == domain.HusbandryTrain) || (intent.TargetId != nil) != (arguments[method] != "" && (method == domain.HusbandryAllowedArea || method == domain.HusbandryMaster)) ||
			(intent.Follow != nil) != (method == domain.HusbandryFollowDrafted || method == domain.HusbandryFollowFieldwork) {
			t.Fatal(method, intent)
		}
	}
	if action, _ := husbandryAction(mustHusbandry(t, domain.HusbandryFollowDrafted, "true")); !action.GetHusbandry().GetFollow() {
		t.Fatal("follow flag lost")
	}
	if action, _ := husbandryAction(mustHusbandry(t, domain.HusbandryMaster, "")); action.GetHusbandry().TargetId != nil {
		t.Fatal("an empty master must clear")
	}
	if _, err := husbandryAction(domain.Action{}); err == nil {
		t.Fatal("non-husbandry action accepted")
	}
}

func mustHusbandry(t *testing.T, method domain.HusbandryMethod, argument string) domain.Action {
	t.Helper()
	h, err := domain.NewHusbandry("animal", method, argument)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewHusbandryAction("a", h)
	if err != nil {
		t.Fatal(err)
	}
	return a
}

func TestPrioritizeSlaughterIsAPrioritizedGiveJob(t *testing.T) {
	wire, err := husbandryAction(mustHusbandry(t, domain.HusbandryPrioritizeSlaughter, "Human1"))
	if err != nil {
		t.Fatal(err)
	}
	got := wire.GetGiveJob()
	if wire.GetHusbandry() != nil || got.GetPawn().GetId() != "Human1" || got.GetJob() != JobSlaughter || !got.GetOptions().GetPrioritized() || len(refIDs(got)) != 1 || refIDs(got)[0] != "animal" {
		t.Fatalf("wire = %v", wire)
	}
	if _, err := domain.NewHusbandry("animal", domain.HusbandryPrioritizeSlaughter, ""); err == nil {
		t.Fatal("prioritized slaughter without a handler accepted")
	}
}
