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

func TestLandSquashesOntoMainAndCarriesTrailers(t *testing.T) {
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
	if err := run("", "", "", time.Second, false); err != nil {
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
	if strings.Contains(body, "inputs=0000000000000001") {
		t.Errorf("message keeps the superseded trailer:\n%s", body)
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

func TestLandRefusesDirtyMainAndConflicts(t *testing.T) {
	root, wt := newRepo(t)
	write(t, filepath.Join(wt, "a.txt"), "branch\n")
	mustGit(t, wt, "commit", "-qam", "branch edit")
	t.Chdir(wt)

	write(t, filepath.Join(root, "a.txt"), "dirty\n")
	if err := run("", "", "", time.Second, false); err == nil || !strings.Contains(err.Error(), "main checkout") {
		t.Errorf("dirty main: got %v", err)
	}
	mustGit(t, root, "checkout", "--", "a.txt")

	write(t, filepath.Join(root, "a.txt"), "main\n")
	mustGit(t, root, "commit", "-qam", "main edit")
	err := run("", "", "", time.Second, false)
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
	err := run("", "", "", 0, true)
	if err == nil || !strings.Contains(err.Error(), "landing lock") {
		t.Errorf("held lock: got %v", err)
	}
}

// Only the branch's own edits after the stamp make a trailer stale; what
// main moved under the harness's inputs is not a rerun trigger.
