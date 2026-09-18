// Command land is the serial landing lane: it takes a task branch from its
// worktree onto local main as one squash commit, so agents call it once
// instead of polling main, rebasing and retesting against every peer.
//
//	go run ./cmd/land [flags] [<branch>]
//
// run from anywhere inside the branch's worktree (the branch defaults to
// the checked-out one). In order it:
//
//  1. takes the repository-wide landing lock (one landing at a time);
//  2. merges main into the branch's worktree, which must be clean; a
//     conflict aborts the merge and leaves the resolution to the caller;
//  3. runs go test for the packages the branch changed since main and
//     the in-module packages that import them (all packages when go.mod
//     or go.sum changed); nothing outside go/ is tested here;
//  4. reports each recorded Verified: trailer as ok or stale for the
//     merged tree (informational; acceptance stays out of the lane);
//  5. squash-merges the branch into the main checkout, which must be clean,
//     with a message built from the branch's commits (-m or -F overrides
//     the subject and body) carrying the branch's Verified: and
//     Co-Authored-By trailers;
//  6. resets the branch to the new main when its tree is identical, so
//     the next task starts from main rather than re-landing the same diff.
package main

import (
	"bytes"
	"errors"
	"flag"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const lockName = "rimgovernor-land.lock"

func main() {
	message := flag.String("m", "", "squash commit subject and body (the trailers are appended)")
	messageFile := flag.String("F", "", "file holding the squash commit subject and body")
	lockTimeout := flag.Duration("lock-timeout", 15*time.Minute, "how long to wait for another landing to finish")
	skipTests := flag.Bool("skip-tests", false, "land without running the affected Go tests")
	flag.Parse()
	if flag.NArg() > 1 {
		fmt.Fprintln(os.Stderr, "usage: land [-m msg | -F file] [-lock-timeout d] [-skip-tests] [<branch>]")
		os.Exit(2)
	}
	if err := run(flag.Arg(0), *message, *messageFile, *lockTimeout, *skipTests); err != nil {
		fmt.Fprintln(os.Stderr, "land:", err)
		os.Exit(1)
	}
}

func run(branch, message, messageFile string, lockTimeout time.Duration, skipTests bool) error {
	cwd, err := os.Getwd()
	if err != nil {
		return err
	}
	worktree, ok := na.FindRepo(cwd)
	if !ok {
		return fmt.Errorf("not inside a git checkout: %s", cwd)
	}
	current, err := git(worktree, "rev-parse", "--abbrev-ref", "HEAD")
	if err != nil {
		return err
	}
	if branch == "" {
		branch = current
	}
	if branch != current {
		return fmt.Errorf("this worktree has %s checked out, not %s; run land from the branch's worktree", current, branch)
	}
	if branch == "main" || branch == "HEAD" {
		return fmt.Errorf("%s is not a task branch", branch)
	}
	mainCheckout, err := branchWorktree(worktree, "main")
	if err != nil {
		return err
	}
	if mainCheckout == "" {
		return errors.New("main is not checked out in any worktree; land needs the main checkout")
	}
	if messageFile != "" {
		data, err := os.ReadFile(messageFile)
		if err != nil {
			return err
		}
		message = string(data)
	}

	unlock, err := lock(worktree, branch, lockTimeout)
	if err != nil {
		return err
	}
	defer unlock()

	if err := requireClean(worktree, "branch worktree"); err != nil {
		return err
	}
	if err := requireClean(mainCheckout, "main checkout"); err != nil {
		return err
	}
	if _, err := git(worktree, "merge", "--no-edit", "main"); err != nil {
		_, _ = git(worktree, "merge", "--abort")
		return fmt.Errorf("merging main into %s: %w\nresolve the conflict on the branch (git merge main), commit, and run land again", branch, err)
	}
	if _, err := git(worktree, "diff", "--quiet", "main"); err == nil {
		fmt.Printf("%s has nothing to land: its tree matches main\n", branch)
		return nil
	}
	changed, err := gitLines(worktree, "diff", "--name-only", "main", "HEAD")
	if err != nil {
		return err
	}
	if skipTests {
		fmt.Println("tests: skipped (-skip-tests)")
	} else if err := testAffected(worktree, changed); err != nil {
		return err
	}
	reportVerified(worktree)

	head, err := git(worktree, "rev-parse", "HEAD")
	if err != nil {
		return err
	}
	body, err := squashMessage(worktree, branch, message)
	if err != nil {
		return err
	}
	if _, err := git(mainCheckout, "merge", "--squash", head); err != nil {
		_, _ = git(mainCheckout, "reset", "--hard", "HEAD")
		return fmt.Errorf("squash-merging %s into main: %w", branch, err)
	}
	messagePath := filepath.Join(os.TempDir(), fmt.Sprintf("land-%d.txt", os.Getpid()))
	if err := os.WriteFile(messagePath, []byte(body), 0o644); err != nil {
		return err
	}
	defer os.Remove(messagePath)
	if _, err := git(mainCheckout, "commit", "-F", messagePath); err != nil {
		_, _ = git(mainCheckout, "reset", "--hard", "HEAD")
		return fmt.Errorf("committing on main: %w", err)
	}
	landed, err := git(mainCheckout, "rev-parse", "--short", "HEAD")
	if err != nil {
		return err
	}
	fmt.Printf("landed %s on main as %s\n", branch, landed)
	if _, err := git(worktree, "diff", "--quiet", "main"); err == nil {
		if _, err := git(worktree, "reset", "--hard", "main"); err != nil {
			return err
		}
		fmt.Printf("%s reset to main (%s); start the next task from here\n", branch, landed)
	} else {
		fmt.Printf("%s left as is: its tree differs from the landed main\n", branch)
	}
	return nil
}

// branchWorktree returns the worktree path that has branch checked out, or
// "" when none does.
func branchWorktree(repo, branch string) (string, error) {
	out, err := git(repo, "worktree", "list", "--porcelain")
	if err != nil {
		return "", err
	}
	path := ""
	for _, line := range strings.Split(out, "\n") {
		switch {
		case strings.HasPrefix(line, "worktree "):
			path = strings.TrimPrefix(line, "worktree ")
		case line == "branch refs/heads/"+branch:
			return filepath.Clean(path), nil
		}
	}
	return "", nil
}

func requireClean(dir, what string) error {
	out, err := git(dir, "status", "--porcelain", "--untracked-files=no")
	if err != nil {
		return err
	}
	if out != "" {
		return fmt.Errorf("%s %s has uncommitted changes:\n%s", what, dir, out)
	}
	return nil
}

// lock takes the repository-wide landing lock under the shared git
// directory, waiting for a holder to finish up to timeout.
func lock(repo, branch string, timeout time.Duration) (func(), error) {
	common, err := git(repo, "rev-parse", "--path-format=absolute", "--git-common-dir")
	if err != nil {
		return nil, err
	}
	path := filepath.Join(common, lockName)
	deadline := time.Now().Add(timeout)
	for {
		f, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o644)
		if err == nil {
			fmt.Fprintf(f, "pid=%d branch=%s since=%s\n", os.Getpid(), branch, time.Now().Format(time.RFC3339))
			f.Close()
			return func() { os.Remove(path) }, nil
		}
		if !errors.Is(err, os.ErrExist) {
			return nil, err
		}
		holder, _ := os.ReadFile(path)
		if time.Now().After(deadline) {
			return nil, fmt.Errorf("landing lock %s held for over %s by %s; remove it only if that process is gone", path, timeout, strings.TrimSpace(string(holder)))
		}
		fmt.Printf("waiting for landing lock held by %s\n", strings.TrimSpace(string(holder)))
		time.Sleep(5 * time.Second)
	}
}

// testAffected runs go test for the packages the changed files belong to
// and every in-module package importing them.
func testAffected(worktree string, changed []string) error {
	goDir := filepath.Join(worktree, "go")
	all := false
	dirs := map[string]bool{}
	for _, file := range changed {
		if !strings.HasPrefix(file, "go/") {
			continue
		}
		switch {
		case file == "go/go.mod" || file == "go/go.sum":
			all = true
		case strings.HasSuffix(file, ".go"):
			dir := filepath.Join(worktree, filepath.FromSlash(file))
			dir = filepath.Dir(dir)
			if info, err := os.Stat(dir); err == nil && info.IsDir() {
				dirs[dir] = true
			}
		}
	}
	if all {
		fmt.Println("tests: go.mod/go.sum changed, testing ./...")
		return goRun(goDir, "test", "./...")
	}
	if len(dirs) == 0 {
		fmt.Println("tests: no Go files changed, nothing to test")
		return nil
	}
	changedPkgs := map[string]bool{}
	for dir := range dirs {
		rel, err := filepath.Rel(goDir, dir)
		if err != nil {
			return err
		}
		out, err := goOutput(goDir, "list", "-f", "{{.ImportPath}}", "./"+filepath.ToSlash(rel))
		if err != nil {
			return err
		}
		if pkg := strings.TrimSpace(out); pkg != "" {
			changedPkgs[pkg] = true
		}
	}
	out, err := goOutput(goDir, "list", "-f", `{{.ImportPath}} {{join .Deps " "}} {{join .TestImports " "}} {{join .XTestImports " "}}`, "./...")
	if err != nil {
		return err
	}
	affected := map[string]bool{}
	for _, line := range strings.Split(out, "\n") {
		fields := strings.Fields(line)
		if len(fields) == 0 {
			continue
		}
		if changedPkgs[fields[0]] {
			affected[fields[0]] = true
			continue
		}
		for _, dep := range fields[1:] {
			if changedPkgs[dep] {
				affected[fields[0]] = true
				break
			}
		}
	}
	pkgs := make([]string, 0, len(affected))
	for pkg := range affected {
		pkgs = append(pkgs, pkg)
	}
	sort.Strings(pkgs)
	fmt.Printf("tests: %d changed package(s), %d affected\n", len(changedPkgs), len(pkgs))
	return goRun(goDir, append([]string{"test"}, pkgs...)...)
}

// reportVerified prints the branch's Verified trailers against the merged tree.
func reportVerified(worktree string) {
	recorded, err := na.RecordedVerifiedTrailers(worktree, "main..HEAD")
	if err != nil {
		fmt.Println("verified:", err)
		return
	}
	if len(recorded) == 0 {
		fmt.Println("verified: no Verified: trailers on the branch")
		return
	}
	harnesses := make([]string, 0, len(recorded))
	for harness := range recorded {
		harnesses = append(harnesses, harness)
	}
	sort.Strings(harnesses)
	for _, harness := range harnesses {
		current, err := na.HarnessInputHash(worktree, harness)
		switch {
		case err != nil:
			fmt.Printf("verified: %s: %v\n", harness, err)
		case current == recorded[harness].Inputs:
			fmt.Printf("verified: %s ok\n", harness)
		default:
			fmt.Printf("verified: %s STALE (recorded %s, merged tree %s); rerun it or land knowingly\n", harness, recorded[harness].Inputs, current)
		}
	}
}

// squashMessage builds the squash commit message: the given message, or
// the branch's single non-merge commit message, or the newest subject with
// the others listed; then the branch's Verified and Co-Authored-By trailers.
func squashMessage(worktree, branch, message string) (string, error) {
	messages, err := gitOutput(worktree, "log", "--no-merges", "--format=%B%x1e", "main..HEAD")
	if err != nil {
		return "", err
	}
	var commits []string
	for _, m := range strings.Split(messages, string(rune(0x1e))) {
		if m = strings.TrimSpace(m); m != "" {
			commits = append(commits, m)
		}
	}
	trailers := map[string]bool{}
	var trailerLines []string
	addTrailer := func(line string) {
		if !trailers[line] {
			trailers[line] = true
			trailerLines = append(trailerLines, line)
		}
	}
	strip := func(m string) string {
		var kept []string
		for _, line := range strings.Split(m, "\n") {
			t := strings.TrimSpace(line)
			if strings.HasPrefix(t, na.VerifiedTrailerKey+":") || strings.HasPrefix(t, "Co-Authored-By:") {
				addTrailer(t)
				continue
			}
			kept = append(kept, line)
		}
		return strings.TrimSpace(strings.Join(kept, "\n"))
	}
	stripped := make([]string, len(commits))
	for i, m := range commits {
		stripped[i] = strip(m)
	}
	body := strings.TrimSpace(message)
	if body != "" {
		body = strip(body)
	} else {
		switch len(stripped) {
		case 0:
			return "", fmt.Errorf("no commits on %s since main; give -m", branch)
		case 1:
			body = stripped[0]
		default:
			var lines []string
			lines = append(lines, firstLine(stripped[0]), "", fmt.Sprintf("Squashed from %d commits on %s:", len(stripped), branch))
			for _, m := range stripped {
				lines = append(lines, "- "+firstLine(m))
			}
			body = strings.Join(lines, "\n")
		}
	}
	// Keep only the newest Verified trailer per harness.
	seenHarness := map[string]bool{}
	var final []string
	for _, line := range trailerLines {
		if t := na.ParseVerifiedTrailers(line); len(t) == 1 {
			if seenHarness[t[0].Harness] {
				continue
			}
			seenHarness[t[0].Harness] = true
		}
		final = append(final, line)
	}
	if len(final) == 0 {
		return body + "\n", nil
	}
	return body + "\n\n" + strings.Join(final, "\n") + "\n", nil
}

func firstLine(s string) string {
	line, _, _ := strings.Cut(s, "\n")
	return line
}

func git(dir string, args ...string) (string, error) {
	out, err := gitOutput(dir, args...)
	return strings.TrimSpace(out), err
}

func gitLines(dir string, args ...string) ([]string, error) {
	out, err := git(dir, args...)
	if err != nil || out == "" {
		return nil, err
	}
	return strings.Split(out, "\n"), nil
}

func gitOutput(dir string, args ...string) (string, error) {
	return output(dir, "git", args...)
}

func goOutput(dir string, args ...string) (string, error) {
	return output(dir, "go", args...)
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

// goRun streams a go command's output so test failures are visible.
func goRun(dir string, args ...string) error {
	cmd := exec.Command("go", args...)
	cmd.Dir = dir
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("go %s: %w", strings.Join(args, " "), err)
	}
	return nil
}
