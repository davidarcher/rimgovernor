package buildingruntime

import (
	"slices"
	"sort"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// exportBenchDefs are the crafting benches a colony builds first.
var exportBenchDefs = []string{"CraftingSpot", "HandTailoringBench", "ElectricTailoringBench", "FueledSmithy", "ElectricSmithy", "TableMachining", "TableSculpting"}

// TestExportRankingOnTheRecordedCatalog ranks the sale goods of the recorded
// whole-game catalog for an average colonist (skill 8) with every ingredient
// at the produce prior and the orbital trader kinds as buyers: the goods table
// the landing commit quotes. It asserts the ranking is non-empty and that
// every ranked good is bought by an orbital kind, never a floor good.
func TestExportRankingOnTheRecordedCatalog(t *testing.T) {
	t.Run("hand benches", func(t *testing.T) {
		exportGoodsTable(t, []string{"CraftingSpot", "HandTailoringBench", "FueledSmithy", "TableSculpting"})
	})
	t.Run("all benches", func(t *testing.T) { exportGoodsTable(t, exportBenchDefs) })
}

func exportGoodsTable(t *testing.T, benchDefs []string) {
	catalog := recordedcatalog.Catalog(t)
	items, err := catalog.ItemFacts()
	if err != nil {
		t.Fatal(err)
	}
	recipeClass := (&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()
	var names []string
	for name := range catalog.Defs[recipeClass] {
		names = append(names, name)
	}
	sort.Strings(names)
	var benches []policy.GearBench
	for _, bench := range benchDefs {
		var recipes []policy.GearRecipe
		skipped := map[string]int{}
		for _, name := range names {
			row, err := catalog.Recipe(name)
			if err != nil || !slices.Contains(row.GetRecipeUsers(), bench) {
				continue
			}
			recipe := policy.GearRecipe{Definition: name, Available: domain.Known(true), AvailableOn: domain.Known(true)}
			if recipe.Role, err = catalog.RecipeRole(name); err != nil {
				skipped[err.Error()]++
				continue
			}
			if recipe.Products, err = catalog.RecipeProducts(name); err != nil {
				skipped[err.Error()]++
				continue
			}
			if recipe.Ingredients, err = catalog.RecipeIngredients(name); err != nil {
				skipped[err.Error()]++
				continue
			}
			if recipe.RequiredWork, err = catalog.RecipeWork(name, bench); err != nil {
				skipped[err.Error()]++
				continue
			}
			if recipe.WorkAmount, err = catalog.RecipeWorkAmount(name); err != nil {
				skipped[err.Error()]++
				continue
			}
			if recipe.MechKind, err = catalog.RecipeMechKind(name); err != nil {
				skipped[err.Error()]++
				continue
			}
			if recipe.Kind, err = catalog.RecipeLedgerKind(name, recipe.MechKind); err != nil {
				skipped[err.Error()]++
				continue
			}
			recipes = append(recipes, recipe)
		}
		t.Logf("%s: %d recipes, skipped %v", bench, len(recipes), skipped)
		benches = append(benches, policy.GearBench{ID: bench + "_1", Def: bench, Usable: domain.Known(true), Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known(recipes)})
	}
	skills := map[string]policy.ProfileSkill{}
	for _, skill := range []string{"Crafting", "Artistic", "Construction", "Mining", "Cooking", "Plants"} {
		skills[skill] = policy.ProfileSkill{Name: skill, Level: 8}
	}
	request := policy.ExportRequest{
		Benches: benches, Profiles: domain.Known([]policy.PawnProfile{{ID: "p1", Skills: skills, Incapable: map[policy.WorkType]bool{}}}),
		Items: items, Gap: domain.Known(5000.0), Packed: domain.Known[int64](0), Stock: map[policy.Resource]int64{},
		SurplusKnown: true, Buys: map[policy.Resource]domain.Fact[bool]{}, Sources: map[policy.Resource][]policy.SupplyCandidate{},
	}
	products := policy.DeclareExportOrders(request).Products
	var kinds []string
	for name := range catalog.Defs[(&d.TraderKindDef{}).ProtoReflect().Descriptor().FullName()] {
		if strings.HasPrefix(name, "Orbital_") {
			kinds = append(kinds, name)
		}
	}
	sort.Strings(kinds)
	cash, buys := exportBuyers(catalog, kinds, products)
	request.Buys, request.CashCap, request.WorkFor = buys, cash, exportWorkFor(catalog)
	var unbought []string
	for _, p := range products {
		if v, known := buys[p].Value(); !known || !v {
			unbought = append(unbought, string(p)+map[bool]string{true: "?", false: ""}[!known])
		}
	}
	t.Logf("craftable sale goods %d, not bought by an orbital kind: %v", len(products), unbought)
	ranked := policy.RankExports(request)
	if len(ranked) == 0 {
		t.Fatal("nothing ranked")
	}
	cashCap, _ := cash.Value()
	t.Logf("orbital kinds %v, cash cap %d, %d ranked goods", kinds, cashCap, len(ranked))
	t.Logf("%-28s %-34s %-22s %-14s %8s %8s %9s %10s", "product", "recipe", "bench", "stuff", "value", "net", "ticks", "net/tick")
	for i, c := range ranked {
		if i >= 12 {
			break
		}
		t.Logf("%-28s %-34s %-22s %-14s %8.1f %8.1f %9.0f %10.5f", c.Product, c.Recipe, c.BenchKind, c.Stuff, c.Value, c.Net, c.Ticks, c.Score)
	}
}
