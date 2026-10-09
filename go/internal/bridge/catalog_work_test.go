package bridge

import (
	"fmt"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// retiredWorkSkills is policy.WorkSkillName as it stood before the rows
// replaced it: the skill each Core work type was scored by, typed by
// hand.
var retiredWorkSkills = map[string]string{
	"Doctor": "Medicine", "Warden": "Social", "Handling": "Animals", "Fishing": "Animals",
	"Cooking": "Cooking", "Hunting": "Shooting", "Construction": "Construction",
	"Growing": "Plants", "PlantCutting": "Plants", "Mining": "Mining",
	"Smithing": "Crafting", "Tailoring": "Crafting", "Crafting": "Crafting",
	"Art": "Artistic", "Research": "Intellectual",
}

// retiredWorkOrder is policy's workOrder before the rows replaced it: the
// fifteen Core work types the planner filled owners in.
var retiredWorkOrder = []string{"Doctor", "Warden", "Handling", "Cooking", "Hunting", "Fishing", "Construction", "Growing", "Mining", "PlantCutting", "Smithing", "Tailoring", "Art", "Crafting", "Research"}

// TestWorkTypeRowsMatchTheRetiredSkillTable resolves every work type of the
// full game recording: the skill of each type the retired table listed is
// reproduced, and the types it did not list (unskilled work, DLC work) are
// the ones whose rows carry another skill or none.
func TestWorkTypeRowsMatchTheRetiredSkillTable(t *testing.T) {
	catalog := fullCatalog(t)
	var diffs []string
	for name := range catalogDefs[*d.WorkTypeDef](catalog) {
		got, err := catalog.WorkTypeSkill(name)
		if err != nil {
			t.Fatal(err)
		}
		if want := retiredWorkSkills[name]; got != want {
			diffs = append(diffs, fmt.Sprintf("%s: row skill %q, retired table %q", name, got, want))
		}
	}
	slices.Sort(diffs)
	// Childcare (Biotech) and DarkStudy (Anomaly) are DLC work the table never
	// listed: the rows give Social and Intellectual.
	want := []string{"Childcare: row skill \"Social\", retired table \"\"", "DarkStudy: row skill \"Intellectual\", retired table \"\""}
	if !slices.Equal(diffs, want) {
		t.Errorf("work type skills differ from the retired table: %q", diffs)
	}
	if _, err := catalog.WorkTypeSkill("VTE_SomeModWork"); err == nil {
		t.Error("a work type the catalog lacks resolved")
	}
}

// TestWorkTypeNaturalOrderMatchesTheRetiredList sorts the work types by their
// natural priority (highest first, the planner's order) and compares the
// fifteen the retired list held with it. The recording has one difference:
// Fishing (Odyssey, naturalPriority 350) sorts after Crafting, where the
// retired list had it between Hunting and Construction.
func TestWorkTypeNaturalOrderMatchesTheRetiredList(t *testing.T) {
	catalog := fullCatalog(t)
	var rows []policy.WorkPriority
	for name := range catalogDefs[*d.WorkTypeDef](catalog) {
		row := policy.WorkPriority{Work: policy.WorkType(name)}
		if err := catalog.ResolveWorkRow(&row); err != nil {
			t.Fatal(err)
		}
		rows = append(rows, row)
	}
	slices.SortFunc(rows, func(a, b policy.WorkPriority) int {
		if a.Order != b.Order {
			return b.Order - a.Order
		}
		return int(slices.Compare([]byte(a.Work), []byte(b.Work)))
	})
	var derived []string
	for _, row := range rows {
		if slices.Contains(retiredWorkOrder, string(row.Work)) {
			derived = append(derived, string(row.Work))
		}
	}
	wantDerived := slices.Clone(retiredWorkOrder)
	// The retired list placed Fishing before Construction.
	wantDerived = slices.DeleteFunc(wantDerived, func(s string) bool { return s == "Fishing" })
	at := slices.Index(wantDerived, "Crafting") + 1
	wantDerived = slices.Insert(wantDerived, at, "Fishing")
	if !slices.Equal(derived, wantDerived) {
		t.Errorf("derived order %v, want the retired list with Fishing after Crafting %v", derived, wantDerived)
	}
}
