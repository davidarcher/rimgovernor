package bridge

import (
	"errors"
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

func roofCatalog(rows ...*d.RoofDef) *DefinitionCatalog {
	byName := map[string]proto.Message{}
	for _, row := range rows {
		byName[row.GetDefName()] = row
	}
	return &DefinitionCatalog{Defs: map[protoreflect.FullName]map[string]proto.Message{(&d.RoofDef{}).ProtoReflect().Descriptor().FullName(): byName}}
}

func TestRoofRules(t *testing.T) {
	t.Parallel()
	rules, err := roofCatalog(
		&d.RoofDef{DefName: "A", IsNatural: true, IsThickRoof: true},
		&d.RoofDef{DefName: "B", IsNatural: true, CanCollapse: true},
		&d.RoofDef{DefName: "C", CanCollapse: true},
	).RoofRules()
	if err != nil {
		t.Fatal(err)
	}
	if len(rules) != 3 || rules["A"].Removable() || !rules["B"].Removable() || !rules["C"].Removable() {
		t.Fatalf("%+v", rules)
	}
	var none *DefinitionCatalog
	for _, c := range []*DefinitionCatalog{none, roofCatalog()} {
		if _, err := c.RoofRules(); !errors.Is(err, ErrRoofRules) {
			t.Fatal(err)
		}
	}
}
