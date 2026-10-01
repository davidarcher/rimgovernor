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
//  2. fetches origin/main and fast-forwards local main to it, since the
//     maintainer and remote agents push origin/main directly (a local main
//     that has diverged is refused; an unreachable origin is reported and
//     the lane continues); then merges main into the branch's worktree, which must be clean; a
//     conflict aborts the merge and leaves the resolution to the caller,
//     and a merged tree that puts a path back to its content before one
//     of main's recent landings is refused, naming the paths and the
//     landings (#889, #946);
//  3. with -test, runs the Go tests the branch affects as cmd/test does
//     (off by default: the branch runs cmd/test before landing, and the
//     lane does not repeat it); with -results <dir>, reads the acceptance
//     suite report there (result.json from `acceptance suite`, the smoke
//     tier's output since #387; the land tier on demand) and refuses a
//     suite that did not pass; rows that
//     resumed from a checkpoint (`acceptance suite -resume`) land and are
//     named in the acceptance line, since their pass proves the fix past
//     the resume point only (#249, #308); results are optional;
//  4. commits the merged branch's tree onto main as one squash commit and
//     moves the main ref only if main has not moved since step 2, with a
//     message built from the branch's commits (-m or -F overrides the
//     subject and body) carrying the branch's Co-Authored-By trailers. No
//     checkout of main is needed; a worktree that has main checked out
//     must be clean (a refusal names each dirty path, its mtime, main's
//     last landing on it and the worktrees holding the same content, #965)
//     and is brought along after the move;
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
	"sort"
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
	flag.Parse()
	if flag.NArg() > 1 {
		fmt.Fprintln(os.Stderr, "usage: land [-m msg | -F file] [-lock-timeout d] [-test] [-results dir] [-issue N | -no-close] [<branch>]")
		os.Exit(2)
	}
	gate := acceptanceGate{Results: *results}
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
	trees, err := listWorktrees(worktree)
	if err != nil {
		return err
	}
	// land moves the main ref itself; a worktree that has main checked out
	// is optional and only brought along after the move.
	mainCheckout := ""
	for _, wt := range trees {
		if wt.Branch == "main" {
			mainCheckout = wt.Path
		}
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
	if mainCheckout != "" {
		if err := requireCleanMain(mainCheckout, trees); err != nil {
			return err
		}
	}
	if err := gate.prepare(worktree); err != nil {
		return err
	}
	if err := syncMain(worktree, mainCheckout); err != nil {
		return err
	}
	oldMain, err := git(worktree, "rev-parse", "main")
	if err != nil {
		return err
	}
	if _, err := git(worktree, "merge", "--no-edit", oldMain); err != nil {
		_, _ = git(worktree, "merge", "--abort")
		return fmt.Errorf("merging main into %s: %w\nresolve the conflict on the branch (git merge main), commit, and run land again", branch, err)
	}
	if err := refuseReverts(worktree); err != nil {
		return err
	}
	if _, err := git(worktree, "diff", "--quiet", "main"); err == nil {
		fmt.Printf("%s has nothing to land: its tree matches main\n", branch)
		return nil
	}
	changed, err := affected.ChangedFiles(worktree, "main")
	if err != nil {
		return err
	}
	if err := gate.check(); err != nil {
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
	// The merged branch's tree is the squash: commit it onto the main land
	// merged and move the ref only if main is still there, so no checkout
	// of main is needed and a concurrent move is refused, not overwritten.
	messagePath := filepath.Join(os.TempDir(), fmt.Sprintf("land-%d.txt", os.Getpid()))
	if err := os.WriteFile(messagePath, []byte(strings.TrimRight(body, "\n")+"\n"), 0o644); err != nil {
		return err
	}
	defer os.Remove(messagePath)
	squash, err := git(worktree, "commit-tree", head+"^{tree}", "-p", oldMain, "-F", messagePath)
	if err != nil {
		return fmt.Errorf("committing the squash of %s: %w", branch, err)
	}
	if _, err := git(worktree, "update-ref", "-m", "land: "+branch, "refs/heads/main", squash, oldMain); err != nil {
		return fmt.Errorf("main moved off %.9s while %s was landing; nothing landed, run land again: %w", oldMain, branch, err)
	}
	landed, err := git(worktree, "rev-parse", "--short", squash)
	if err != nil {
		return err
	}
	fmt.Printf("landed %s on main as %s\n", branch, landed)
	if mainCheckout != "" {
		if _, err := git(mainCheckout, "read-tree", "-m", "-u", oldMain, squash); err != nil {
			fmt.Printf("main checkout %s not updated to %s (%v); bring it along with git reset --keep main there\n", mainCheckout, landed, err)
		}
	}
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
// the branch's one titled commit message, or the newest title with the
// others listed; then the branch's Co-Authored-By trailers. A title is a
// commit subject, merges included (a merge renamed to the milestone
// subject titles the squash), that is neither git's default merge subject
// nor a placeholder such as "wip"; a branch with none needs -m.
func squashMessage(worktree, branch, message string) (string, error) {
	messages, err := gitOutput(worktree, "log", "--format=%B%x1e", "main..HEAD")
	if err != nil {
		return "", err
	}
	var commits []string
	for _, m := range strings.Split(messages, string(rune(0x1e))) {
		if m = strings.TrimSpace(m); m != "" && !untitled(firstLine(m)) {
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
			return "", fmt.Errorf("no titled commits on %s since main (only merges or placeholders like wip); give -m or reword the tip", branch)
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

// untitled is true for a subject that cannot title a squash: git's default
// merge subjects and checkpoint placeholders.
func untitled(subject string) bool {
	s := strings.ToLower(strings.TrimSpace(subject))
	for _, prefix := range []string{"merge branch ", "merge remote-tracking branch ", "merge commit ", "fixup!", "squash!", "amend!"} {
		if strings.HasPrefix(s, prefix) {
			return true
		}
	}
	words := strings.FieldsFunc(s, func(r rune) bool { return r == ' ' || r == ':' || r == '.' || r == '(' })
	if len(words) == 0 {
		return true
	}
	switch words[0] {
	case "wip", "tmp", "temp", "checkpoint", "fixup", "squash", "todo", "xxx":
		return true
	}
	return false
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

// revertWindow is how many main landings refuseReverts looks back through.
const revertWindow = 300

const nullBlob = "0000000000000000000000000000000000000000"

var hashToken = regexp.MustCompile(`\b[0-9a-f]{7,40}\b`)

// refuseReverts refuses a merged branch that puts a path back to the
// content it had before one of main's last revertWindow landings touched
// it: a stale tree committed over a newer main. The fork point alone
// cannot see this, since a stale tree re-parented onto main (git reset
// --soft main, then commit) forks at main itself; that is how 6cbe9d337
// undid 981ab0b9a (#946) after the fork-point check of #889 passed.
// Deleting a file counts only for files main added after the branch
// forked (the oldest main landing any branch commit has as a parent;
// later merges of main do not move it), so the branch's own deletions of
// old files land. A branch commit message naming the reverted commit's
// hash marks the revert as intended.
func refuseReverts(worktree string) error {
	changed, err := git(worktree, "diff", "--raw", "--no-abbrev", "--no-renames", "main", "HEAD")
	if err != nil || changed == "" {
		return err
	}
	branchBlob := map[string]string{}
	for _, line := range strings.Split(changed, "\n") {
		if _, blob, path, ok := rawEntry(line); ok {
			branchBlob[path] = blob
		}
	}
	history, err := git(worktree, "log", "--first-parent", "--diff-merges=first-parent", "-n", strconv.Itoa(revertWindow),
		"--raw", "--no-abbrev", "--no-renames", "--format=@%H %s", "main")
	if err != nil {
		return err
	}
	branch, err := git(worktree, "log", "--format=%P%x1f%B%x1e", "main..HEAD")
	if err != nil {
		return err
	}
	parents := map[string]bool{}
	var named []string
	for _, entry := range strings.Split(branch, "\x1e") {
		ps, body, _ := strings.Cut(entry, "\x1f")
		for _, p := range strings.Fields(ps) {
			parents[p] = true
		}
		named = append(named, hashToken.FindAllString(body, -1)...)
	}
	// sinceFork holds main's landings newer than the fork: every one in the
	// window down to the oldest that parents a branch commit.
	sinceFork := map[string]bool{}
	var landings []string
	for _, line := range strings.Split(history, "\n") {
		if header, ok := strings.CutPrefix(line, "@"); ok {
			c, _, _ := strings.Cut(header, " ")
			landings = append(landings, c)
		}
	}
	fork := len(landings)
	for i, c := range landings {
		if parents[c] {
			fork = i
		}
	}
	for _, c := range landings[:fork] {
		sinceFork[c] = true
	}
	intended := func(commit string) bool {
		for _, token := range named {
			if strings.HasPrefix(commit, token) {
				return true
			}
		}
		return false
	}

	reverted := map[string]string{}
	var commit, subject string
	for _, line := range strings.Split(history, "\n") {
		if header, ok := strings.CutPrefix(line, "@"); ok {
			commit, subject, _ = strings.Cut(header, " ")
			continue
		}
		before, _, path, ok := rawEntry(line)
		blob, onBranch := branchBlob[path]
		if !ok || !onBranch || reverted[path] != "" || blob != before {
			continue
		}
		if (blob == nullBlob && !sinceFork[commit]) || intended(commit) {
			continue
		}
		reverted[path] = fmt.Sprintf("%s (undoes %.9s %s)", path, commit, subject)
	}
	if len(reverted) == 0 {
		return nil
	}
	lines := make([]string, 0, len(reverted))
	for _, line := range reverted {
		lines = append(lines, line)
	}
	sort.Strings(lines)
	return fmt.Errorf("the merged branch puts back content main replaced:\n  %s\nrestore main's version (git checkout main -- <path>) and commit, or name the commit's hash in a commit message if the revert is intended; then run land again", strings.Join(lines, "\n  "))
}

// rawEntry parses one `--raw --no-abbrev` line into the blob before, the
// blob after and the path.
func rawEntry(line string) (before, after, path string, ok bool) {
	meta, path, found := strings.Cut(line, "\t")
	fields := strings.Fields(meta)
	if !found || !strings.HasPrefix(meta, ":") || len(fields) < 5 {
		return "", "", "", false
	}
	return fields[2], fields[3], path, true
}
