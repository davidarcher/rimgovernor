package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// TestBiotechGeneEffects: active genes combine their typed stat and
// need effects from the catalog rows; an inactive gene adds nothing and an
// undefined gene fails.
func TestBiotechGeneEffects(t *testing.T) {
	cat, err := DecodeBiotechCatalog(&o.BiotechCatalog{Genes: []*o.GeneRow{
		{DefName: proto.String("A"), Effects: []*o.StatEffect{{Stat: proto.String("S"), Offset: proto.Float64(.1)}, {Stat: proto.String("S"), Factor: proto.Float64(2)}}, DisablesNeeds: []string{"Rest"}},
		{DefName: proto.String("B"), Effects: []*o.StatEffect{{Stat: proto.String("S"), Offset: proto.Float64(.2)}}, EnablesNeeds: []string{"Deathrest"}},
		{DefName: proto.String("C"), Effects: []*o.StatEffect{{Stat: proto.String("S"), Factor: proto.Float64(9)}}, DisablesNeeds: []string{"Joy"}},
	}})
	if err != nil {
		t.Fatal(err)
	}
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
