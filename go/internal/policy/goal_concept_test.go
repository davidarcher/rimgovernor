package policy

import (
	"go/ast"
	"go/parser"
	"go/token"
	"os"
	"strconv"
	"strings"
	"testing"
)

// Every GoalID constant declared in this package must be classified.
func TestGoalConceptCoversEveryGoalID(t *testing.T) {
	fset := token.NewFileSet()
	entries, err := os.ReadDir(".")
	if err != nil {
		t.Fatal(err)
	}
	var ids []GoalID
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
				if typ, ok := v.Type.(*ast.Ident); !ok || typ.Name != "GoalID" {
					continue
				}
				for i, name := range v.Names {
					lit, ok := v.Values[i].(*ast.BasicLit)
					if !ok {
						t.Fatalf("%s: GoalID %s is not a string literal", n, name.Name)
					}
					value, _ := strconv.Unquote(lit.Value)
					ids = append(ids, GoalID(value))
				}
			}
		}
	}
	if err := ValidateGoalDetectors(goalDetectors, ids); err != nil {
		t.Error(err)
	}
	if GoalDomain(EnsureMood) != DomainPeople || GoalDomain("NotAGoal") != DomainUnknown {
		t.Error("GoalDomain misclassifies a mood goal or an unknown id")
	}
	for id, want := range map[GoalID]Concept{
		EnsureMood:         ConceptResponse,
		EnsureFoodSupply:   ConceptStandard,
		EnsureBasicDefense: ConceptStandard,
		TradeWithCaravan:   ConceptResponse,
		EnsureCooking:      ConceptProject,
		MaintainWaste:      ConceptStandard,
		"NotAGoal":         ConceptUnknown,
	} {
		if got := GoalConcept(id); got != want {
			t.Errorf("GoalConcept(%s) = %q, want %q", id, got, want)
		}
	}
}

func TestValidateGoalDetectorsRefusesFaults(t *testing.T) {
	noop := func(*routineRun) error { return nil }
	ok := GoalDetector{"A", ConceptStandard, DomainFood, []FactFamily{FactColony}, noop}
	for name, c := range map[string]struct {
		ds  []GoalDetector
		ids []GoalID
	}{
		"missing detector": {nil, []GoalID{"A"}},
		"duplicate":        {[]GoalDetector{ok, ok}, []GoalID{"A"}},
		"no inputs":        {[]GoalDetector{{"A", ConceptStandard, DomainFood, nil, noop}}, []GoalID{"A"}},
		"unknown input":    {[]GoalDetector{{"A", ConceptStandard, DomainFood, []FactFamily{"x"}, noop}}, []GoalID{"A"}},
		"no concept":       {[]GoalDetector{{"A", ConceptUnknown, DomainFood, []FactFamily{FactColony}, noop}}, []GoalID{"A"}},
		"no domain":        {[]GoalDetector{{"A", ConceptStandard, DomainUnknown, []FactFamily{FactColony}, noop}}, []GoalID{"A"}},
		"no detect":        {[]GoalDetector{{"A", ConceptStandard, DomainFood, []FactFamily{FactColony}, nil}}, []GoalID{"A"}},
		"stray detector":   {[]GoalDetector{ok}, nil},
	} {
		if ValidateGoalDetectors(c.ds, c.ids) == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	if err := ValidateGoalDetectors([]GoalDetector{ok}, []GoalID{"A"}); err != nil {
		t.Error(err)
	}
}
