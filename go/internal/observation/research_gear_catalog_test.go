package observation

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
)

// The research ladder is the recorded ResearchProjectDef rows: every row
// is a project of the catalog, with the row's prerequisites, bench and
// knowledge category, and a finite cost for a knowledge project.
func TestRecordedResearchLadderIsTheDefRows(t *testing.T) {
	catalog := recordedcatalog.Catalog(t)
	rows := recordedcatalog.Wire(t).GetDefs().GetResearchProjectDefs()
	if len(rows) < 100 || len(catalog.Research) != len(rows) {
		t.Fatalf("%d rows, %d projects", len(rows), len(catalog.Research))
	}
	knowledge := 0
	for _, row := range rows {
		facts, ok := catalog.Research[row.GetDefName()]
		if !ok {
			t.Fatal("no project for row", row.GetDefName())
		}
		prerequisites, _ := facts.Prerequisites.Value()
		if len(prerequisites) != len(row.GetPrerequisites()) || facts.RequiredBuilding != row.GetRequiredResearchBuilding() || facts.KnowledgeCategory != row.GetKnowledgeCategory() {
			t.Fatal("project differs from its row", row.GetDefName(), facts)
		}
		if facts.KnowledgeCategory != "" {
			knowledge++
			if facts.Cost != float64(row.GetKnowledgeCost()) || facts.Cost <= 0 {
				t.Fatal("knowledge cost", row.GetDefName(), facts.Cost)
			}
		} else if facts.Cost != float64(row.GetBaseCost()) {
			t.Fatal("base cost", row.GetDefName(), facts.Cost)
		}
	}
	if knowledge == 0 {
		t.Fatal("the recording holds no knowledge project")
	}
}

// A bill option's research and ingredients are its recipe's row and the def's
// adjusted cost, and the smoke-pop flag is the def's verb.
func TestRecordedApparelBillOptionsAreTheRecipeRows(t *testing.T) {
	catalog := recordedcatalog.Catalog(t)
	made := 0
	for name, row := range catalog.ThingDefs {
		if row.GetApparel() == nil {
			continue
		}
		stuff := ""
		if len(row.GetStuffCategories()) > 0 {
			eval, err := catalog.StatEvaluator()
			if err != nil {
				t.Fatal(err)
			}
			allowed, err := eval.AllowedStuffsFor(name)
			if err != nil || len(allowed) == 0 {
				continue
			}
			stuff = allowed[0]
		}
		research, ingredients, ok, err := catalog.ApparelBill(name, stuff)
		if err != nil {
			t.Fatal(name, err)
		}
		if !ok {
			continue
		}
		made++
		if len(ingredients) == 0 || !slices.IsSorted(research) {
			t.Fatal("bill option", name, research, ingredients)
		}
	}
	if made < 50 {
		t.Fatal("recipes make only", made, "apparel defs")
	}
	if !catalog.SmokePop("Apparel_SmokepopBelt") || catalog.SmokePop("Apparel_Parka") {
		t.Fatal("smoke-pop verb")
	}
	research, _, ok, err := catalog.ApparelBill("Apparel_FlakVest", "")
	if err != nil || !ok || len(research) == 0 {
		t.Fatal("flak vest", research, ok, err)
	}
}
