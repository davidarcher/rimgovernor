package buildingruntime

import (
	"go/ast"
	"go/parser"
	"go/token"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestVerdictCallSitesNameKindsTheirOutcomeAccepts reads every waitFor,
// waitOn and refuse call in the package and checks that a policy.CauseX first
// argument is in the closed set its outcome accepts. mustValid panics on a bad
// one at run time (#2812: unsafe_threat as a wait); this catches it at test
// time even on a branch no fixture reaches.
func TestVerdictCallSitesNameKindsTheirOutcomeAccepts(t *testing.T) {
	t.Parallel()
	values := causeValues(t)
	fset := token.NewFileSet()
	files, err := filepath.Glob("*.go")
	if err != nil {
		t.Fatal(err)
	}
	sites := 0
	for _, file := range files {
		if strings.HasSuffix(file, "_test.go") {
			continue
		}
		parsed, err := parser.ParseFile(fset, file, nil, 0)
		if err != nil {
			t.Fatal(err)
		}
		ast.Inspect(parsed, func(n ast.Node) bool {
			call, ok := n.(*ast.CallExpr)
			if !ok || len(call.Args) == 0 {
				return true
			}
			fn, ok := call.Fun.(*ast.Ident)
			if !ok {
				return true
			}
			var allowed []policy.Cause
			switch fn.Name {
			case "waitFor", "waitOn":
				allowed = waitKinds
			case "refuse":
				allowed = refusalKinds
			default:
				return true
			}
			sel, ok := call.Args[0].(*ast.SelectorExpr)
			if !ok {
				return true
			}
			if pkg, ok := sel.X.(*ast.Ident); !ok || pkg.Name != "policy" {
				return true
			}
			sites++
			cause, known := values[sel.Sel.Name]
			if !known {
				t.Errorf("%s: %s names policy.%s, which is no Cause constant", fset.Position(call.Pos()), fn.Name, sel.Sel.Name)
			} else if !slices.Contains(allowed, cause) {
				t.Errorf("%s: %s(policy.%s) is rejected by Verdict.Validate (%q is not in its closed set)", fset.Position(call.Pos()), fn.Name, sel.Sel.Name, cause)
			}
			return true
		})
	}
	if sites < 100 {
		t.Fatalf("found only %d literal call sites; the scan is broken", sites)
	}
}

// causeValues maps each policy Cause constant name to its value by reading
// policy/cause.go.
func causeValues(t *testing.T) map[string]policy.Cause {
	t.Helper()
	parsed, err := parser.ParseFile(token.NewFileSet(), filepath.Join("..", "policy", "cause.go"), nil, 0)
	if err != nil {
		t.Fatal(err)
	}
	out := map[string]policy.Cause{}
	for _, decl := range parsed.Decls {
		gen, ok := decl.(*ast.GenDecl)
		if !ok || gen.Tok != token.CONST {
			continue
		}
		for _, spec := range gen.Specs {
			value := spec.(*ast.ValueSpec)
			for i, name := range value.Names {
				if i >= len(value.Values) {
					continue
				}
				if lit, ok := value.Values[i].(*ast.BasicLit); ok && lit.Kind == token.STRING {
					s, err := strconv.Unquote(lit.Value)
					if err != nil {
						t.Fatal(err)
					}
					out[name.Name] = policy.Cause(s)
				}
			}
		}
	}
	return out
}
