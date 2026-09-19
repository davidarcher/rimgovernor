// Package affected maps the files a change touched to the Go packages and
// native acceptance harnesses worth running for it, so "rerun only affected
// checks" is a command rather than a judgment call.
package affected

import (
	"bytes"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path"
	"path/filepath"
	"sort"
	"strings"
	"sync"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/inputs"
)

// Selection is what a change affects.
type Selection struct {
	// AllGo means go.mod or go.sum changed: every package is affected.
	AllGo bool
	// Packages are the import paths to go test: the packages holding the
	// changed Go files (or testdata). Production edits also select every
	// in-module importer; test-only edits stay local. Empty with AllGo set
	// means ./... .
	Packages []string
	// Cases are the go/internal/nativeaccept/cases areas (#135) whose
	// inputs (na.HarnessInputs) include a changed file: every case the
	// area registers is affected (acceptance run <area>/...).
	Cases []string
	// AllHarnesses means a shared acceptance input changed (native mod
	// sources, go.mod): every case is affected. A test fixture change is
	// not shared: it affects the areas whose Go sources name one of the
	// fixture's ops or its save (na.FixtureInputs, #170).
	AllHarnesses bool
	// Probes means the native contract probes build (task probes:build,
	// contracts/tests/NativeContractProbes.csproj) is affected: it compiles
	// production sources under integrations/rimgovernor-native/src against
	// the hand-written stubs in contracts/tests, so a native source, a probe
	// or a generated protocol class change can break it while the mod and
	// the Go suite stay green (#123).
	Probes bool
	// Why explains each selected area: one line per rule that selected it,
	// naming the changed file (#361).
	Why map[string][]string
	// Shared names the changed shared acceptance inputs behind AllHarnesses.
	Shared []string
}

// probeInputs are the roots whose files the native contract probes build
// compiles or links.
var probeInputs = []string{"integrations/rimgovernor-native/src", "contracts/tests", "contracts/generated/protobuf/csharp"}

// ChangedFiles lists the repo-relative files the working tree changed
// since it diverged from base (the merge base, so what base gained
// meanwhile does not count), including uncommitted and untracked ones. A
// Go file whose edit is comment-only (commentOnly) is left out: it changes
// no behaviour, so it should not name the packages importing it or the
// harnesses they drive.
func ChangedFiles(repo, base string) ([]string, error) {
	mergeBase, err := gitLines(repo, "merge-base", base, "HEAD")
	if err != nil {
		return nil, err
	}
	committed, err := gitLines(repo, "diff", "--name-only", mergeBase[0])
	if err != nil {
		return nil, err
	}
	untracked, err := gitLines(repo, "ls-files", "--others", "--exclude-standard")
	if err != nil {
		return nil, err
	}
	seen := map[string]bool{}
	var files []string
	for _, file := range append(committed, untracked...) {
		if file == "" || seen[file] {
			continue
		}
		seen[file] = true
		if commentOnly(repo, mergeBase[0], file) {
			continue
		}
		files = append(files, file)
	}
	sort.Strings(files)
	return files, nil
}

// Select computes what the changed repo-relative files affect.
func Select(repo string, changed []string) (Selection, error) {
	sel := Selection{Why: map[string][]string{}}
	goDir := filepath.Join(repo, "go")
	dirs := map[string][]string{} // absolute package dir -> changed files in it
	changedFixtures := map[string]bool{}
	for _, file := range changed {
		file = filepath.ToSlash(file)
		if file == "go/go.mod" || file == "go/go.sum" {
			sel.AllGo = true
		}
		if na.FixtureFile(file) {
			changedFixtures[file] = true
		}
		for _, root := range na.HarnessInputRoots() {
			if file == root || strings.HasPrefix(file, root+"/") {
				sel.AllHarnesses = true
				sel.Shared = append(sel.Shared, file)
			}
		}
		for _, root := range probeInputs {
			if strings.HasPrefix(file, root+"/") {
				sel.Probes = true
			}
		}
		if dir, ok := goPackageDir(repo, file); ok {
			dirs[dir] = append(dirs[dir], file)
		}
	}
	if sel.AllHarnesses {
		var err error
		if sel.Cases, err = caseAreas(goDir); err != nil {
			return sel, err
		}
		for _, area := range sel.Cases {
			sel.Why[area] = []string{"shared acceptance input changed: " + strings.Join(sel.Shared, ", ")}
		}
	}
	if sel.AllGo {
		return sel, nil
	}
	if len(dirs) == 0 && len(changedFixtures) == 0 && !sel.AllHarnesses {
		return sel, nil
	}
	graph, err := dependencyGraph(goDir)
	if err != nil {
		return sel, err
	}
	fixtureAreas, err := areasUsingFixtures(repo, graph, changedFixtures)
	if err != nil {
		return sel, err
	}
	// changedPkgs holds every changed package with its files, for go test;
	// sources holds only the files that build into a binary (no _test.go,
	// no testdata), since only those reach a case through the binary or
	// the runner; a source scoped to routine families (routineFamilyScope)
	// is kept out of sources and recorded under families instead.
	changedPkgs := map[string][]string{}
	productionPkgs := map[string]bool{}
	sources := map[string][]string{}
	families := map[string][]string{} // family -> files scoped to it
	for dir, files := range dirs {
		pkg, ok := graph.byDir[dir]
		if !ok {
			continue
		}
		changedPkgs[pkg] = append(changedPkgs[pkg], files...)
		for _, file := range files {
			if strings.HasSuffix(file, "_test.go") || !strings.HasSuffix(file, ".go") {
				continue
			}
			productionPkgs[pkg] = true
			scope, err := routineFamilyScope(goDir, file)
			if err != nil {
				return sel, err
			}
			if scope.All {
				sources[pkg] = append(sources[pkg], file)
				continue
			}
			for _, family := range scope.Families {
				families[family] = append(families[family], file)
			}
		}
	}
	// A package is affected when it changed or imports (directly, or
	// through its own tests) changed production code. Another package's
	// tests and testdata are not compiled into its importers.
	for pkg, deps := range graph.testDeps {
		if _, ok := changedPkgs[pkg]; ok {
			sel.Packages = append(sel.Packages, pkg)
			continue
		}
		for _, dep := range deps {
			if productionPkgs[dep] {
				sel.Packages = append(sel.Packages, pkg)
				break
			}
		}
	}
	sort.Strings(sel.Packages)
	if sel.AllHarnesses {
		return sel, nil
	}
	// A case area is affected when it or the runner imports a changed
	// source, or, for an area hosting `rimgovernor serve`, when the binary
	// does; a bridge-only area never runs the binary (#361). The runner
	// imports every area to register it, so the areas, and what only they
	// import, do not count as its inputs here; nor does this package, which
	// the runner imports to compose the land tier (#273): a change here
	// re-selects checks, it changes no case's run. A source scoped to routine
	// families reaches the serve-hosting areas whose cases compose one of
	// them, or compose every family.
	selector := graph.module + "/internal/affected"
	binary := graph.module + "/cmd/rimgovernor"
	var binaryWhy, runnerWhy []string
	for _, dep := range graph.deps[binary] {
		if files, ok := sources[dep]; ok {
			binaryWhy = append(binaryWhy, "the rimgovernor binary imports "+dep+" ("+strings.Join(files, ", ")+")")
		}
	}
	prefix := graph.module + "/internal/nativeaccept/cases/"
	runner := graph.module + "/internal/nativeaccept/cmd/acceptance"
	if files, ok := sources[runner]; ok {
		runnerWhy = append(runnerWhy, "the runner changed ("+strings.Join(files, ", ")+")")
	}
	for _, dep := range graph.closure(runner, func(dep string) bool { return strings.HasPrefix(dep, prefix) || dep == selector }) {
		if files, ok := sources[dep]; ok {
			runnerWhy = append(runnerWhy, "the runner imports "+dep+" ("+strings.Join(files, ", ")+")")
		}
	}
	familyNames := make([]string, 0, len(families))
	for family := range families {
		familyNames = append(familyNames, family)
	}
	sort.Strings(familyNames)
	for pkg, deps := range graph.deps {
		name := strings.TrimPrefix(pkg, prefix)
		if !strings.HasPrefix(pkg, prefix) || strings.Contains(name, "/") {
			continue
		}
		profile, err := readAreaProfile(filepath.Join(goDir, "internal", "nativeaccept", "cases", name))
		if err != nil {
			return sel, err
		}
		why := append([]string{}, runnerWhy...)
		if profile.Binary {
			why = append(why, binaryWhy...)
		}
		if files, ok := sources[pkg]; ok {
			why = append(why, "the area changed ("+strings.Join(files, ", ")+")")
		}
		if fixtureAreas[name] {
			why = append(why, "the area uses a changed fixture")
		}
		for _, dep := range deps {
			if files, ok := sources[dep]; ok && !strings.HasPrefix(dep, prefix) {
				why = append(why, "the area imports "+dep+" ("+strings.Join(files, ", ")+")")
			}
		}
		for _, family := range familyNames {
			if !profile.composes(family) {
				continue
			}
			how := "composes it"
			if profile.AllFamilies {
				how = "composes every family"
			}
			why = append(why, "routine family "+family+" changed ("+strings.Join(families[family], ", ")+") and the area "+how)
		}
		if len(why) > 0 {
			sel.Cases = append(sel.Cases, name)
			sel.Why[name] = why
		}
	}
	sort.Strings(sel.Cases)
	return sel, nil
}

// areasUsingFixtures names the case areas whose Go sources (the area, the
// runner and what they import, other areas excluded) depend on one of the
// changed fixture files: a fixture source whose op they call, a save they
// load, or a build file every build reads.
func areasUsingFixtures(repo string, g *graph, changedFixtures map[string]bool) (map[string]bool, error) {
	areas := map[string]bool{}
	if len(changedFixtures) == 0 {
		return areas, nil
	}
	inputs, err := g.areaFixtureInputs(repo)
	if err != nil {
		return nil, err
	}
	for name, files := range inputs {
		for _, input := range files {
			if changedFixtures[input] {
				areas[name] = true
				break
			}
		}
	}
	return areas, nil
}

// areaFixtureInputs maps each case area to the fixture files its sources
// depend on, scanned once per graph: the scan walks every area's import
// closure and is the expensive half of a fixture-scoped Select.
func (g *graph) areaFixtureInputs(repo string) (map[string][]string, error) {
	g.fixturesOnce.Do(func() {
		g.fixtures, g.fixturesErr = g.scanAreaFixtureInputs(repo)
	})
	return g.fixtures, g.fixturesErr
}

func (g *graph) scanAreaFixtureInputs(repo string) (map[string][]string, error) {
	goDir := filepath.Join(repo, "go")
	dirByPkg := map[string]string{}
	for dir, pkg := range g.byDir {
		dirByPkg[pkg] = dir
	}
	prefix := g.module + "/internal/nativeaccept/cases/"
	runner := g.module + "/internal/nativeaccept/cmd/acceptance"
	inputsByArea := map[string][]string{}
	for pkg := range g.deps {
		name := strings.TrimPrefix(pkg, prefix)
		if !strings.HasPrefix(pkg, prefix) || strings.Contains(name, "/") {
			continue
		}
		seen := map[string]bool{}
		var dirs []string
		for _, dep := range append([]string{pkg, runner}, append(g.deps[pkg], g.deps[runner]...)...) {
			if dir, ok := dirByPkg[dep]; ok && !seen[dep] {
				seen[dep] = true
				dirs = append(dirs, dir)
			}
		}
		refs, err := na.ScanFixtureRefs(na.WithoutOtherAreas(goDir, name, dirs))
		if err != nil {
			return nil, err
		}
		inputs, err := na.FixtureInputs(repo, refs)
		if err != nil {
			return nil, err
		}
		inputsByArea[name] = inputs
	}
	return inputsByArea, nil
}

// goPackageDir returns the absolute package directory a changed file
// belongs to: its own directory for a .go file, the directory above
// testdata for a fixture, and only while that directory still exists.
func goPackageDir(repo, file string) (string, bool) {
	if !strings.HasPrefix(file, "go/") {
		return "", false
	}
	dir := path.Dir(file)
	if !strings.HasSuffix(file, ".go") {
		i := strings.Index(file, "/testdata/")
		if i < 0 {
			return "", false
		}
		dir = file[:i]
	}
	abs := filepath.Join(repo, filepath.FromSlash(dir))
	if info, err := os.Stat(abs); err != nil || !info.IsDir() {
		return "", false
	}
	return abs, true
}

type graph struct {
	module   string
	byDir    map[string]string   // absolute directory -> import path
	deps     map[string][]string // import path -> transitive in-module production deps
	testDeps map[string][]string // production deps plus dependencies of this package's tests
	direct   map[string][]string // import path -> in-module direct imports (no tests)

	fixturesOnce sync.Once
	fixtures     map[string][]string // case area -> fixture inputs its sources depend on
	fixturesErr  error
}

var (
	graphsMu sync.Mutex
	graphs   = map[string]*graph{} // module dir -> graph, read once per process
)

// dependencyGraph reads the module's packages once per process: Select is
// a one-shot query over a checkout, and go list over the module is the
// bulk of its cost.
func dependencyGraph(goDir string) (*graph, error) {
	graphsMu.Lock()
	defer graphsMu.Unlock()
	if g, ok := graphs[goDir]; ok {
		return g, nil
	}
	g, err := readDependencyGraph(goDir)
	if err != nil {
		return nil, err
	}
	graphs[goDir] = g
	return g, nil
}

func readDependencyGraph(goDir string) (*graph, error) {
	module, err := goOutput(goDir, "list", "-m", "-f", "{{.Path}}")
	if err != nil {
		return nil, err
	}
	g := &graph{module: strings.TrimSpace(module), byDir: map[string]string{}, deps: map[string][]string{}, direct: map[string][]string{}, testDeps: map[string][]string{}}
	out, err := goOutput(goDir, "list", "-f", `{{.ImportPath}}|{{.Dir}}|{{join .Imports " "}}|{{join .Deps " "}}|{{join .TestImports " "}} {{join .XTestImports " "}}`, "./...")
	if err != nil {
		return nil, err
	}
	tests := map[string][]string{}
	for _, line := range strings.Split(out, "\n") {
		fields := strings.SplitN(strings.TrimSpace(line), "|", 5)
		if len(fields) != 5 {
			continue
		}
		pkg := fields[0]
		g.byDir[filepath.Clean(fields[1])] = pkg
		g.direct[pkg] = g.inModule(strings.Fields(fields[2]))
		g.deps[pkg] = g.inModule(strings.Fields(fields[3]))
		tests[pkg] = g.inModule(strings.Fields(fields[4]))
	}
	g.addTestDependencies(tests)
	return g, nil
}

func (g *graph) addTestDependencies(tests map[string][]string) {
	// .Deps is transitive but the test imports are direct: close over them
	// with each import's .Deps, so a package's own test imports never
	// reach the packages importing it.
	for pkg, imports := range tests {
		seen := map[string]bool{}
		closed := append([]string{}, g.deps[pkg]...)
		for _, dep := range closed {
			seen[dep] = true
		}
		for _, dep := range imports {
			for _, d := range append([]string{dep}, g.deps[dep]...) {
				if !seen[d] {
					seen[d] = true
					closed = append(closed, d)
				}
			}
		}
		sort.Strings(closed)
		g.testDeps[pkg] = closed
	}
}

// inModule keeps the module's own import paths.
func (g *graph) inModule(paths []string) []string {
	var kept []string
	for _, path := range paths {
		if path == g.module || strings.HasPrefix(path, g.module+"/") {
			kept = append(kept, path)
		}
	}
	return kept
}

// closure is the in-module packages reachable from pkg through direct
// (non-test) imports, skipping the packages skip admits and what is
// reachable only through them.
func (g *graph) closure(pkg string, skip func(string) bool) []string {
	seen := map[string]bool{pkg: true}
	var out []string
	queue := []string{pkg}
	for len(queue) > 0 {
		next := queue[0]
		queue = queue[1:]
		for _, dep := range g.direct[next] {
			if seen[dep] || skip(dep) {
				continue
			}
			seen[dep] = true
			out = append(out, dep)
			queue = append(queue, dep)
		}
	}
	sort.Strings(out)
	return out
}

// caseAreas lists the case area packages under
// go/internal/nativeaccept/cases.
func caseAreas(goDir string) ([]string, error) {
	entries, err := os.ReadDir(filepath.Join(goDir, "internal", "nativeaccept", "cases"))
	if err != nil {
		return nil, err
	}
	var names []string
	for _, entry := range entries {
		if entry.IsDir() {
			names = append(names, entry.Name())
		}
	}
	return names, nil
}

func gitLines(repo string, args ...string) ([]string, error) {
	out, err := output(repo, "git", args...)
	if err != nil {
		return nil, err
	}
	out = strings.TrimSpace(out)
	if out == "" {
		return nil, nil
	}
	return strings.Split(out, "\n"), nil
}

func goOutput(dir string, args ...string) (string, error) { return output(dir, "go", args...) }

func output(dir, name string, args ...string) (string, error) {
	cmd := exec.Command(name, args...)
	cmd.Dir = dir
	var stderr bytes.Buffer
	cmd.Stderr = &stderr
	out, err := cmd.Output()
	if err != nil {
		return "", fmt.Errorf("%s %s: %w: %s", name, strings.Join(args, " "), err, strings.TrimSpace(stderr.String()))
	}
	return string(out), nil
}

// Test runs go test for what the changed files affect (Select) and the
// native contract probes build when the change touches its inputs,
// streaming the output to stdout/stderr, and names the affected case
// areas first so the caller knows what the change still owes. The one
// command it prints is the land tier, which already covers those areas
// plus the smoke set and runs fresh (#249, #273); running the areas on
// their own first and then the tier would run every case twice.
func Test(repo string, changed []string) error {
	goDir := filepath.Join(repo, "go")
	sel, err := Select(repo, changed)
	if err != nil {
		return err
	}
	if sel.Probes {
		fmt.Println("probes: native contract probes build affected, running task probes:build")
		if err := run(repo, "task", "probes:build"); err != nil {
			return err
		}
	}
	if sel.AllHarnesses {
		fmt.Println("cases affected: all (a shared acceptance input changed: native sources or go.mod)")
	}
	if len(sel.Cases) > 0 {
		fmt.Printf("cases affected: %s (cmd/affected -files says why)\n", strings.Join(sel.Cases, " "))
	}
	if len(sel.Cases) > 0 || sel.AllHarnesses {
		fmt.Println("acceptance: one run, the land tier (it covers the affected areas and the smoke set; do not run the areas separately first):")
		fmt.Println("  go run ./internal/nativeaccept/cmd/acceptance suite -tier land -root <abs root> -output <fresh dir>")
		fmt.Println("  go run ./cmd/land -results <that dir>")
	}
	switch {
	case sel.AllGo:
		if err := lint(goDir, changed, []string{"./..."}); err != nil {
			return err
		}
		fmt.Println("tests: go.mod/go.sum changed, testing ./...")
		return goRun(goDir, "test", "./...")
	case len(sel.Packages) == 0:
		fmt.Println("tests: no Go files changed, nothing to test")
		return nil
	}
	if err := lint(goDir, changed, sel.Packages); err != nil {
		return err
	}
	fmt.Println("tests:", len(sel.Packages), "affected package(s)")
	return goRun(goDir, append([]string{"test"}, sel.Packages...)...)
}

// lint runs the static gates task go:build applies to the whole module
// (gofmt, go vet, staticcheck) on the changed Go files and the affected
// packages, so a finding surfaces in the edit/test loop rather than at the
// next task build (#334).
func lint(goDir string, changed, packages []string) error {
	var files []string
	for _, file := range changed {
		if !strings.HasSuffix(file, ".go") || !strings.HasPrefix(file, "go/") {
			continue
		}
		rel := filepath.FromSlash(strings.TrimPrefix(file, "go/"))
		if _, err := os.Stat(filepath.Join(goDir, rel)); err == nil {
			files = append(files, rel)
		}
	}
	if len(files) > 0 {
		unformatted, err := output(goDir, "gofmt", append([]string{"-l"}, files...)...)
		if err != nil {
			return err
		}
		if unformatted = strings.TrimSpace(unformatted); unformatted != "" {
			return fmt.Errorf("gofmt -l: %s", strings.Join(strings.Fields(unformatted), " "))
		}
	}
	fmt.Println("lint: go vet and staticcheck on", len(packages), "package(s)")
	if err := goRun(goDir, append([]string{"vet"}, packages...)...); err != nil {
		return errors.New("go vet: findings above")
	}
	if err := goRun(goDir, append([]string{"tool", "staticcheck"}, packages...)...); err != nil {
		return errors.New("staticcheck: findings above")
	}
	return nil
}

// goRun streams a go command's output so test failures are visible.
func goRun(dir string, args ...string) error { return run(dir, "go", args...) }

// run streams a command's output so failures are visible.
func run(dir, name string, args ...string) error {
	cmd := exec.Command(name, args...)
	cmd.Dir = dir
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("%s %s: %w", name, strings.Join(args, " "), err)
	}
	return nil
}
