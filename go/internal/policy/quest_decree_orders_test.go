package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func produceOffer(quest domain.QuestID, player domain.Fact[bool], deadline domain.Fact[int64]) JoinerOffer {
	return JoinerOffer{Quest: quest, State: "Ongoing", Profile: domain.Known(QuestProfile{Family: QuestFamilyDecreeProduce}), AskerFactionPlayer: player,
		Objectives: []QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_PRODUCE_ITEM, Def: "Armor", Count: domain.Known[int64](6), Produced: domain.Known[int64](0), Active: domain.Known(true), DeadlineTicks: deadline}}}
}

func decreeOrderRequest(offers []JoinerOffer, recipe DecreeRecipe) DecreeOrderRequest {
	stock := domain.Known([]Amount{{Resource: "Steel", Count: 100}})
	return DecreeOrderRequest{Offers: domain.Known(offers), Now: 100, Stock: stock, Recipes: func(QuestObjective) []DecreeRecipe { return []DecreeRecipe{recipe} }}
}

// An open produce decree declares the batch its recipe funds on the bench
// kind; with a bill already making it, that bill as it stands; a decree that
// is expired, another faction's or complete declares nothing.
func TestDeclareDecreeOrders(t *testing.T) {
	recipe := DecreeRecipe{Bench: "bench", BenchKind: "TableMachining", Recipe: "MakeArmor", Product: "Armor", Units: 2, Available: domain.Known(true), Stuff: map[Resource]bool{},
		Ingredients: domain.Known([][]Amount{{{Resource: "Steel", Count: 5}}})}
	open := produceOffer("q1", domain.Known(true), domain.Unknown[int64]())
	got := DeclareDecreeOrders(decreeOrderRequest([]JoinerOffer{open}, recipe))
	want := OrderSpec{Recipe: "MakeArmor", Ingredients: []string{"Steel"}, Mode: domain.GearBatch, Target: 3, BenchKind: "TableMachining"}
	if got.Abstain || len(got.Orders) != 1 || got.Orders[0].Key() != want.Key() {
		t.Fatalf("declared = %+v", got)
	}
	// The bill is half made: it is declared as the bench reads it back.
	standing := want
	standing.Target = 1
	recipe.Existing, recipe.Standing = true, []OrderSpec{standing}
	got = DeclareDecreeOrders(decreeOrderRequest([]JoinerOffer{open}, recipe))
	if len(got.Orders) != 1 || got.Orders[0].Key() != standing.Key() {
		t.Fatalf("standing = %+v", got)
	}
	recipe.Existing, recipe.Standing = false, nil
	for name, offer := range map[string]JoinerOffer{
		"expired":       produceOffer("q1", domain.Known(true), domain.Known[int64](100)),
		"another asker": produceOffer("q1", domain.Known(false), domain.Unknown[int64]()),
	} {
		if got := DeclareDecreeOrders(decreeOrderRequest([]JoinerOffer{offer}, recipe)); got.Abstain || len(got.Orders) != 0 {
			t.Fatalf("%s: %+v", name, got)
		}
	}
	// What is unread abstains.
	if got := DeclareDecreeOrders(decreeOrderRequest([]JoinerOffer{produceOffer("q1", domain.Unknown[bool](), domain.Unknown[int64]())}, recipe)); !got.Abstain {
		t.Fatalf("unread asker: %+v", got)
	}
	request := decreeOrderRequest([]JoinerOffer{open}, recipe)
	request.Stock = domain.Unknown[[]Amount]()
	if got := DeclareDecreeOrders(request); !got.Abstain || len(got.Orders) != 0 {
		t.Fatalf("unread stock: %+v", got)
	}
	request.Offers = domain.Unknown[[]JoinerOffer]()
	if got := DeclareDecreeOrders(request); !got.Abstain {
		t.Fatalf("unread offers: %+v", got)
	}
}
