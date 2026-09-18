package inputs

import (
	"bytes"
	"fmt"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
)

// harnessSharedInputs are the checked-in inputs every acceptance run
// depends on besides its Go packages and the native mod build
// (nativeSourceInputs, the same list RequireCurrentPackage compares). The
// test fixtures (FixtureRoot) are not shared: FixtureInputs scopes them to
// the cases that call their ops or load their saves (#170), and the
// contract fixtures (contracts/fixtures) feed unit tests only.
var harnessSharedInputs = []string{
	"go/go.mod",
	"go/go.sum",
}

// HarnessInputs lists, repo-relative with forward slashes, the files an
// acceptance case run depends on: every non-test file of each in-module
// Go package the case's area, the shared acceptance runner and the
// rimgovernor binary it drives import (the other areas the runner
// registers excluded), the native mod's build inputs, the fixture
// sources and saves those Go files name (FixtureInputs) and
// harnessSharedInputs. harness is a registered case "<area>/<case>"
// (#135), whose packages are the area's under
// go/internal/nativeaccept/cases and go/internal/nativeaccept/cmd/acceptance.
// It does not cover the game, GABS or the machine: those are environment,
// not inputs.
func HarnessInputs(repo, harness string) ([]string, error) {
	goDir := filepath.Join(repo, "go")
	area, patterns, err := casePackages(goDir, harness)
	if err != nil {
		return nil, err
	}
	dirs, err := goPackageDirs(goDir, append(patterns, "./cmd/rimgovernor")...)
	if err != nil {
		return nil, err
	}
	dirs = WithoutOtherAreas(goDir, area, dirs)
	seen := map[string]bool{}
	var files []string
	add := func(relative string) {
		if !seen[relative] {
			seen[relative] = true
			files = append(files, relative)
		}
	}
	for _, dir := range dirs {
		entries, err := os.ReadDir(dir)
		if err != nil {
			return nil, err
		}
		relDir, err := filepath.Rel(repo, dir)
		if err != nil {
			return nil, err
		}
		for _, entry := range entries {
			if entry.IsDir() || strings.HasSuffix(entry.Name(), "_test.go") {
				continue
			}
			add(filepath.ToSlash(filepath.Join(relDir, entry.Name())))
		}
	}
	for _, input := range nativeSourceInputs {
		if input.repo == FixtureRoot {
			continue
		}
		if err := walkInput(repo, input.repo, input.keep, add); err != nil {
			return nil, err
		}
	}
	for _, input := range harnessSharedInputs {
		if err := walkInput(repo, input, nil, add); err != nil {
			return nil, err
		}
	}
	refs, err := ScanFixtureRefs(dirs)
	if err != nil {
		return nil, err
	}
	fixtures, err := FixtureInputs(repo, refs)
	if err != nil {
		return nil, err
	}
	for _, file := range fixtures {
		add(file)
	}
	sort.Strings(files)
	return files, nil
}

// casePackages resolves a registered case's "<area>/<case>" to its area
// and the go list patterns of its own packages.
func casePackages(goDir, harness string) (string, []string, error) {
	area, name, isCase := strings.Cut(harness, "/")
	if !isCase || area == "" || name == "" || strings.ContainsAny(area+name, `/\.`) {
		return "", nil, fmt.Errorf("case %q is not <area>/<case>", harness)
	}
	if _, err := os.Stat(filepath.Join(goDir, "internal", "nativeaccept", "cases", area)); err != nil {
		return "", nil, fmt.Errorf("case %s: %w", harness, err)
	}
	return area, []string{"./internal/nativeaccept/cases/" + area, "./internal/nativeaccept/cmd/acceptance"}, nil
}

// WithoutOtherAreas drops from dirs (absolute package directories) the
// case area packages under go/internal/nativeaccept/cases other than
// area's: the runner imports every area to register it, but a case only
// runs its own.
func WithoutOtherAreas(goDir, area string, dirs []string) []string {
	casesDir := filepath.Join(goDir, "internal", "nativeaccept", "cases")
	var kept []string
	for _, dir := range dirs {
		rel, err := filepath.Rel(casesDir, dir)
		if err == nil && rel != "." && !strings.HasPrefix(rel, "..") && strings.Split(filepath.ToSlash(rel), "/")[0] != area {
			continue
		}
		kept = append(kept, dir)
	}
	return kept
}

// walkInput calls add for each file under the repo-relative input (or the
// input itself when it is a file) that keep accepts; a missing input is an
// error since every listed input is checked in.
func walkInput(repo, input string, keep func(string) bool, add func(string)) error {
	root := filepath.Join(repo, filepath.FromSlash(input))
	info, err := os.Stat(root)
	if err != nil {
		return fmt.Errorf("harness input %s: %w", input, err)
	}
	if !info.IsDir() {
		add(input)
		return nil
	}
	return filepath.WalkDir(root, func(path string, entry fs.DirEntry, err error) error {
		if err != nil || entry.IsDir() {
			return err
		}
		relative, err := filepath.Rel(root, path)
		if err != nil {
			return err
		}
		relative = filepath.ToSlash(relative)
		if keep != nil && !keep(relative) {
			return nil
		}
		add(input + "/" + relative)
		return nil
	})
}

// goPackageDirs returns the directories of the in-module packages the
// given patterns (relative to goDir) transitively import.
func goPackageDirs(goDir string, patterns ...string) ([]string, error) {
	module, err := goList(goDir, "-m", "-f", "{{.Path}}")
	if err != nil {
		return nil, err
	}
	modulePath := strings.TrimSpace(module)
	out, err := goList(goDir, append([]string{"-deps", "-f", "{{.ImportPath}}\t{{.Dir}}"}, patterns...)...)
	if err != nil {
		return nil, err
	}
	var dirs []string
	for _, line := range strings.Split(out, "\n") {
		importPath, dir, ok := strings.Cut(strings.TrimSpace(line), "\t")
		if !ok || importPath != modulePath && !strings.HasPrefix(importPath, modulePath+"/") {
			continue
		}
		dirs = append(dirs, dir)
	}
	sort.Strings(dirs)
	return dirs, nil
}

func goList(goDir string, args ...string) (string, error) {
	cmd := exec.Command("go", append([]string{"list"}, args...)...)
	cmd.Dir = goDir
	var stderr bytes.Buffer
	cmd.Stderr = &stderr
	out, err := cmd.Output()
	if err != nil {
		return "", fmt.Errorf("go list %s: %w: %s", strings.Join(args, " "), err, strings.TrimSpace(stderr.String()))
	}
	return string(out), nil
}

// HarnessInputRoots lists, repo-relative with forward slashes, the files
// and directories outside a case's Go packages that every acceptance run
// depends on: the native mod's build inputs (less the test fixtures,
// which FixtureInputs scopes per case) and harnessSharedInputs. A change
// under any of them affects every case.
func HarnessInputRoots() []string {
	roots := make([]string, 0, len(nativeSourceInputs)+len(harnessSharedInputs))
	for _, input := range nativeSourceInputs {
		if input.repo == FixtureRoot {
			continue
		}
		roots = append(roots, input.repo)
	}
	roots = append(roots, harnessSharedInputs...)
	sort.Strings(roots)
	return roots
}
