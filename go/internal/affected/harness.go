package affected

import (
	"fmt"
	"go/ast"
	"go/importer"
	"go/parser"
	"go/token"
	"go/types"
	"io"
	"os"
	"path/filepath"
	"slices"
	"sort"
	"strings"
	"sync"
)

// harnessDir is the acceptance harness package (imported as na by the
// runner, every case area and the harness helpers), relative to go/.
const harnessDir = "internal/nativeaccept"

// harnessFile reports whether a repo-relative changed file is a non-test
// source of the harness package itself (not of a subpackage).
func harnessFile(file string) bool {
	file = filepath.ToSlash(file)
	return strings.TrimPrefix(filepath.ToSlash(filepath.Dir(file)), "go/") == harnessDir &&
		strings.HasSuffix(file, ".go") && !strings.HasSuffix(file, "_test.go")
}

// checkedPackage is one type-checked package: its types, the resolved
// identifiers of its sources and the sources themselves.
type checkedPackage struct {
	Types  *types.Package
	Info   *types.Info
	Syntax []*ast.File
}

// harnessTypes is the type-checked harness package and every package
// importing it directly, loaded once per process: a change to a harness
// source is scoped by the objects it reaches, which needs resolved
// selectors (which Session method, which field), not names.
type harnessTypes struct {
	fset    *token.FileSet
	harness *checkedPackage
	users   map[string]*checkedPackage // import path -> direct importer of the harness

	spansOnce sync.Once
	spans     []typeSpan // package-level type declarations, for fieldOwner
}

var (
	harnessTypesMu sync.Mutex
	harnessTypesBy = map[string]*harnessTypes{} // go dir -> loaded packages
)

// loadHarnessTypes type-checks, from source and in dependency order, the
// harness package and every in-module package depending on it, so that
// their resolved identifiers share one set of harness objects; everything
// else comes from the export data `go list -export` builds. Only the
// direct importers are kept.
func loadHarnessTypes(goDir string, g *graph) (*harnessTypes, error) {
	harnessTypesMu.Lock()
	defer harnessTypesMu.Unlock()
	if ht, ok := harnessTypesBy[goDir]; ok {
		return ht, nil
	}
	harness := g.module + "/" + harnessDir
	dependents := map[string]bool{harness: true}
	for pkg := range g.deps {
		if slices.Contains(directClosure(g, pkg), harness) {
			dependents[pkg] = true
		}
	}
	patterns := make([]string, 0, len(dependents))
	for pkg := range dependents {
		patterns = append(patterns, pkg)
	}
	sort.Strings(patterns)
	// One go list call builds the export data of every dependency and
	// names each package's sources.
	out, err := goOutput(goDir, append([]string{"list", "-export", "-deps", "-f", `{{.ImportPath}}|{{.Dir}}|{{.Export}}|{{join .GoFiles ","}}`}, patterns...)...)
	if err != nil {
		return nil, err
	}
	exports := map[string]string{}
	sources := map[string][]string{}
	for _, line := range strings.Split(out, "\n") {
		fields := strings.SplitN(strings.TrimSpace(line), "|", 4)
		if len(fields) != 4 {
			continue
		}
		exports[fields[0]] = fields[2]
		for _, name := range strings.Split(fields[3], ",") {
			if name != "" {
				sources[fields[0]] = append(sources[fields[0]], filepath.Join(fields[1], name))
			}
		}
	}
	fset := token.NewFileSet()
	imp := &exportImporter{checked: map[string]*types.Package{}}
	imp.gc = importer.ForCompiler(fset, "gc", func(path string) (io.ReadCloser, error) {
		file, ok := exports[path]
		if !ok || file == "" {
			return nil, fmt.Errorf("no export data for %s", path)
		}
		return os.Open(file)
	})
	ht := &harnessTypes{fset: fset, users: map[string]*checkedPackage{}}
	checked := map[string]bool{}
	var check func(pkg string) error
	check = func(pkg string) error {
		if checked[pkg] || !dependents[pkg] {
			return nil
		}
		checked[pkg] = true
		for _, dep := range g.direct[pkg] {
			if err := check(dep); err != nil {
				return err
			}
		}
		var files []*ast.File
		for _, name := range sources[pkg] {
			file, err := parser.ParseFile(fset, name, nil, parser.SkipObjectResolution)
			if err != nil {
				return err
			}
			files = append(files, file)
		}
		info := &types.Info{Defs: map[*ast.Ident]types.Object{}, Uses: map[*ast.Ident]types.Object{}}
		cfg := types.Config{Importer: imp}
		typed, err := cfg.Check(pkg, fset, files, info)
		if err != nil {
			return fmt.Errorf("type-checking %s: %w", pkg, err)
		}
		imp.checked[pkg] = typed
		cp := &checkedPackage{Types: typed, Info: info, Syntax: files}
		if pkg == harness {
			ht.harness = cp
		} else if slices.Contains(g.direct[pkg], harness) {
			ht.users[pkg] = cp
		}
		return nil
	}
	for _, pkg := range patterns {
		if err := check(pkg); err != nil {
			return nil, err
		}
	}
	harnessTypesBy[goDir] = ht
	return ht, nil
}

// directClosure is every package reachable from pkg through non-test
// imports.
func directClosure(g *graph, pkg string) []string {
	return g.closure(pkg, func(string) bool { return false })
}

// exportImporter serves the packages checked from source and reads every
// other from export data.
type exportImporter struct {
	checked map[string]*types.Package
	gc      types.Importer
}

func (i *exportImporter) Import(path string) (*types.Package, error) {
	if pkg, ok := i.checked[path]; ok {
		return pkg, nil
	}
	return i.gc.Import(path)
}

// taint is the harness surface a change to the named harness sources
// reaches: each tainted top-level object (a function, method, type,
// constant or variable, a struct field counting as its type) maps to the
// changed file it traces to. The roots are the declarations the change
// edited or added (changedDecls: compared to base, or every declaration
// of a file base does not hold or that is unchanged, so a what-if query
// names whole files); an object whose declaration references a tainted
// one is tainted in turn, since its behaviour may depend on the change.
// What a user of the package observes the change through is any tainted
// object.
func (ht *harnessTypes) taint(repo, base string, changed []string) map[types.Object]string {
	info := ht.harness.Info
	users := map[types.Object]map[types.Object]bool{} // object -> objects whose declarations reference it
	roots := map[string][]types.Object{}              // changed file -> its edited declarations
	sorted := append([]string{}, changed...)
	sort.Strings(sorted)
	for _, file := range ht.harness.Syntax {
		name := ht.fset.Position(file.Pos()).Filename
		for _, changed := range sorted {
			if filepath.Base(changed) == filepath.Base(name) {
				roots[changed] = ht.changedDecls(repo, base, changed, file)
			}
		}
		for _, decl := range file.Decls {
			ast.Inspect(decl, func(n ast.Node) bool {
				ident, ok := n.(*ast.Ident)
				if !ok {
					return true
				}
				used := ht.topLevel(info.Uses[ident])
				if used == nil {
					return true
				}
				for _, obj := range declaredObjects(info, decl) {
					if obj == used {
						continue
					}
					if users[used] == nil {
						users[used] = map[types.Object]bool{}
					}
					users[used][obj] = true
				}
				return true
			})
		}
	}
	tainted := map[types.Object]string{}
	for _, file := range sorted {
		queue := append([]types.Object{}, roots[file]...)
		for len(queue) > 0 {
			next := queue[0]
			queue = queue[1:]
			if _, ok := tainted[next]; ok {
				continue
			}
			tainted[next] = file
			for user := range users[next] {
				queue = append(queue, user)
			}
		}
	}
	return tainted
}

// changedDecls are the top-level objects of a harness source whose
// declaration the change edited or added since base (changedDeclKeys).
// Every declaration counts when base is empty, the file is new in base's
// terms, or the file has not changed since base (a what-if query for a
// whole file).
func (ht *harnessTypes) changedDecls(repo, base, file string, syntax *ast.File) []types.Object {
	info := ht.harness.Info
	var all []types.Object
	for _, decl := range syntax.Decls {
		all = append(all, declaredObjects(info, decl)...)
	}
	if base == "" {
		return all
	}
	before, err := output(repo, "git", "show", base+":"+filepath.ToSlash(file))
	if err != nil {
		return all
	}
	after, err := os.ReadFile(filepath.Join(repo, filepath.FromSlash(file)))
	if err != nil {
		return all
	}
	keys, ok := changedDeclKeys([]byte(before), after)
	if !ok {
		return all
	}
	var out []types.Object
	for _, decl := range syntax.Decls {
		for _, key := range declKeys(decl) {
			if keys[key.name] {
				out = append(out, declaredObjects(info, decl)...)
				break
			}
		}
	}
	return out
}

// changedDeclKeys names the declarations (declKeys) of after whose token
// stream, comments dropped, differs from the same-named declaration of
// before, or that before lacks. It reports false when either version does
// not parse or the two are identical, so the caller falls back to every
// declaration.
func changedDeclKeys(before, after []byte) (map[string]bool, bool) {
	if string(before) == string(after) {
		return nil, false
	}
	was, ok := declTokens(before)
	if !ok {
		return nil, false
	}
	fset := token.NewFileSet()
	now, err := parser.ParseFile(fset, "", after, 0)
	if err != nil {
		return nil, false
	}
	keys := map[string]bool{}
	for _, decl := range now.Decls {
		for _, key := range declKeys(decl) {
			tokens, ok := significantTokens(after[fset.Position(key.node.Pos()).Offset:fset.Position(key.node.End()).Offset])
			if !ok {
				return nil, false
			}
			if had, ok := was[key.name]; !ok || !sameTokens(tokens, had) {
				keys[key.name] = true
			}
		}
	}
	return keys, true
}

// declKey names one declaration of a file for comparison across versions:
// "Type.Method" or "Func" for a function, the name for a type, the names
// for a constant or variable spec.
type declKey struct {
	name string
	node ast.Node
}

// declKeys keys a top-level declaration: a function as itself, a group by
// each of its specs.
func declKeys(decl ast.Decl) []declKey {
	switch decl := decl.(type) {
	case *ast.FuncDecl:
		name := decl.Name.Name
		if decl.Recv != nil && len(decl.Recv.List) == 1 {
			recv := decl.Recv.List[0].Type
			if star, ok := recv.(*ast.StarExpr); ok {
				recv = star.X
			}
			if index, ok := recv.(*ast.IndexExpr); ok {
				recv = index.X
			}
			if ident, ok := recv.(*ast.Ident); ok {
				name = ident.Name + "." + name
			}
		}
		return []declKey{{name, decl}}
	case *ast.GenDecl:
		var keys []declKey
		for _, spec := range decl.Specs {
			switch spec := spec.(type) {
			case *ast.TypeSpec:
				keys = append(keys, declKey{spec.Name.Name, spec})
			case *ast.ValueSpec:
				var names []string
				for _, name := range spec.Names {
					names = append(names, name.Name)
				}
				keys = append(keys, declKey{strings.Join(names, ","), spec})
			}
		}
		return keys
	}
	return nil
}

// declTokens scans a file version into the significant tokens of each
// declaration by key.
func declTokens(src []byte) (map[string][]lexeme, bool) {
	fset := token.NewFileSet()
	file, err := parser.ParseFile(fset, "", src, 0)
	if err != nil {
		return nil, false
	}
	out := map[string][]lexeme{}
	for _, decl := range file.Decls {
		for _, key := range declKeys(decl) {
			tokens, ok := significantTokens(src[fset.Position(key.node.Pos()).Offset:fset.Position(key.node.End()).Offset])
			if !ok {
				return nil, false
			}
			out[key.name] = tokens
		}
	}
	return out, true
}

// declaredObjects are the top-level objects a declaration introduces: the
// function or method, or each type, constant and variable of a group.
func declaredObjects(info *types.Info, decl ast.Decl) []types.Object {
	var out []types.Object
	switch decl := decl.(type) {
	case *ast.FuncDecl:
		if obj := info.Defs[decl.Name]; obj != nil {
			out = append(out, obj)
		}
	case *ast.GenDecl:
		for _, spec := range decl.Specs {
			switch spec := spec.(type) {
			case *ast.TypeSpec:
				if obj := info.Defs[spec.Name]; obj != nil {
					out = append(out, obj)
				}
			case *ast.ValueSpec:
				for _, name := range spec.Names {
					if obj := info.Defs[name]; obj != nil {
						out = append(out, obj)
					}
				}
			}
		}
	}
	return out
}

// topLevel maps a used object of the harness package to the top-level
// object that declares it: itself for a package-level function, method,
// type, constant or variable; the named type for a struct field; nil for
// a local or an object of another package.
func (ht *harnessTypes) topLevel(obj types.Object) types.Object {
	if obj == nil || obj.Pkg() != ht.harness.Types {
		return nil
	}
	switch o := obj.(type) {
	case *types.Var:
		if o.IsField() {
			return ht.fieldOwner(o)
		}
	case *types.Func:
		return o
	}
	if obj.Parent() == ht.harness.Types.Scope() {
		return obj
	}
	return nil
}

// fieldOwner is the package-level named type declaring a struct field,
// found by the field's position inside the type's declaration; nil for a
// field of an anonymous struct outside any type declaration.
func (ht *harnessTypes) fieldOwner(field *types.Var) types.Object {
	ht.spansOnce.Do(func() {
		for _, file := range ht.harness.Syntax {
			for _, decl := range file.Decls {
				gen, ok := decl.(*ast.GenDecl)
				if !ok {
					continue
				}
				for _, spec := range gen.Specs {
					if spec, ok := spec.(*ast.TypeSpec); ok {
						if obj := ht.harness.Info.Defs[spec.Name]; obj != nil {
							ht.spans = append(ht.spans, typeSpan{spec.Pos(), spec.End(), obj})
						}
					}
				}
			}
		}
	})
	for _, span := range ht.spans {
		if span.start <= field.Pos() && field.Pos() < span.end {
			return span.obj
		}
	}
	return nil
}

// typeSpan is the source extent of a package-level type declaration.
type typeSpan struct {
	start, end token.Pos
	obj        types.Object
}

// uses describes the tainted harness objects a package references, as
// one line: "na.Name, na.Type.Method and 3 more of <changed file>", the
// changed files joined by "; ". Empty when the change is invisible to the
// package, or the package is not a direct importer of the harness.
func (ht *harnessTypes) uses(pkgPath string, tainted map[types.Object]string) string {
	pkg, ok := ht.users[pkgPath]
	if !ok {
		return ""
	}
	byFile := map[string]map[string]bool{} // changed file -> names used
	for _, obj := range pkg.Info.Uses {
		obj = ht.topLevel(obj)
		changed, ok := tainted[obj]
		if !ok {
			continue
		}
		name := obj.Name()
		if fn, ok := obj.(*types.Func); ok && fn.Signature().Recv() != nil {
			recv := fn.Signature().Recv().Type()
			if ptr, ok := recv.(*types.Pointer); ok {
				recv = ptr.Elem()
			}
			if named, ok := recv.(*types.Named); ok {
				name = named.Obj().Name() + "." + name
			}
		}
		if byFile[changed] == nil {
			byFile[changed] = map[string]bool{}
		}
		byFile[changed]["na."+name] = true
	}
	files := make([]string, 0, len(byFile))
	for file := range byFile {
		files = append(files, file)
	}
	sort.Strings(files)
	const shown = 3
	var parts []string
	for _, file := range files {
		names := make([]string, 0, len(byFile[file]))
		for name := range byFile[file] {
			names = append(names, name)
		}
		sort.Strings(names)
		list := strings.Join(names, ", ")
		if len(names) > shown {
			list = strings.Join(names[:shown], ", ") + fmt.Sprintf(" and %d more", len(names)-shown)
		}
		parts = append(parts, list+" of "+file)
	}
	return strings.Join(parts, "; ")
}
