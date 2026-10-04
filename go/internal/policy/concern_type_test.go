package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"go/ast"
	"go/parser"
	"go/token"
	"os"
	"strconv"
	"strings"
	"testing"
)

// Every ConcernID constant declared in this package must be classified.
func TestConcernTypeOfCoversEveryConcernID(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	fset := token.NewFileSet()
	entries, err := os.ReadDir(".")
	if err != nil {
		t.Fatal(err)
	}
	var ids []ConcernID
	for _, e := range entries {
		n := e.Name()
		if !strings.HasSuffix(n, ".go") || strings.HasSuffix(n, "_test.go") {
			continue
		}
		f, err := parser.ParseFile(fset, n, nil, 0)
		if err != nil {
			t.Fatal(err)
		}
		for _, d := range f.Decls {
			g, ok := d.(*ast.GenDecl)
			if !ok || g.Tok != token.CONST {
				continue
			}
			for _, s := range g.Specs {
				v := s.(*ast.ValueSpec)
				if typ, ok := v.Type.(*ast.Ident); !ok || typ.Name != "ConcernID" {
					continue
				}
				for i, name := range v.Names {
					lit, ok := v.Values[i].(*ast.BasicLit)
					if !ok {
						t.Fatalf("%s: ConcernID %s is not a string literal", n, name.Name)
					}
					value, _ := strconv.Unquote(lit.Value)
					ids = append(ids, ConcernID(value))
				}
			}
		}
	}
	if err := ValidateInspections(inspections, ids); err != nil {
		t.Error(err)
	}
	if DepartmentOf(EnsureMood) != DepartmentPeople || DepartmentOf("NotAGoal") != DepartmentUnknown {
		t.Error("DepartmentOf misclassifies a mood goal or an unknown id")
	}
	for id, want := range map[ConcernID]ConcernType{
		EnsureMood:         IncidentConcern,
		EnsureFoodSupply:   StandardConcern,
		EnsureBasicDefense: StandardConcern,
		TradeWithCaravan:   IncidentConcern,
		EnsureCooking:      ProjectConcern,
		MaintainWaste:      StandardConcern,
		"NotAGoal":         UnknownConcern,
	} {
		if got := ConcernTypeOf(id); got != want {
			t.Errorf("ConcernTypeOf(%s) = %q, want %q", id, got, want)
		}
	}
}

func TestValidateInspectionsRefusesFaults(t *testing.T) {
	noop := func(*roundsRun) error { return nil }
	ok := Inspection{"A", StandardConcern, DepartmentFood, []FactFamily{FactColony}, noop}
	for name, c := range map[string]struct {
		ds  []Inspection
		ids []ConcernID
	}{
		"missing inspection": {nil, []ConcernID{"A"}},
		"duplicate":          {[]Inspection{ok, ok}, []ConcernID{"A"}},
		"no inputs":          {[]Inspection{{"A", StandardConcern, DepartmentFood, nil, noop}}, []ConcernID{"A"}},
		"unknown input":      {[]Inspection{{"A", StandardConcern, DepartmentFood, []FactFamily{"x"}, noop}}, []ConcernID{"A"}},
		"no type":            {[]Inspection{{"A", UnknownConcern, DepartmentFood, []FactFamily{FactColony}, noop}}, []ConcernID{"A"}},
		"no department":      {[]Inspection{{"A", StandardConcern, DepartmentUnknown, []FactFamily{FactColony}, noop}}, []ConcernID{"A"}},
		"no inspect":         {[]Inspection{{"A", StandardConcern, DepartmentFood, []FactFamily{FactColony}, nil}}, []ConcernID{"A"}},
		"stray inspection":   {[]Inspection{ok}, nil},
	} {
		if ValidateInspections(c.ds, c.ids) == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	if err := ValidateInspections([]Inspection{ok}, []ConcernID{"A"}); err != nil {
		t.Error(err)
	}
}
