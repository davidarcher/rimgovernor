package bridge

import (
	"context"
	"encoding/json"
	"fmt"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func gearBenchContext() *c.ObservationContext {
	return &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(5), NativeGeneration: proto.Uint64(1)}
}

func TestReadGearBenchesAssemblesCensusAcrossBillsAndRecipes(t *testing.T) {
	bills := &o.BillsReply{Outcome: &o.BillsReply_Observed{Observed: &o.BillsSnapshot{
		Context: gearBenchContext(),
		Benches: []*o.BillStack{{
			Snapshot: &o.SnapshotRef{Context: gearBenchContext(), EntityId: proto.String("bench1"), Token: proto.String("bench1-token")},
			Bench:    &c.Ref{Id: proto.String("bench1")},
			Bills: []*o.BillState{
				{Id: proto.String("bill1"), Recipe: &o.DefinitionRef{DefName: proto.String("MakeParka")}, Suspended: proto.Bool(false), Finished: proto.Bool(false)},
			},
		}},
	}}}
	recipes := &o.RecipesReply{Outcome: &o.RecipesReply_Observed{Observed: &o.RecipesSnapshot{
		Context:  gearBenchContext(),
		Snapshot: &o.SnapshotRef{EntityId: proto.String("bench1")},
		BenchDef: proto.String("TableTailor"),
		Recipes: []*o.RecipeState{{
			Recipe:           &o.DefinitionRef{DefName: proto.String("MakeParka")},
			AvailableNow:     proto.Bool(true),
			AvailableOnBench: proto.Bool(true),
		}},
	}}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		switch arg.Tool {
		case methodDefinitionCatalog:
			reply := catalogReply(gearBenchContext())
			v := reply.GetObserved()
			v.ThingDefs = []*d.ThingDef{{DefName: "Parka", ThingClass: "Verse.ThingWithComps"}, {DefName: "Synthread", ThingClass: "Verse.ThingWithComps"}, {DefName: "TableTailor", ThingClass: "RimWorld.Building_WorkTable"}}
			v.ThingFacts = []*o.ThingDefFacts{{DefName: "Parka"}, {DefName: "Synthread"}, {DefName: "TableTailor"}}
			v.ClassChains = []*o.ClassChain{{Name: "Verse.ThingWithComps"}, {Name: "RimWorld.Building_WorkTable"}, {Name: "RimWorld.WorkGiver_DoBill"}, {Name: "RimWorld.WorkGiver_DoBillTest", Bases: []string{"RimWorld.WorkGiver_DoBill"}}, {Name: "RimWorld.IngredientValueGetter_Volume"}}
			v.Defs.WorkGiverDefs = []*d.WorkGiverDef{{DefName: "DoBillsTailor", GiverClass: "RimWorld.WorkGiver_DoBillTest", WorkType: "Tailoring", FixedBillGiverDefs: []string{"TableTailor"}}}
			v.Defs.RecipeDefs = []*d.RecipeDef{{DefName: "MakeParka", WorkSkill: "Crafting", WorkAmount: 100,
				Products:          []*d.Opt_ThingDefCountClass{{Value: &d.ThingDefCountClass{ThingDef: "Parka", Count: 1}}},
				SkillRequirements: []*d.Opt_SkillRequirement{{Value: &d.SkillRequirement{Skill: "Crafting", MinLevel: 4}}},
				Ingredients:       []*d.Opt_IngredientCount{{Value: &d.IngredientCount{Count: 4, Filter: &d.ThingFilter{ThingDefs: []string{"Synthread"}}}}}}}
			return pbResult(reply), nil
		case "rimgovernor/observations_read_bills":
			return pbResult(bills), nil
		case "rimgovernor/observations_read_recipes":
			q := &o.RecipesRequest{}
			if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
				t.Fatal(err)
			}
			if q.GetBenchId() != "bench1" {
				t.Fatal("unexpected bench", q)
			}
			return pbResult(recipes), nil
		default:
			t.Fatal(arg.Tool)
			return nil, nil
		}
	}}, time.Second)
	census, _, err := client.ReadGearBenches(context.Background(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if len(census) != 1 || census[0].Token != "bench1-token" || census[0].Bench.ID != "bench1" {
		t.Fatal(census)
	}
	recipeList, known := census[0].Bench.Recipes.Value()
	if !known || len(recipeList) != 1 || recipeList[0].Definition != "MakeParka" || len(recipeList[0].Products) != 1 || recipeList[0].Products[0] != "Parka" {
		t.Fatal(recipeList)
	}
	slots, known := recipeList[0].Ingredients.Value()
	if !known || len(slots) != 1 || len(slots[0]) != 1 || slots[0][0].Resource != "Synthread" || slots[0][0].Count != 4 {
		t.Fatal(slots)
	}
	work, known := recipeList[0].RequiredWork.Value()
	if !known || len(work) != 1 || work[0].Work != "Tailoring" || work[0].Skill != "Crafting" || work[0].Minimum != 4 {
		t.Fatal(work)
	}
	billList, known := census[0].Bench.Bills.Value()
	if !known || len(billList) != 1 || billList[0].ID != "bill1" || billList[0].Recipe != "MakeParka" {
		t.Fatal(billList)
	}
	active, known := billList[0].Active.Value()
	if !known || !active || len(billList[0].Products) != 1 || billList[0].Products[0] != "Parka" {
		t.Fatal(billList[0])
	}
}

func TestReadSupplyStockReportsKnownAvailability(t *testing.T) {
	reply := &o.ListSuppliesReply{Outcome: &o.ListSuppliesReply_Observed{Observed: &o.SuppliesSnapshot{
		Context: gearBenchContext(),
		Stocks: []*o.ResourceStock{{
			Definition:      &o.DefinitionRef{DefName: proto.String("Synthread")},
			OursUnforbidden: proto.Int64(12),
		}},
		Completeness: &o.Completeness{},
	}}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool != "rimgovernor/observations_list_supplies" {
			t.Fatal(arg.Tool)
		}
		return pbResult(reply), nil
	}}, time.Second)
	stock, _, err := client.ReadSupplyStock(context.Background(), pbIdentity(), []string{"Synthread", "Cloth"})
	if err != nil {
		t.Fatal(err)
	}
	if len(stock) != 2 || stock[0].Resource != "Synthread" || stock[1].Resource != "Cloth" {
		t.Fatal(stock)
	}
	available, known := stock[0].Available.Value()
	if !known || available != 12 {
		t.Fatal(stock[0])
	}
	// A complete census with no row for a requested definition holds none of it.
	if none, known := stock[1].Available.Value(); !known || none != 0 {
		t.Fatal(stock[1])
	}
}

// Native refuses more than 256 filter names, so a long list reads in chunks.
func TestReadSupplyStockChunksLongDefinitionLists(t *testing.T) {
	names := make([]string, 600)
	for i := range names {
		names[i] = fmt.Sprintf("Def%d", i)
	}
	calls := 0
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		q := &o.ListSuppliesRequest{}
		if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
			t.Fatal(err)
		}
		calls++
		got := q.GetFilter().GetDefNames()
		if len(got) == 0 || len(got) > 256 {
			t.Fatal("chunk size", len(got))
		}
		return pbResult(&o.ListSuppliesReply{Outcome: &o.ListSuppliesReply_Observed{Observed: &o.SuppliesSnapshot{
			Context:      gearBenchContext(),
			Stocks:       []*o.ResourceStock{{Definition: &o.DefinitionRef{DefName: proto.String(got[0])}, OursUnforbidden: proto.Int64(7)}},
			Completeness: &o.Completeness{},
		}}}), nil
	}}, time.Second)
	stock, _, err := client.ReadSupplyStock(context.Background(), pbIdentity(), names)
	if err != nil {
		t.Fatal(err)
	}
	if calls != 3 || len(stock) != 600 {
		t.Fatal(calls, len(stock))
	}
	for _, i := range []int{0, 256, 512} {
		found := false
		for _, s := range stock {
			if v, known := s.Available.Value(); s.Resource == policy.Resource(names[i]) && known && v == 7 {
				found = true
			}
		}
		if !found {
			t.Fatal("missing chunk row", names[i])
		}
	}
}

func TestReadSupplyStockRejectsInvalidInput(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema}, time.Second)
	if _, _, err := client.ReadSupplyStock(context.Background(), pbIdentity(), []string{"a", "a"}); err == nil {
		t.Fatal("expected duplicate rejection")
	}
	if out, _, err := client.ReadSupplyStock(context.Background(), pbIdentity(), nil); err != nil || out != nil {
		t.Fatal(out, err)
	}
}
