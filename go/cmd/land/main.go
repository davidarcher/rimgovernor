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
//  3. with -test, runs the Go tests the branch affects as cmd/test does
//     (off by default: the branch runs cmd/test before landing, and the
//     lane does not repeat it); with -results <dir>, reads the acceptance
//     suite report there (result.json from `acceptance suite`, the land
//     tier's output) and refuses a suite that did not pass; rows that
//     resumed from a checkpoint (`acceptance suite -resume`) land and are
//     named in the acceptance line, since their pass proves the fix past
//     the resume point only (#249, #308); without it, refuses a diff that
//     touches the native mod sources or go/internal/buildingruntime (#273: nothing cheaper than a
//     game run proves those) unless -unverified says the landing goes
//     without, to be named in the commit body;
//  4. squash-merges the branch into the main checkout, which must be clean,
//     with a message built from the branch's commits (-m or -F overrides
//     the subject and body) carrying the branch's Co-Authored-By
//     trailers;
//  5. resets the branch to the new main when its tree is identical, so
//     the next task starts from main rather than re-landing the same diff;
//  6. closes the GitHub issue the branch is for (the number in a branch
//     name like claude/github-issue-128-abc, or -issue N) with a comment
//     naming the landing commit; -no-close skips it, and a missing gh or
//     a failed close is reported, never fatal.
package main

import (
	"bytes"
	"errors"
	"flag"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/affected"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/inputs"
)

const lockName = "rimgovernor-land.lock"

func main() {
	message := flag.String("m", "", "squash commit subject and body (the trailers are appended)")
	messageFile := flag.String("F", "", "file holding the squash commit subject and body")
	lockTimeout := flag.Duration("lock-timeout", 15*time.Minute, "how long to wait for another landing to finish")
	runTests := flag.Bool("test", false, "also run the affected Go tests against the merged tree before landing")
	issue := flag.Int("issue", 0, "GitHub issue to close with the landing commit (default: the number in the branch name)")
	noClose := flag.Bool("no-close", false, "do not close a GitHub issue")
	results := flag.String("results", "", "acceptance suite output directory (its result.json) the landing presents as its pass")
	unverified := flag.Bool("unverified", false, "land a native or buildingruntime change without -results; name what is unverified in the commit body")
	flag.Parse()
	if flag.NArg() > 1 {
		fmt.Fprintln(os.Stderr, "usage: land [-m msg | -F file] [-lock-timeout d] [-test] [-results dir | -unverified] [-issue N | -no-close] [<branch>]")
		os.Exit(2)
	}
	gate := acceptanceGate{Results: *results, Unverified: *unverified}
	if err := run(flag.Arg(0), *message, *messageFile, *lockTimeout, *runTests, gate, closeIssue(*issue, *noClose)); err != nil {
		fmt.Fprintln(os.Stderr, "land:", err)
		os.Exit(1)
	}
}

func run(branch, message, messageFile string, lockTimeout time.Duration, runTests bool, gate acceptanceGate, close issueCloser) error {
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
	changed, err := affected.ChangedFiles(worktree, "main")
	if err != nil {
		return err
	}
	if err := gate.check(changed); err != nil {
		return err
	}
	if runTests {
		if err := affected.Test(worktree, changed, "main"); err != nil {
			return err
		}
	} else {
		fmt.Println("tests: not run by the lane (go run ./cmd/test before landing, or pass -test)")
	}

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
	// main may have moved again while the tests ran; catch the branch up
	// so the identical-tree check below sees only what this landing missed.
	if _, err := git(worktree, "merge", "--no-edit", "main"); err != nil {
		_, _ = git(worktree, "merge", "--abort")
	}
	if _, err := git(worktree, "diff", "--quiet", "main"); err == nil {
		if _, err := git(worktree, "reset", "--hard", "main"); err != nil {
			return err
		}
		fmt.Printf("%s reset to main (%s); start the next task from here\n", branch, landed)
	} else {
		fmt.Printf("%s left as is: its tree differs from the landed main\n", branch)
	}
	if close != nil {
		close(worktree, branch, landed)
	}
	return nil
}

// issueCloser closes the GitHub issue a landed branch was for, or does
// nothing; it never fails the landing.
type issueCloser func(worktree, branch, landed string)

var issueInBranch = regexp.MustCompile(`(?:^|[/-])issue-(\d+)(?:-|$)`)

// closeIssue returns the closer for the flags: none with -no-close, the
// given issue with -issue, otherwise the number in the branch name.
func closeIssue(issue int, noClose bool) issueCloser {
	if noClose {
		return nil
	}
	return func(worktree, branch, landed string) {
		number := issue
		if number == 0 {
			m := issueInBranch.FindStringSubmatch(branch)
			if m == nil {
				return
			}
			number, _ = strconv.Atoi(m[1])
		}
		comment := fmt.Sprintf("Landed on main as %s.", landed)
		if _, err := output(worktree, "gh", "issue", "close", strconv.Itoa(number), "--comment", comment); err != nil {
			fmt.Printf("issue #%d not closed (%v); close it by hand with the landing commit\n", number, err)
			return
		}
		fmt.Printf("closed issue #%d\n", number)
	}
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

// squashMessage builds the squash commit message: the given message, or
// the branch's single non-merge commit message, or the newest subject with
// the others listed; then the branch's Co-Authored-By trailers.
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
			if strings.HasPrefix(t, "Co-Authored-By:") {
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
	if len(trailerLines) == 0 {
		return body + "\n", nil
	}
	return body + "\n\n" + strings.Join(trailerLines, "\n") + "\n", nil
}

func firstLine(s string) string {
	line, _, _ := strings.Cut(s, "\n")
	return line
}

func git(dir string, args ...string) (string, error) {
	out, err := gitOutput(dir, args...)
	return strings.TrimSpace(out), err
}

func gitOutput(dir string, args ...string) (string, error) {
	return output(dir, "git", args...)
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
