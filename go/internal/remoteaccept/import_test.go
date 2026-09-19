package remoteaccept

import (
	"archive/zip"
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

type recordedAPI map[string][]byte

func (a recordedAPI) Get(endpoint string, w io.Writer) error {
	b, ok := a[endpoint]
	if !ok {
		return fmt.Errorf("unexpected API request: %s", endpoint)
	}
	_, err := w.Write(b)
	return err
}
func jsonBytes(t *testing.T, v any) []byte {
	t.Helper()
	b, err := json.Marshal(v)
	if err != nil {
		t.Fatal(err)
	}
	return b
}
func testGit(t *testing.T, repo string, args ...string) string {
	t.Helper()
	c := exec.Command("git", append([]string{"-c", "user.email=test@example.com", "-c", "user.name=Test"}, args...)...)
	c.Dir = repo
	b, err := c.CombinedOutput()
	if err != nil {
		t.Fatalf("git %v: %v: %s", args, err, b)
	}
	return strings.TrimSpace(string(b))
}
func sourceRepo(t *testing.T) (string, string, string) {
	t.Helper()
	repo := t.TempDir()
	testGit(t, repo, "init", "-q", "-b", "main")
	// Import the small, diverging histories in one process. Repeated add,
	// commit and checkout processes dominate fixture cost on Windows.
	// main stays at base until the merge test advances it to peer.
	c := exec.Command("git", "fast-import", "--quiet")
	c.Dir = repo
	c.Stdin = strings.NewReader(`commit refs/heads/main
mark :1
committer Test <test@example.com> 1700000000 +0000
data 4
base
M 100644 inline base.txt
data 4
base

commit refs/heads/task
mark :2
committer Test <test@example.com> 1700000001 +0000
data 4
task
from :1
M 100644 inline task.txt
data 4
task

commit refs/heads/peer
committer Test <test@example.com> 1700000002 +0000
data 4
peer
from :1
M 100644 inline peer.txt
data 4
peer

done
`)
	if b, err := c.CombinedOutput(); err != nil {
		t.Fatalf("git fast-import: %v: %s", err, b)
	}
	testGit(t, repo, "checkout", "-q", "task")
	commits := strings.Fields(testGit(t, repo, "rev-parse", "main", "task"))
	if len(commits) != 2 {
		t.Fatalf("expected base and task commits, got %v", commits)
	}
	return repo, commits[0], commits[1]
}
func zipTree(t *testing.T, root string) []byte {
	t.Helper()
	var b bytes.Buffer
	z := zip.NewWriter(&b)
	err := filepath.WalkDir(root, func(p string, d os.DirEntry, e error) error {
		if e != nil {
			return e
		}
		if d.IsDir() || d.Name() == ".gitattributes" {
			return nil
		}
		rel, e := filepath.Rel(root, p)
		if e != nil {
			return e
		}
		w, e := z.Create(filepath.ToSlash(rel))
		if e != nil {
			return e
		}
		data, e := os.ReadFile(p)
		if e != nil {
			return e
		}
		_, e = w.Write(data)
		return e
	})
	if err != nil {
		t.Fatal(err)
	}
	if err = z.Close(); err != nil {
		t.Fatal(err)
	}
	return b.Bytes()
}
func importFixture(t *testing.T) (recordedAPI, Provenance, string, *fixture) {
	t.Helper()
	repo, base, head := sourceRepo(t)
	f := fixtureRun(t)
	f.run.BaseCommit = base
	f.run.TestedCommit = head
	f.selection.Planner = head
	f.selection.Changed = []string{"task.txt"}
	for i := range f.attempts {
		for j := range f.attempts[i].Attempts {
			f.native(t, i, j, func(m map[string]json.RawMessage) { delete(m, "fixture_only") })
		}
	}
	e, err := f.evaluate(t)
	if err != nil {
		t.Fatal(err)
	}
	writeTest(t, f.root, "aggregate.json", e.Aggregate)
	p := Provenance{Trust: Trust{Repository: f.run.Repository, Workflow: ".github/workflows/acceptance.yml", WorkflowCommit: f.run.WorkflowCommit, BundleSHA256: f.run.Bundle.SHA256}, ArtifactID: 10, RunID: f.run.Trigger.RunID, Attempt: f.run.Trigger.Attempt}
	archive := zipTree(t, f.root)
	api := recordedAPI{}
	api["repos/"+p.Trust.Repository+"/actions/runs/1000/attempts/1"] = jsonBytes(t, map[string]any{"id": 1000, "run_attempt": 1, "head_sha": p.Trust.WorkflowCommit, "path": p.Trust.Workflow, "event": "workflow_dispatch", "status": "completed", "conclusion": "success", "repository": map[string]string{"full_name": p.Trust.Repository}, "head_repository": map[string]string{"full_name": p.Trust.Repository}})
	api["repos/"+p.Trust.Repository+"/actions/artifacts/10"] = jsonBytes(t, map[string]any{"id": 10, "expired": false, "digest": "sha256:" + hash(archive), "size_in_bytes": len(archive), "workflow_run": map[string]any{"id": 1000, "head_sha": p.Trust.WorkflowCommit}})
	api["repos/"+p.Trust.Repository+"/actions/artifacts/10/zip"] = archive
	return api, p, repo, f
}
func TestCompleteImportAndNormalMainMerge(t *testing.T) {
	api, p, repo, f := importFixture(t)
	out := filepath.Join(t.TempDir(), "import")
	if err := Download(api, p, out, repo); err != nil {
		t.Fatal(err)
	}
	root := filepath.Join(out, "evidence")
	if _, err := VerifyImported(root, repo, p.Trust, api); err != nil {
		t.Fatal(err)
	}
	// A peer moves main. Evidence is associated before the lane merges it.
	testGit(t, repo, "update-ref", "refs/heads/main", "peer")
	if err := VerifySource(repo, f.run); err != nil {
		t.Fatal(err)
	}
	testGit(t, repo, "merge", "--no-edit", "main")
	if _, err := VerifyImported(root, repo, p.Trust, api); err != nil {
		t.Fatalf("normal clean main merge invalidated evidence: %v", err)
	}
	if err := os.WriteFile(filepath.Join(repo, "task.txt"), []byte("uncovered"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := VerifySource(repo, f.run); err == nil {
		t.Fatal("dirty task accepted")
	}
	testGit(t, repo, "commit", "-qam", "uncovered change")
	if err := VerifySource(repo, f.run); err == nil {
		t.Fatal("wrong source accepted")
	}
}
func TestImportRejectsUntrustedAndTamperedEvidence(t *testing.T) {
	api, p, repo, _ := importFixture(t)
	for name, edit := range map[string]func(*Provenance){
		"repository":      func(p *Provenance) { p.Trust.Repository = "other/repo" },
		"workflow":        func(p *Provenance) { p.Trust.Workflow = ".github/workflows/other.yml" },
		"workflow commit": func(p *Provenance) { p.Trust.WorkflowCommit = strings.Repeat("1", 40) },
		"bundle":          func(p *Provenance) { p.Trust.BundleSHA256 = strings.Repeat("1", 64) },
		"run attempt":     func(p *Provenance) { p.Attempt = 2 },
	} {
		t.Run(name, func(t *testing.T) {
			bad := p
			edit(&bad)
			if err := Download(api, bad, filepath.Join(t.TempDir(), "import"), repo); err == nil {
				t.Fatal("untrusted import passed")
			}
		})
	}
	out := filepath.Join(t.TempDir(), "valid")
	if err := Download(api, p, out, repo); err != nil {
		t.Fatal(err)
	}
	root := filepath.Join(out, "evidence")
	var report Report
	readTest(t, root, "result.json", &report)
	report.Cases[0] = raw(`{"name":"light/dark","passed":true,"exit":0,"metrics":{"forged":1}}`)
	writeTest(t, root, "result.json", report)
	if _, err := VerifyImported(root, repo, p.Trust, api); err == nil {
		t.Fatal("tampered result passed")
	}
	if err := os.WriteFile(filepath.Join(out, "actions-artifact.zip"), []byte("corrupt"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := VerifyImported(root, repo, p.Trust, api); err == nil {
		t.Fatal("corrupt original archive passed")
	}
}
func TestArchiveRejectsEscapesLinksAndCollisions(t *testing.T) {
	for _, names := range [][]string{{"../escape.json"}, {"a.json", "A.json"}, {"game.dll"}, {"NUL.txt"}, {"a.json", "a.json"}} {
		var b bytes.Buffer
		z := zip.NewWriter(&b)
		for _, name := range names {
			w, err := z.Create(name)
			if err != nil {
				t.Fatal(err)
			}
			if _, err = w.Write([]byte("{}")); err != nil {
				t.Fatal(err)
			}
		}
		if err := z.Close(); err != nil {
			t.Fatal(err)
		}
		archive := filepath.Join(t.TempDir(), "a.zip")
		if err := os.WriteFile(archive, b.Bytes(), 0o600); err != nil {
			t.Fatal(err)
		}
		if err := extract(archive, t.TempDir()); err == nil {
			t.Fatalf("accepted %v", names)
		}
	}
	var b bytes.Buffer
	z := zip.NewWriter(&b)
	h := &zip.FileHeader{Name: "link.json"}
	h.SetMode(os.ModeSymlink | 0o777)
	w, err := z.CreateHeader(h)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = w.Write([]byte("../escape")); err != nil {
		t.Fatal(err)
	}
	if err = z.Close(); err != nil {
		t.Fatal(err)
	}
	archive := filepath.Join(t.TempDir(), "link.zip")
	if err = os.WriteFile(archive, b.Bytes(), 0o600); err != nil {
		t.Fatal(err)
	}
	if err = extract(archive, t.TempDir()); err == nil {
		t.Fatal("accepted archive link")
	}
}
func TestSourceAllowsEquivalentCherryPick(t *testing.T) {
	repo, base, head := sourceRepo(t)
	testGit(t, repo, "checkout", "-qb", "equivalent", base)
	testGit(t, repo, "cherry-pick", head)
	if err := VerifySource(repo, Run{TestedCommit: head, BaseCommit: base}); err != nil {
		t.Fatal(err)
	}
}

func TestActionsCancellationAndForeignRepositoryFail(t *testing.T) {
	api, p, _, _ := importFixture(t)
	endpoint := "repos/" + p.Trust.Repository + "/actions/runs/1000/attempts/1"
	original := api[endpoint]
	for _, conclusion := range []string{"cancelled", "timed_out", "skipped", "unknown"} {
		var run map[string]json.RawMessage
		if err := json.Unmarshal(original, &run); err != nil {
			t.Fatal(err)
		}
		run["conclusion"] = jsonBytes(t, conclusion)
		api[endpoint] = jsonBytes(t, run)
		if _, err := Authenticate(api, p); err == nil {
			t.Fatalf("accepted %s run", conclusion)
		}
	}
	var run map[string]json.RawMessage
	if err := json.Unmarshal(original, &run); err != nil {
		t.Fatal(err)
	}
	run["head_repository"] = raw(`{"full_name":"fork/repo"}`)
	api[endpoint] = jsonBytes(t, run)
	if _, err := Authenticate(api, p); err == nil {
		t.Fatal("accepted fork artifact")
	}
}
