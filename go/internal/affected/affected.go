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
	// Harnesses are the go/internal/nativeaccept/cmd directories whose
	// inputs (na.HarnessInputs) include a changed file.
	Harnesses []string
	// AllHarnesses means a shared harness input changed (native mod
	// sources, fixtures, go.mod): every harness is affected.
	AllHarnesses bool
}

// ChangedFiles lists the repo-relative files the working tree changed
// since it diverged from base (the merge base, so what base gained
// meanwhile does not count), including uncommitted and untracked ones.
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
		if file != "" && !seen[file] {
			seen[file] = true
			files = append(files, file)
		}
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
	for _, file := range changed {
		file = filepath.ToSlash(file)
		if file == "go/go.mod" || file == "go/go.sum" {
			sel.AllGo = true
		}
		for _, root := range na.HarnessInputRoots() {
			if file == root || strings.HasPrefix(file, root+"/") {
				sel.AllHarnesses = true
			}
		}
		if dir, ok := goPackageDir(repo, file); ok {
			dirs[dir] = true
		}
	}
	if sel.AllHarnesses {
		harnesses, err := harnessNames(goDir)
		if err != nil {
			return sel, err
		}
		sel.Harnesses = harnesses
	}
	if sel.AllGo {
		return sel, nil
	}
	if len(dirs) == 0 && !sel.AllHarnesses {
		return sel, nil
	}
	graph, err := dependencyGraph(goDir)
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
	// A harness is affected when it or the rimgovernor binary it drives
	// imports a changed package.
	binaryDeps := map[string]bool{}
	for _, dep := range graph.deps[graph.module+"/cmd/rimgovernor"] {
		binaryDeps[dep] = true
	}
	binaryAffected := false
	for dep := range binaryDeps {
		if changedPkgs[dep] {
			binaryAffected = true
			break
		}
	}
	prefix := graph.module + "/internal/nativeaccept/cmd/"
	for pkg, deps := range graph.deps {
		name := strings.TrimPrefix(pkg, prefix)
		if !strings.HasPrefix(pkg, prefix) || !isHarness(name) {
			continue
		}
		affected := binaryAffected || changedPkgs[pkg]
		for _, dep := range deps {
			if affected {
				break
			}
			affected = changedPkgs[dep]
		}
		if affected {
			sel.Harnesses = append(sel.Harnesses, name)
		}
	}
	sort.Strings(sel.Harnesses)
	return sel, nil
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
}

// dependencyGraph reads the module's packages once.
func dependencyGraph(goDir string) (*graph, error) {
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

// isHarness tells an acceptance harness directory under
// go/internal/nativeaccept/cmd from the tools beside it (gamesstop,
// variantsavegen, verified): harnesses end in "accept".
func isHarness(name string) bool {
	return strings.HasSuffix(name, "accept") && !strings.Contains(name, "/")
}

func harnessNames(goDir string) ([]string, error) {
	entries, err := os.ReadDir(filepath.Join(goDir, "internal", "nativeaccept", "cmd"))
	if err != nil {
		return nil, err
	}
	var names []string
	for _, entry := range entries {
		if entry.IsDir() && isHarness(entry.Name()) {
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
