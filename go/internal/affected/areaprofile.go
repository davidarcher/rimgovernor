package affected

import (
	"go/ast"
	"go/token"
	"slices"
	"sort"
	"strings"
)

// areaProfile is how a case area drives the rimgovernor binary: Binary is
// set when its sources host `rimgovernor serve` (a Serve spec or Service
// mark on a case, na.ServiceLaunch, na.LaunchService or the session's
// Rimgovernor binary); a bridge-only area never runs the binary, so no
// change to it reaches the area. Families are the routine families the
// sources compose: the values every Families field is set to, resolved
// through package-level constants, comma-joined lists split; when a value
// cannot be resolved (a helper's result, a field), every known family name
// among the package's string literals stands in. AllFamilies is set when
// no file sets a Families field, or one sets it to nil: the area composes
// serve with every family.
type areaProfile struct {
	Binary      bool
	Families    []string
	AllFamilies bool
}

// composes reports whether a change scoped to family reaches the area.
func (p areaProfile) composes(family string) bool {
	return p.Binary && (p.AllFamilies || slices.Contains(p.Families, family))
}

// serveIdents are the identifiers an area's sources use to host the binary.
var serveIdents = map[string]bool{"ServeSpec": true, "ServiceLaunch": true, "LaunchService": true, "Rimgovernor": true, "Serve": true, "Service": true}

// readAreaProfile scans a case area's directory.
func readAreaProfile(dir string) (areaProfile, error) {
	files, err := parseDir(dir, true)
	if err != nil {
		return areaProfile{}, err
	}
	known := map[string]bool{}
	for _, name := range routineFamilyNames() {
		known[name] = true
	}
	values := map[string]ast.Expr{} // package-level constant or variable -> its value
	for _, file := range files {
		for _, decl := range file.Decls {
			decl, ok := decl.(*ast.GenDecl)
			if !ok {
				continue
			}
			for _, spec := range decl.Specs {
				if spec, ok := spec.(*ast.ValueSpec); ok && len(spec.Values) == len(spec.Names) {
					for i, name := range spec.Names {
						values[name.Name] = spec.Values[i]
					}
				}
			}
		}
	}
	var profile areaProfile
	set := map[string]bool{}
	literals := map[string]bool{}
	named, resolved := false, true
	r := &resolver{known: known, values: values}
	for _, file := range files {
		ast.Inspect(file, func(n ast.Node) bool {
			switch n := n.(type) {
			case *ast.Ident:
				if serveIdents[n.Name] {
					profile.Binary = true
				}
			case *ast.BasicLit:
				for _, part := range r.parts(n) {
					literals[part] = true
				}
			case *ast.KeyValueExpr:
				if key, ok := n.Key.(*ast.Ident); ok && key.Name == "Families" {
					named = true
					resolved = r.resolve(n.Value, set) && resolved
				}
			case *ast.AssignStmt:
				for i, lhs := range n.Lhs {
					if sel, ok := lhs.(*ast.SelectorExpr); ok && sel.Sel.Name == "Families" && i < len(n.Rhs) {
						named = true
						resolved = r.resolve(n.Rhs[i], set) && resolved
					}
				}
			}
			return true
		})
	}
	profile.AllFamilies = !named || r.nilValue
	if !resolved {
		set = literals
	}
	for family := range set {
		profile.Families = append(profile.Families, family)
	}
	sort.Strings(profile.Families)
	return profile, nil
}

// resolver reads family names out of a Families value.
type resolver struct {
	known  map[string]bool
	values map[string]ast.Expr
	// nilValue is set when a Families value is nil: serve composes every
	// family.
	nilValue bool
}

// resolve adds the known family names a value names to set and reports
// whether the whole value was readable: string literals, lists and appends
// of them, and package-level names bound to them.
func (r *resolver) resolve(expr ast.Expr, set map[string]bool) bool {
	switch expr := expr.(type) {
	case *ast.BasicLit:
		for _, part := range r.parts(expr) {
			set[part] = true
		}
		return true
	case *ast.CompositeLit:
		ok := true
		for _, elt := range expr.Elts {
			ok = r.resolve(elt, set) && ok
		}
		return ok
	case *ast.CallExpr:
		if fun, ok := expr.Fun.(*ast.Ident); !ok || fun.Name != "append" {
			return false
		}
		ok := true
		for _, arg := range expr.Args {
			ok = r.resolve(arg, set) && ok
		}
		return ok
	case *ast.Ident:
		if expr.Name == "nil" {
			r.nilValue = true
			return true
		}
		value, ok := r.values[expr.Name]
		return ok && r.resolve(value, set)
	case *ast.ParenExpr:
		return r.resolve(expr.X, set)
	}
	return false
}

// parts is the known family names a string literal lists.
func (r *resolver) parts(lit *ast.BasicLit) []string {
	if lit.Kind != token.STRING {
		return nil
	}
	var out []string
	for _, part := range strings.Split(strings.Trim(lit.Value, "\"`"), ",") {
		if part = strings.TrimSpace(part); r.known[part] {
			out = append(out, part)
		}
	}
	return out
}
