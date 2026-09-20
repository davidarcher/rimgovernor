package bridge

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
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
			Bench:    &o.EntityRef{Id: proto.String("bench1")},
			Bills: []*o.BillState{
				{Id: proto.String("bill1"), Recipe: &o.DefinitionRef{DefName: proto.String("MakeParka")}, Suspended: proto.Bool(false), Finished: proto.Bool(false)},
			},
		}},
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}},
	}}}
	recipes := &o.RecipesReply{Outcome: &o.RecipesReply_Observed{Observed: &o.RecipesSnapshot{
		Context:  gearBenchContext(),
		Snapshot: &o.SnapshotRef{EntityId: proto.String("bench1")},
		Recipes: []*o.RecipeState{{
			Recipe:           &o.DefinitionRef{DefName: proto.String("MakeParka")},
			AvailableNow:     proto.Bool(true),
			AvailableOnBench: proto.Bool(true),
			Products:         []*o.Quantity{{DefName: proto.String("Parka")}},
			WorkSkill:        proto.String("Crafting"),
			WorkType:         proto.String("Tailoring"),
			Skills:           []*o.SkillRequirement{{DefName: proto.String("Crafting"), Minimum: proto.Int32(4)}},
			Ingredients:      []*o.IngredientRequirement{{AllowedDefNames: []string{"Synthread"}, Required: proto.Float64(4), Complete: proto.Bool(true)}},
		}},
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}},
	}}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		switch arg.Tool {
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
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}},
	}}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
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

func TestReadSupplyStockRejectsInvalidInput(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema}, time.Second)
	if _, _, err := client.ReadSupplyStock(context.Background(), pbIdentity(), []string{"a", "a"}); err == nil {
		t.Fatal("expected duplicate rejection")
	}
	if out, _, err := client.ReadSupplyStock(context.Background(), pbIdentity(), nil); err != nil || out != nil {
		t.Fatal(out, err)
	}
}

func TestReadRecipeCatalogListsHostingBenchDefinitions(t *testing.T) {
	catalog := &o.RecipesReply{Outcome: &o.RecipesReply_Observed{Observed: &o.RecipesSnapshot{
		Context:  gearBenchContext(),
		Snapshot: &o.SnapshotRef{Context: gearBenchContext()},
		Recipes: []*o.RecipeState{{
			Recipe:       &o.DefinitionRef{DefName: proto.String("Make_MeleeWeapon_Club")},
			AvailableNow: proto.Bool(true),
			Products:     []*o.Quantity{{DefName: proto.String("MeleeWeapon_Club"), Units: proto.Int64(1)}},
			WorkSkill:    proto.String("Crafting"),
			WorkType:     proto.String("Crafting"),
			BenchDefs:    []string{"CraftingSpot"},
			Ingredients: []*o.IngredientRequirement{{AllowedDefNames: []string{"WoodLog"}, Required: proto.Float64(40), Complete: proto.Bool(true),
				Alternatives: []*o.Quantity{{DefName: proto.String("WoodLog"), Units: proto.Int64(40)}}}},
		}},
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}},
	}}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		if arg.Tool != "rimgovernor/observations_read_recipes" {
			t.Fatal(arg.Tool)
		}
		q := &o.RecipesRequest{}
		if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
			t.Fatal(err)
		}
		if q.BenchId != nil || q.GetProductDef() != "MeleeWeapon_Club" {
			t.Fatal("unexpected catalog request", q)
		}
		return pbResult(catalog), nil
	}}, time.Second)
	hosts, _, err := client.ReadRecipeCatalog(context.Background(), pbIdentity(), "MeleeWeapon_Club")
	if err != nil {
		t.Fatal(err)
	}
	if len(hosts) != 1 || hosts[0].Definition != "Make_MeleeWeapon_Club" || !hosts[0].Available || len(hosts[0].Benches) != 1 || hosts[0].Benches[0] != "CraftingSpot" || len(hosts[0].Products) != 1 || hosts[0].Products[0] != "MeleeWeapon_Club" {
		t.Fatal(hosts)
	}
	work, known := hosts[0].RequiredWork.Value()
	if !known || len(work) != 1 || work[0].Work != "Crafting" {
		t.Fatal(work)
	}
	slots, known := hosts[0].Ingredients.Value()
	if !known || len(slots) != 1 || slots[0][0].Count != 40 {
		t.Fatal(slots)
	}
}
