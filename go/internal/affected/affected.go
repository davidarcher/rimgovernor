// Package affected finds the files a change touched and runs the checks
// worth running for them: lint and go test on the module when Go changed, the
// native contract probes build when its inputs did.
package affected

import (
	"bytes"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

// probeInputs are the roots whose files the native contract probes build
// compiles or links.
var probeInputs = []string{"integrations/rimgovernor-native/src", "contracts/tests", "contracts/generated/protobuf/csharp"}

// ChangedFiles lists the repo-relative files the working tree changed
// since it diverged from base (the merge base, so what base gained
// meanwhile does not count), including uncommitted and untracked ones.
// Discovery never excludes executable edits from fast checks.
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
		files = append(files, file)
	}
	sort.Strings(files)
	return files, nil
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

// Test runs the checks the changed files owe: the native contract probes
// build when a probe input changed, then lint and go test -short over the
// whole module when anything under go/ changed (go's own cache replays every
// package whose inputs are unchanged).
func Test(repo string, changed []string) error {
	start := time.Now()
	err := test(repo, changed)
	if err != nil {
		fmt.Printf("test: FAIL (%s)\n", time.Since(start).Round(time.Second))
		return err
	}
	fmt.Printf("test: PASS (%s)\n", time.Since(start).Round(time.Second))
	return nil
}

func test(repo string, changed []string) error {
	goDir := filepath.Join(repo, "go")
	if probesChanged(changed) {
		fmt.Println("probes: native contract probes build affected, running task probes:build")
		if err := run(repo, "task", "probes:build"); err != nil {
			return err
		}
	}
	if !Full && !GoChanged(changed) {
		fmt.Println("tests: no Go files changed, nothing to test")
		return nil
	}
	if err := lint(goDir, changed, []string{"./..."}); err != nil {
		return err
	}
	fmt.Println("tests: ./... (a package prints only when it finishes; silence is normal)")
	return timed("tests", func() error { return goTestShort(goDir, "./...") })
}

// probesChanged reports whether a changed file is one the native contract
// probes build compiles or links.
func probesChanged(changed []string) bool {
	for _, file := range changed {
		file = filepath.ToSlash(file)
		for _, root := range probeInputs {
			if strings.HasPrefix(file, root+"/") {
				return true
			}
		}
	}
	return false
}

// GoChanged reports whether a change touches anything under go/: Go
// sources, testdata, embedded inputs, go.mod.
func GoChanged(changed []string) bool {
	for _, file := range changed {
		if strings.HasPrefix(filepath.ToSlash(file), "go/") {
			return true
		}
	}
	return false
}

// Full checks the whole module even without changed Go files and runs the
// slow tests too (go test without -short). The default loop
// stays under about 30 s; the slow tests run in the nightly module run and
// at the end of an epic (cmd/test -full).
var Full bool

func testArgs(pkgs ...string) []string {
	args := []string{"test"}
	if !Full {
		args = append(args, "-short")
	}
	return append(args, pkgs...)
}

// timed runs a stage and prints how long it took, so a quiet stage still
// ends with a visible line.
func timed(stage string, fn func() error) error {
	start := time.Now()
	err := fn()
	status := "ok"
	if err != nil {
		status = "failed"
	}
	fmt.Printf("%s: %s (%s)\n", stage, status, time.Since(start).Round(time.Second))
	return err
}

// lint runs the static gates task go:build applies to the whole module
// (gofmt, go vet, staticcheck) on the changed Go files and the affected
// packages, so a finding surfaces in the edit/test loop rather than at the
// next task build.
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
	batches, err := fileBatches(files)
	if err != nil {
		return err
	}
	for _, batch := range batches {
		unformatted, err := output(goDir, "gofmt", append([]string{"-l"}, batch...)...)
		if err != nil {
			return err
		}
		if unformatted = strings.TrimSpace(unformatted); unformatted != "" {
			return fmt.Errorf("gofmt -l: %s", strings.Join(strings.Fields(unformatted), " "))
		}
	}
	fmt.Println("lint: go vet and staticcheck on", strings.Join(packages, " "), "(silent when clean)")
	return timed("lint", func() error {
		if err := goRun(goDir, append([]string{"vet"}, packages...)...); err != nil {
			return errors.New("go vet: findings above")
		}
		if err := goRun(goDir, append([]string{"tool", "staticcheck"}, packages...)...); err != nil {
			return errors.New("staticcheck: findings above")
		}
		return nil
	})
}

// goRun streams a go command's output so test failures are visible.
func goRun(dir string, args ...string) error { return run(dir, "go", args...) }

// heartbeatEvery is how often a running command reports that it is alive.
const heartbeatEvery = 30 * time.Second

// run streams a command's output so failures are visible, and prints a
// heartbeat while it is quiet so a slow command reads as running, not stuck.
func run(dir, name string, args ...string) error { return runOut(dir, os.Stdout, name, args...) }

// runOut is run with the command's stdout sent to out.
func runOut(dir string, out io.Writer, name string, args ...string) error {
	cmd := exec.Command(name, args...)
	cmd.Dir = dir
	cmd.Stdout = out
	cmd.Stderr = os.Stderr
	label := name
	if len(args) > 0 {
		label += " " + args[0]
		if args[0] == "tool" && len(args) > 1 {
			label += " " + args[1]
		}
	}
	start := time.Now()
	done := make(chan struct{})
	defer close(done)
	go func() {
		ticker := time.NewTicker(heartbeatEvery)
		defer ticker.Stop()
		for {
			select {
			case <-done:
				return
			case <-ticker.C:
				fmt.Fprintf(os.Stderr, "  still running: %s (%s)\n", label, time.Since(start).Round(time.Second))
			}
		}
	}()
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("%s %s: %w", name, strings.Join(args, " "), err)
	}
	return nil
}

// fileArgumentBudget leaves room for the executable, flags and terminating NUL
// below Windows' 32767 UTF-16-unit command line limit. Twice the UTF-8 byte
// length plus quotes and a separator bounds Windows quoting and UTF-16 encoding,
// including escaped backslashes and non-ASCII filenames.
const fileArgumentBudget = 16000

func fileBatches(files []string) ([][]string, error) {
	var batches [][]string
	start, size := 0, 0
	for i, file := range files {
		cost := 2*len(file) + 3
		if cost > fileArgumentBudget {
			return nil, fmt.Errorf("file argument exceeds command-line budget: %q", file)
		}
		if size+cost > fileArgumentBudget {
			batches = append(batches, files[start:i])
			start, size = i, 0
		}
		size += cost
	}
	if start < len(files) {
		batches = append(batches, files[start:])
	}
	return batches, nil
}
