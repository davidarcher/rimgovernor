// Package archgate holds the mechanical architecture gates (#2497, rules 1-6 of
// docs/developers/architecture/rules.md). Each rule is a function over a Go
// module root (the go/ directory) that returns violation keys; the test
// compares them with the checked-in baseline under baseline/, which may only
// shrink: a new violation fails, and so does a baseline entry that no longer
// occurs. Gates read syntax only (imports, declarations, literals), never
// counts that reward splitting files.
package archgate

import (
	"fmt"
	"go/ast"
	"go/parser"
	"go/token"
	"math"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
)

const modulePrefix = "github.com/davidarcher/RimGovernor/go/internal/"

type file struct {
	rel  string // slash path relative to the module root
	pkg  string // first path segment under internal/ (or "cmd")
	node *ast.File
}

// load parses the non-test Go files under dirs (relative to root) that dirs
// name; recursive walks subdirectories too.
func load(root string, dirs []string, recursive bool) []file {
	var out []file
	fset := token.NewFileSet()
	for _, d := range dirs {
		base := filepath.Join(root, filepath.FromSlash(d))
		_ = filepath.WalkDir(base, func(p string, e os.DirEntry, err error) error {
			if err != nil {
				return nil
			}
			if e.IsDir() {
				if p != base && !recursive {
					return filepath.SkipDir
				}
				return nil
			}
			if !strings.HasSuffix(p, ".go") || strings.HasSuffix(p, "_test.go") {
				return nil
			}
			n, perr := parser.ParseFile(fset, p, nil, parser.SkipObjectResolution)
			if perr != nil {
				return nil
			}
			rel, _ := filepath.Rel(root, p)
			rel = filepath.ToSlash(rel)
			parts := strings.Split(rel, "/")
			pkg := parts[0]
			if pkg == "internal" && len(parts) > 1 {
				pkg = parts[1]
			}
			out = append(out, file{rel: rel, pkg: pkg, node: n})
			return nil
		})
	}
	return out
}

func sorted(set map[string]bool) []string {
	out := make([]string, 0, len(set))
	for k := range set {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// funcName is the declaration that encloses a node ("-" outside any).
func funcName(d ast.Decl) string {
	fd, ok := d.(*ast.FuncDecl)
	if !ok {
		return "-"
	}
	if fd.Recv != nil && len(fd.Recv.List) == 1 {
		t := fd.Recv.List[0].Type
		if s, ok := t.(*ast.StarExpr); ok {
			t = s.X
		}
		if id, ok := t.(*ast.Ident); ok {
			return id.Name + "." + fd.Name.Name
		}
	}
	return fd.Name.Name
}

const rulesDoc = "docs/developers/architecture/rules.md"

// Compare is the gate verdict: one message per violation missing from the
// baseline and per baseline entry that no longer occurs (the baseline only
// shrinks), each naming the rule.
func Compare(rule int, msg string, got []string, base map[string]bool) []string {
	var out []string
	seen := map[string]bool{}
	for _, v := range got {
		seen[v] = true
		if !base[v] {
			out = append(out, fmt.Sprintf("arch rule %d violated: %s: %s (see %s)", rule, msg, v, rulesDoc))
		}
	}
	for _, v := range sorted(base) {
		if !seen[v] {
			out = append(out, fmt.Sprintf("arch rule %d: baseline entry no longer occurs, delete it from baseline/rule%d.txt: %s", rule, rule, v))
		}
	}
	if limit := baselineCap(rule); len(base) > limit {
		out = append(out, fmt.Sprintf("arch rule %d: baseline has %d entries (>%d), stop and review the rule", rule, len(base), limit))
	}
	return out
}

// baselineCap is the most entries a baseline may hold before the rule needs
// review: 200, except rule 6. Rule 6 baselines every def-name and game-constant
// literal that predates the gate (#2649), which outnumbers any other rule's
// legacy; the cap is set just above today's count, so it only guards growth.
func baselineCap(rule int) int {
	if rule == 6 {
		return 450
	}
	return 200
}

// Layers is the rule 1 order, lowest first. cmd sits above all of them.
var Layers = []string{"domain", "policy", "bridge", "facts", "observation", "store", "snapshot", "executor", "buildingruntime", "httpapi", "cmd"}

// deniedEdges are role-forbidden imports even though the layer is lower:
// store persists and never reads the wire, executor does not touch the store,
// httpapi reads snapshots and does not drive the runtime, and bridge validates
// the wire without policy.
var deniedEdges = map[[2]string]bool{
	{"store", "bridge"}:            true,
	{"store", "gabp"}:              true,
	{"executor", "store"}:          true,
	{"httpapi", "buildingruntime"}: true,
	{"bridge", "policy"}:           true,
}

// Rule1 returns "from->to" for every import that climbs the layers or is a
// denied edge. Packages outside Layers (and gabp as an importee) are free.
func Rule1(root string) []string {
	rank := map[string]int{}
	for i, l := range Layers {
		rank[l] = i
	}
	bad := map[string]bool{}
	for _, f := range load(root, []string{"internal", "cmd"}, true) {
		from := f.pkg
		fr, layered := rank[from]
		if !layered {
			continue
		}
		for _, im := range f.node.Imports {
			path, _ := strconv.Unquote(im.Path.Value)
			if !strings.HasPrefix(path, modulePrefix) {
				continue
			}
			to := strings.Split(strings.TrimPrefix(path, modulePrefix), "/")[0]
			if to == from {
				continue
			}
			tr, ok := rank[to]
			if (ok && tr > fr) || deniedEdges[[2]string{from, to}] {
				bad[from+"->"+to] = true
			}
		}
	}
	return sorted(bad)
}

var impureImports = map[string]bool{"time": true, "sync": true, "sync/atomic": true, "runtime": true, "os": true}

// Rule2 returns "file|import X" or "file|go statement" for policy files that
// import time, sync, runtime, os or net, or start a goroutine.
func Rule2(root string) []string {
	bad := map[string]bool{}
	for _, f := range load(root, []string{"internal/policy"}, false) {
		for _, im := range f.node.Imports {
			path, _ := strconv.Unquote(im.Path.Value)
			if impureImports[path] || path == "net" || strings.HasPrefix(path, "net/") {
				bad[f.rel+"|import "+path] = true
			}
		}
		ast.Inspect(f.node, func(n ast.Node) bool {
			if _, ok := n.(*ast.GoStmt); ok {
				bad[f.rel+"|go statement"] = true
			}
			return true
		})
	}
	return sorted(bad)
}

// Rule3 returns each exported type name declared by two or more of domain,
// policy, bridge and observation.
func Rule3(root string) []string {
	owners := map[string]map[string]bool{}
	for _, f := range load(root, []string{"internal/domain", "internal/policy", "internal/bridge", "internal/observation"}, false) {
		for _, d := range f.node.Decls {
			gd, ok := d.(*ast.GenDecl)
			if !ok || gd.Tok != token.TYPE {
				continue
			}
			for _, s := range gd.Specs {
				if ts := s.(*ast.TypeSpec); ts.Name.IsExported() {
					if owners[ts.Name.Name] == nil {
						owners[ts.Name.Name] = map[string]bool{}
					}
					owners[ts.Name.Name][f.pkg] = true
				}
			}
		}
	}
	bad := map[string]bool{}
	for name, pk := range owners {
		if len(pk) > 1 {
			bad[name] = true
		}
	}
	return sorted(bad)
}

// Rule4 returns "file|func" for every `if err != nil` whose body holds no
// statement (the error is dropped).
func Rule4(root string) []string {
	bad := map[string]bool{}
	for _, f := range load(root, []string{"internal", "cmd"}, true) {
		for _, d := range f.node.Decls {
			name := funcName(d)
			ast.Inspect(d, func(n ast.Node) bool {
				is, ok := n.(*ast.IfStmt)
				if !ok || len(is.Body.List) != 0 {
					return true
				}
				be, ok := is.Cond.(*ast.BinaryExpr)
				if !ok || be.Op != token.NEQ {
					return true
				}
				x, xok := be.X.(*ast.Ident)
				y, yok := be.Y.(*ast.Ident)
				if xok && yok && x.Name == "err" && y.Name == "nil" {
					bad[f.rel+"|"+name] = true
				}
				return true
			})
		}
	}
	return sorted(bad)
}

var countName = regexp.MustCompile(`(?i)target|count|need|want|short|total|stock|have|qty|amount|demand`)

// literalNumber is the value of an int or float literal, or of 1 << n.
func literalNumber(e ast.Expr) (float64, bool) {
	switch v := e.(type) {
	case *ast.BasicLit:
		if v.Kind == token.INT || v.Kind == token.FLOAT {
			f, err := strconv.ParseFloat(strings.ReplaceAll(v.Value, "_", ""), 64)
			return f, err == nil
		}
	case *ast.BinaryExpr:
		if v.Op == token.SHL {
			a, aok := literalNumber(v.X)
			b, bok := literalNumber(v.Y)
			if aok && bok && b < 63 {
				return a * math.Pow(2, b), true
			}
		}
	case *ast.ParenExpr:
		return literalNumber(v.X)
	}
	return 0, false
}

func exprText(e ast.Expr) string {
	var sb strings.Builder
	ast.Inspect(e, func(n ast.Node) bool {
		switch v := n.(type) {
		case *ast.Ident:
			sb.WriteString(v.Name + " ")
		}
		return true
	})
	return sb.String()
}

// Rule5 returns "file|func|token" for a literal 10000 compared with or
// clamping a count/target-named operand, and for sentinels standing for
// unknown (1e12, 1<<40, math.MaxFloat64), in policy and buildingruntime.
func Rule5(root string) []string {
	bad := map[string]bool{}
	for _, f := range load(root, []string{"internal/policy", "internal/buildingruntime"}, true) {
		if f.pkg != "policy" && f.pkg != "buildingruntime" {
			continue
		}
		for _, d := range f.node.Decls {
			name := funcName(d)
			add := func(tok string) { bad[f.rel+"|"+name+"|"+tok] = true }
			ast.Inspect(d, func(n ast.Node) bool {
				switch v := n.(type) {
				case *ast.SelectorExpr:
					if id, ok := v.X.(*ast.Ident); ok && id.Name == "math" && v.Sel.Name == "MaxFloat64" {
						add("math.MaxFloat64")
					}
				case ast.Expr:
					if x, ok := literalNumber(v); ok && (x == 1e12 || x == float64(1<<40)) {
						add("sentinel " + strconv.FormatFloat(x, 'g', -1, 64))
					}
				}
				switch v := n.(type) {
				case *ast.BinaryExpr:
					switch v.Op {
					case token.LSS, token.GTR, token.LEQ, token.GEQ, token.EQL, token.NEQ:
						for _, p := range [][2]ast.Expr{{v.X, v.Y}, {v.Y, v.X}} {
							if x, ok := literalNumber(p[0]); ok && x == 10000 && countName.MatchString(exprText(p[1])) {
								add("10000 compared")
							}
						}
					}
				case *ast.CallExpr:
					if fn := exprText(v.Fun); strings.TrimSpace(fn) == "min" || strings.TrimSpace(fn) == "max" || strings.TrimSpace(fn) == "math Min" || strings.TrimSpace(fn) == "math Max" {
						lit, other := false, false
						for _, a := range v.Args {
							if x, ok := literalNumber(a); ok && x == 10000 {
								lit = true
							} else if countName.MatchString(exprText(a)) {
								other = true
							}
						}
						if lit && other {
							add("10000 clamp")
						}
					}
				}
				return true
			})
		}
	}
	return sorted(bad)
}
