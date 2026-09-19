// Package affected maps the files a change touched to the Go packages and
// native acceptance harnesses worth running for it, so "rerun only affected
// checks" is a command rather than a judgment call.
package affected

import (
	"bytes"
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
	// changed Go files (or testdata) and every in-module package importing
	// them. Empty with AllGo set means ./... .
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
	var sel Selection
	goDir := filepath.Join(repo, "go")
	changedPkgs := map[string]bool{}
	dirs := map[string]bool{}
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
			}
		}
		for _, root := range probeInputs {
			if strings.HasPrefix(file, root+"/") {
				sel.Probes = true
			}
		}
		if dir, ok := goPackageDir(repo, file); ok {
			dirs[dir] = true
		}
	}
	if sel.AllHarnesses {
		var err error
		if sel.Cases, err = caseAreas(goDir); err != nil {
			return sel, err
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
	for dir := range dirs {
		if pkg, ok := graph.byDir[dir]; ok {
			changedPkgs[pkg] = true
		}
	}
	// A package is affected when it changed or imports (directly, or
	// through tests) a changed package.
	for pkg, deps := range graph.deps {
		if changedPkgs[pkg] {
			sel.Packages = append(sel.Packages, pkg)
			continue
		}
		for _, dep := range deps {
			if changedPkgs[dep] {
				sel.Packages = append(sel.Packages, pkg)
				break
			}
		}
	}
	sort.Strings(sel.Packages)
	if sel.AllHarnesses {
		return sel, nil
	}
	// A case area is affected when it, the runner or the rimgovernor binary
	// the cases drive imports a changed package. The runner imports every
	// area to register it, so the areas themselves do not count as its
	// inputs here.
	binaryAffected := false
	for _, dep := range graph.deps[graph.module+"/cmd/rimgovernor"] {
		if changedPkgs[dep] {
			binaryAffected = true
			break
		}
	}
	prefix := graph.module + "/internal/nativeaccept/cases/"
	runner := graph.module + "/internal/nativeaccept/cmd/acceptance"
	runnerAffected := binaryAffected || changedPkgs[runner]
	for _, dep := range graph.deps[runner] {
		if runnerAffected {
			break
		}
		runnerAffected = changedPkgs[dep] && !strings.HasPrefix(dep, prefix)
	}
	for pkg, deps := range graph.deps {
		name := strings.TrimPrefix(pkg, prefix)
		if !strings.HasPrefix(pkg, prefix) || strings.Contains(name, "/") {
			continue
		}
		affected := runnerAffected || changedPkgs[pkg] || fixtureAreas[name]
		for _, dep := range deps {
			if affected {
				break
			}
			affected = changedPkgs[dep]
		}
		if affected {
			sel.Cases = append(sel.Cases, name)
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
	module string
	byDir  map[string]string   // absolute directory -> import path
	deps   map[string][]string // import path -> in-module deps (transitive) plus direct test imports

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
	g := &graph{module: strings.TrimSpace(module), byDir: map[string]string{}, deps: map[string][]string{}}
	out, err := goOutput(goDir, "list", "-f", `{{.ImportPath}}|{{.Dir}}|{{join .Deps " "}} {{join .TestImports " "}} {{join .XTestImports " "}}`, "./...")
	if err != nil {
		return nil, err
	}
	for _, line := range strings.Split(out, "\n") {
		fields := strings.SplitN(strings.TrimSpace(line), "|", 3)
		if len(fields) != 3 {
			continue
		}
		pkg := fields[0]
		g.byDir[filepath.Clean(fields[1])] = pkg
		var deps []string
		for _, dep := range strings.Fields(fields[2]) {
			if dep == g.module || strings.HasPrefix(dep, g.module+"/") {
				deps = append(deps, dep)
			}
		}
		g.deps[pkg] = deps
	}
	// .Deps is transitive but the test imports are direct: close over them.
	for pkg, deps := range g.deps {
		seen := map[string]bool{}
		var closed []string
		for _, dep := range deps {
			for _, d := range append([]string{dep}, g.deps[dep]...) {
				if !seen[d] {
					seen[d] = true
					closed = append(closed, d)
				}
			}
		}
		g.deps[pkg] = closed
	}
	return g, nil
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
// areas first so the caller knows which acceptance runs the change may
// still owe; the hint passes -fresh, since a landing pass never resumes
// from a checkpoint (#249).
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
	for _, area := range sel.Cases {
		fmt.Printf("case: go run ./internal/nativeaccept/cmd/acceptance run %s/... -root <abs root> -output <fresh dir> -fresh\n", area)
	}
	switch {
	case sel.AllGo:
		fmt.Println("tests: go.mod/go.sum changed, testing ./...")
		return goRun(goDir, "test", "./...")
	case len(sel.Packages) == 0:
		fmt.Println("tests: no Go files changed, nothing to test")
		return nil
	}
	fmt.Println("tests:", len(sel.Packages), "affected package(s)")
	return goRun(goDir, append([]string{"test"}, sel.Packages...)...)
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
