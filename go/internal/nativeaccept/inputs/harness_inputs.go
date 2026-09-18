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
// depends on besides its Go packages: the native mod build
// (nativeSourceInputs, the same list RequireCurrentPackage compares) and
// the fixture saves and contract fixtures a case loads.
var harnessSharedInputs = []string{
	"go/go.mod",
	"go/go.sum",
	"scripts/fixtures",
	"contracts/fixtures",
}

// HarnessInputs lists, repo-relative with forward slashes, the files an
// acceptance case run depends on: every non-test file of each in-module
// Go package the case's area, the shared acceptance runner and the
// rimgovernor binary it drives import, the native mod's build inputs and
// harnessSharedInputs. harness is a registered case "<area>/<case>"
// (#135), whose packages are the area's under
// go/internal/nativeaccept/cases and go/internal/nativeaccept/cmd/acceptance.
// It does not cover the game, GABS or the machine: those are environment,
// not inputs.
func HarnessInputs(repo, harness string) ([]string, error) {
	goDir := filepath.Join(repo, "go")
	patterns, err := casePackages(goDir, harness)
	if err != nil {
		return nil, err
	}
	dirs, err := goPackageDirs(goDir, append(patterns, "./cmd/rimgovernor")...)
	if err != nil {
		return nil, err
	}
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
		if err := walkInput(repo, input.repo, input.keep, add); err != nil {
			return nil, err
		}
	}
	for _, input := range harnessSharedInputs {
		if err := walkInput(repo, input, nil, add); err != nil {
			return nil, err
		}
	}
	sort.Strings(files)
	return files, nil
}

// casePackages resolves a registered case's "<area>/<case>" to the go
// list patterns of its own packages.
func casePackages(goDir, harness string) ([]string, error) {
	area, name, isCase := strings.Cut(harness, "/")
	if !isCase || area == "" || name == "" || strings.ContainsAny(area+name, `/\.`) {
		return nil, fmt.Errorf("case %q is not <area>/<case>", harness)
	}
	if _, err := os.Stat(filepath.Join(goDir, "internal", "nativeaccept", "cases", area)); err != nil {
		return nil, fmt.Errorf("case %s: %w", harness, err)
	}
	return []string{"./internal/nativeaccept/cases/" + area, "./internal/nativeaccept/cmd/acceptance"}, nil
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
// depends on: the native mod's build inputs and harnessSharedInputs. A
// change under any of them affects every case.
func HarnessInputRoots() []string {
	roots := make([]string, 0, len(nativeSourceInputs)+len(harnessSharedInputs))
	for _, input := range nativeSourceInputs {
		roots = append(roots, input.repo)
	}
	roots = append(roots, harnessSharedInputs...)
	sort.Strings(roots)
	return roots
}
