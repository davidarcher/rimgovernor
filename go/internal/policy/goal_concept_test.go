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
	found := 0
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
					found++
					if GoalConcept(GoalID(value)) == ConceptUnknown {
						t.Errorf("%s: GoalID %s (%q) has no concept", n, name.Name, value)
					}
					if GoalDomain(GoalID(value)) == DomainUnknown {
						t.Errorf("%s: GoalID %s (%q) has no domain", n, name.Name, value)
					}
				}
			}
		}
	}
	if found != len(goalConcepts) {
		t.Errorf("found %d GoalID constants, classified %d", found, len(goalConcepts))
	}
	if found != len(goalDomains) {
		t.Errorf("found %d GoalID constants, tagged %d with a domain", found, len(goalDomains))
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
