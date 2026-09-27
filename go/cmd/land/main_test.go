package main

import (
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// A throwaway repository: main checked out at root, a task branch in a
// linked worktree, git identity set so commits work on a bare machine.
func newRepo(t *testing.T) (root, branchWT string) {
	t.Helper()
	root = filepath.Join(t.TempDir(), "repo")
	mustGit(t, "", "init", "-q", "-b", "main", root)
	mustGit(t, root, "config", "user.email", "t@example.com")
	mustGit(t, root, "config", "user.name", "t")
	write(t, filepath.Join(root, "a.txt"), "a\n")
	mustGit(t, root, "add", ".")
	mustGit(t, root, "commit", "-qm", "init")
	branchWT = filepath.Join(filepath.Dir(root), "wt")
	mustGit(t, root, "worktree", "add", "-q", "-b", "task", branchWT, "main")
	return root, branchWT
}

func mustGit(t *testing.T, dir string, args ...string) string {
	t.Helper()
	cmd := exec.Command("git", args...)
	cmd.Dir = dir
	out, err := cmd.CombinedOutput()
	if err != nil {
		t.Fatalf("git %s: %v\n%s", strings.Join(args, " "), err, out)
	}
	return strings.TrimSpace(string(out))
}

func write(t *testing.T, path, content string) {
	t.Helper()
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}
}

func TestLandSquashesOntoMainAndKeepsCoAuthors(t *testing.T) {
	root, wt := newRepo(t)
	write(t, filepath.Join(wt, "b.txt"), "b\n")
	mustGit(t, wt, "add", ".")
	mustGit(t, wt, "commit", "-qm", "feat: add b\n\nCo-Authored-By: A <a@x>")
	write(t, filepath.Join(wt, "b.txt"), "bb\n")
	mustGit(t, wt, "commit", "-qam", "fix: b again\n\nCo-Authored-By: A <a@x>\nCo-Authored-By: B <b@x>")
	// main moves underneath: an unrelated file.
	write(t, filepath.Join(root, "c.txt"), "c\n")
	mustGit(t, root, "add", ".")
	mustGit(t, root, "commit", "-qm", "peer: add c")

	t.Chdir(wt)
	if err := run("", "", "", time.Second, false, acceptanceGate{}, nil); err != nil {
		t.Fatal(err)
	}
	if got := mustGit(t, root, "log", "--format=%s", "main"); got != "fix: b again\npeer: add c\ninit" {
		t.Errorf("main subjects:\n%s", got)
	}
	body := mustGit(t, root, "log", "-1", "--format=%B", "main")
	for _, want := range []string{
		"Squashed from 2 commits on task:",
		"- feat: add b",
		"Co-Authored-By: A <a@x>",
		"Co-Authored-By: B <b@x>",
	} {
		if !strings.Contains(body, want) {
			t.Errorf("message lacks %q:\n%s", want, body)
		}
	}
	if parents := mustGit(t, root, "log", "-1", "--format=%P", "main"); strings.Contains(parents, " ") {
		t.Errorf("landed commit is a merge: parents %s", parents)
	}
	if data, _ := os.ReadFile(filepath.Join(root, "b.txt")); strings.TrimSpace(string(data)) != "bb" {
		t.Errorf("main b.txt = %q", data)
	}
	if got := mustGit(t, wt, "rev-parse", "HEAD"); got != mustGit(t, root, "rev-parse", "main") {
		t.Errorf("branch was not reset to main")
	}
	if _, err := os.Stat(filepath.Join(root, ".git", lockName)); !os.IsNotExist(err) {
		t.Errorf("lock left behind: %v", err)
	}
}

// A wip commit under a merge renamed to the milestone subject never titles
// the squash (28f2a7184 landed as "wip").
func TestLandNeverTitlesASquashWip(t *testing.T) {
	root, wt := newRepo(t)
	write(t, filepath.Join(wt, "b.txt"), "b\n")
	mustGit(t, wt, "add", ".")
	mustGit(t, wt, "commit", "-qm", "Add b (#1)")
	write(t, filepath.Join(root, "c.txt"), "c\n")
	mustGit(t, root, "add", ".")
	mustGit(t, root, "commit", "-qm", "peer: add c")
	write(t, filepath.Join(wt, "b.txt"), "bb\n")
	mustGit(t, wt, "commit", "-qam", "wip")
	mustGit(t, wt, "merge", "-q", "--no-edit", "main")
	mustGit(t, wt, "commit", "--amend", "-qm", "Milestone b (#1)")

	t.Chdir(wt)
	if err := run("", "", "", time.Second, false, acceptanceGate{}, nil, false); err != nil {
		t.Fatal(err)
	}
	body := mustGit(t, root, "log", "-1", "--format=%B", "main")
	if !strings.HasPrefix(body, "Milestone b (#1)\n") || strings.Contains(body, "wip") {
		t.Errorf("squash message:\n%s", body)
	}
}

func TestUntitled(t *testing.T) {
	for subject, want := range map[string]bool{
		"wip": true, "WIP: stash": true, "wip.": true, "checkpoint": true, "fixup! Add b": true,
		"Merge branch 'main' into task": true, "": true,
		"Add b (#1)": false, "Wipe stale saves": false, "Merge policy for zones": false,
	} {
		if got := untitled(subject); got != want {
			t.Errorf("untitled(%q) = %v, want %v", subject, got, want)
		}
	}
}

func TestLandRefusesDirtyMainAndConflicts(t *testing.T) {
	root, wt := newRepo(t)
	write(t, filepath.Join(wt, "a.txt"), "branch\n")
	mustGit(t, wt, "commit", "-qam", "branch edit")
	t.Chdir(wt)

	write(t, filepath.Join(root, "a.txt"), "dirty\n")
	if err := run("", "", "", time.Second, false, acceptanceGate{}, nil); err == nil || !strings.Contains(err.Error(), "main checkout") {
		t.Errorf("dirty main: got %v", err)
	}
	mustGit(t, root, "checkout", "--", "a.txt")

	write(t, filepath.Join(root, "a.txt"), "main\n")
	mustGit(t, root, "commit", "-qam", "main edit")
	err := run("", "", "", time.Second, false, acceptanceGate{}, nil)
	if err == nil || !strings.Contains(err.Error(), "resolve the conflict") {
		t.Errorf("conflict: got %v", err)
	}
	if got := mustGit(t, wt, "status", "--porcelain"); got != "" {
		t.Errorf("branch worktree left mid-merge:\n%s", got)
	}
	if got := mustGit(t, root, "log", "--format=%s", "main"); got != "main edit\ninit" {
		t.Errorf("main changed:\n%s", got)
	}
}

func TestLandWaitsForLock(t *testing.T) {
	root, wt := newRepo(t)
	write(t, filepath.Join(wt, "b.txt"), "b\n")
	mustGit(t, wt, "add", ".")
	mustGit(t, wt, "commit", "-qm", "b")
	write(t, filepath.Join(root, ".git", lockName), "pid=0 branch=other\n")
	t.Chdir(wt)
	err := run("", "", "", 0, false, acceptanceGate{}, nil)
	if err == nil || !strings.Contains(err.Error(), "landing lock") {
		t.Errorf("held lock: got %v", err)
	}
}

func TestCloseIssueReadsTheBranchName(t *testing.T) {
	for branch, want := range map[string]string{
		"claude/github-issue-128-dbc221": "128",
		"issue-7-corridor":               "7",
		"claude/rimgovernor-issue-39-x":  "39",
		"claude/agents-duplicate-tests":  "",
		"feature/issue-abc":              "",
	} {
		got := ""
		if m := issueInBranch.FindStringSubmatch(branch); m != nil {
			got = m[1]
		}
		if got != want {
			t.Errorf("%s: issue %q, want %q", branch, got, want)
		}
	}
	if closeIssue(0, true) != nil {
		t.Error("-no-close should disable closing")
	}
}

func TestLandRefusesABranchThatRevertsMainWork(t *testing.T) {
	root, wt := newRepo(t)
	write(t, filepath.Join(wt, "b.txt"), "b\n")
	mustGit(t, wt, "add", ".")
	mustGit(t, wt, "commit", "-qm", "feat: b")
	// main lands a change to a.txt and a new file after the branch forked.
	write(t, filepath.Join(root, "a.txt"), "landed\n")
	write(t, filepath.Join(root, "new.txt"), "new\n")
	mustGit(t, root, "add", ".")
	mustGit(t, root, "commit", "-qm", "peer: landed work")
	// The branch merges main, then a stale-tree commit puts both back.
	mustGit(t, wt, "merge", "-q", "--no-edit", "main")
	write(t, filepath.Join(wt, "a.txt"), "a\n")
	mustGit(t, wt, "rm", "-q", "new.txt")
	write(t, filepath.Join(wt, "b.txt"), "bb\n")
	mustGit(t, wt, "add", ".")
	mustGit(t, wt, "commit", "-qm", "feat: b from a stale tree")
	before := mustGit(t, root, "rev-parse", "HEAD")

	t.Chdir(wt)
	err := run("", "", "", time.Second, false, acceptanceGate{}, nil)
	if err == nil || !strings.Contains(err.Error(), "a.txt") || !strings.Contains(err.Error(), "new.txt") {
		t.Fatalf("err = %v, want a refusal naming a.txt and new.txt", err)
	}
	if after := mustGit(t, root, "rev-parse", "HEAD"); after != before {
		t.Fatalf("main moved to %s", after)
	}
}
