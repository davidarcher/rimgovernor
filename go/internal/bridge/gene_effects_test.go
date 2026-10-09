package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// TestBiotechGeneEffects: active genes combine their typed stat and
// need effects from the catalog rows; an inactive gene adds nothing and an
// undefined gene fails.
func TestBiotechGeneEffects(t *testing.T) {
	stat := func(stat string, value float32) *d.Opt_StatModifier {
		return &d.Opt_StatModifier{Value: &d.StatModifier{Stat: stat, Value: value}}
	}
	cat := &BiotechCatalog{Genes: map[string]*d.GeneDef{
		"A": {DefName: "A", StatOffsets: []*d.Opt_StatModifier{stat("S", .1)}, StatFactors: []*d.Opt_StatModifier{stat("S", 2)}, DisablesNeeds: []string{"Rest"}},
		"B": {DefName: "B", StatOffsets: []*d.Opt_StatModifier{stat("S", .2)}, EnablesNeeds: []string{"Deathrest"}},
		"C": {DefName: "C", StatFactors: []*d.Opt_StatModifier{stat("S", 9)}, DisablesNeeds: []string{"Joy"}},
	}}
	gene := func(name string, active bool) policy.PawnGene {
		return policy.PawnGene{Name: name, Active: domain.Known(active)}
	}
	got, err := cat.GeneEffects([]policy.PawnGene{gene("A", true), gene("B", true), gene("C", false)})
	if err != nil {
		t.Fatal(err)
	}
	if m := got.Stat("S"); m.Factor != 2 || m.Offset < .299 || m.Offset > .301 {
		t.Fatalf("stat %v", m)
	}
	if !got.NeedDisabled("Rest") || got.NeedDisabled("Joy") || !got.EnabledNeeds["Deathrest"] {
		t.Fatalf("needs %v", got)
	}
	if _, err := cat.GeneEffects([]policy.PawnGene{gene("Nope", true)}); err == nil {
		t.Fatal("undefined gene accepted")
	}
	if _, err := (*BiotechCatalog)(nil).GeneEffects([]policy.PawnGene{gene("A", true)}); err == nil {
		t.Fatal("gene without a catalog accepted")
	}
}
