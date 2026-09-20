package main

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func progressFixture(t *testing.T) progressPlan {
	t.Helper()
	root := t.TempDir()
	p := progressPlan{Shard: "s7", RunID: "123", Attempt: "2", HeadSHA: strings.Repeat("a", 40), TestedCommit: strings.Repeat("b", 40), Cases: []string{"smoke/identity", "food/reserve"}, Outputs: map[string]string{}}
	for _, name := range p.Cases {
		p.Outputs[name] = filepath.Join(root, filepath.FromSlash(name), "result.json")
	}
	return p
}

func writeProgressFile(t *testing.T, path, data string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(data), 0600); err != nil {
		t.Fatal(err)
	}
}

func TestProgressRendering(t *testing.T) {
	p := progressFixture(t)
	states := []progressState{{Status: "passed", Wall: 842 * time.Second}, {Status: "running", Since: time.Date(2026, 9, 20, 8, 14, 0, 0, time.UTC)}}
	name, output := renderProgress(p, states, false)
	if name != "acceptance-s7 · 1/2 · food/reserve" || !strings.Contains(output.Text, "14m2s") || !strings.Contains(output.Text, "since 08:14Z") || !strings.Contains(output.Summary, p.TestedCommit) {
		t.Fatalf("render: %s %#v", name, output)
	}
	name, output = renderProgress(p, states, true)
	if name != "acceptance-s7 · 1 failed" || !strings.Contains(output.Text, "missing result") {
		t.Fatalf("incomplete: %s %#v", name, output)
	}
	states[1] = progressState{Status: "passed"}
	name, _ = renderProgress(p, states, true)
	if name != "acceptance-s7 · 2/2 passed" {
		t.Fatal(name)
	}
}

func TestProgressObservationTransitionsAndUntrustedData(t *testing.T) {
	p := progressFixture(t)
	states := make([]progressState, len(p.Cases))
	transitions := 0
	poll := func() {
		t.Helper()
		if err := observeProgress(p, states, time.Date(2026, 9, 20, 8, 14, 0, 0, time.UTC), func() error { transitions++; return nil }); err != nil {
			t.Fatal(err)
		}
	}
	poll()
	path := p.Outputs[p.Cases[0]]
	writeProgressFile(t, filepath.Dir(path)+".log", "PRIVATE LOG")
	poll()
	poll()
	if transitions != 1 || states[0].Status != "running" {
		t.Fatalf("start: %d %#v", transitions, states)
	}
	writeProgressFile(t, path, `{"passed":`)
	poll()
	writeProgressFile(t, path, `{"error":"PRIVATE ERROR","argv":"PRIVATE ARGV","wall_ms":842000}`)
	poll()
	if transitions != 1 {
		t.Fatal("partial/missing verdict transitioned")
	}
	writeProgressFile(t, path, `{"passed":true,"error":"PRIVATE ERROR","wall_ms":842000}`)
	// A report outside the plan must not appear.
	writeProgressFile(t, filepath.Join(filepath.Dir(path), "unexpected", "result.json"), `{"passed":false}`)
	poll()
	poll()
	writeProgressFile(t, path, `{"passed":false}`)
	poll()
	if transitions != 2 || states[0].Status != "passed" {
		t.Fatalf("terminal state not latched: %d %#v", transitions, states)
	}
	_, output := renderProgress(p, states, false)
	if strings.Contains(output.Text, "PRIVATE") || strings.Contains(output.Text, "unexpected") {
		t.Fatal(output.Text)
	}
	writeProgressFile(t, p.Outputs[p.Cases[1]], `{"passed":false,"wall_ms":-100}`)
	poll()
	if transitions != 3 || states[1].Status != "failed" || states[1].Wall != 0 {
		t.Fatalf("failed: %d %#v", transitions, states)
	}
}

func TestProgressWatchAPIAndConclusion(t *testing.T) {
	for _, outcome := range []string{"success", "failure", "missing"} {
		t.Run(outcome, func(t *testing.T) {
			p := progressFixture(t)
			p.Cases = p.Cases[:1]
			root := t.TempDir()
			idFile, stop := filepath.Join(root, "id"), filepath.Join(root, "stop")
			path := p.Outputs[p.Cases[0]]
			var requests []checkUpdate
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.Header.Get("Authorization") != "Bearer sentinel" {
					t.Error("missing stdin credential")
				}
				var body checkUpdate
				if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
					t.Error(err)
				}
				requests = append(requests, body)
				switch len(requests) {
				case 1:
					if r.Method != "POST" || r.URL.Path != "/repos/owner/repo/check-runs" || body.Status != "in_progress" || body.HeadSHA != p.HeadSHA || body.ExternalID != "123:2:s7" || !strings.Contains(body.Output.Text, "queued") {
						t.Errorf("create: %#v %s", body, r.URL)
					}
					writeProgressFile(t, filepath.Dir(path)+".log", "")
					fmt.Fprint(w, `{"id":42}`)
				case 2:
					if body.Status != "" || !strings.Contains(body.Output.Text, "running") {
						t.Errorf("start: %#v", body)
					}
					if outcome != "missing" {
						writeProgressFile(t, path, fmt.Sprintf(`{"passed":%t,"wall_ms":1200,"error":"SECRET"}`, outcome == "success"))
					}
					writeProgressFile(t, stop, "")
				default:
					if r.Method != "PATCH" || r.URL.Path != "/repos/owner/repo/check-runs/42" {
						t.Errorf("patch: %s %s", r.Method, r.URL)
					}
				}
			}))
			defer srv.Close()
			a := progressAPI{client: srv.Client(), base: srv.URL, repo: "owner/repo", token: "sentinel"}
			if err := a.watch(p, idFile, stop, make(chan time.Time)); err != nil {
				t.Fatal(err)
			}
			wantCount, conclusion := 4, "failure"
			if outcome == "missing" {
				wantCount = 3
			}
			if outcome == "success" {
				conclusion = "success"
			}
			if len(requests) != wantCount {
				t.Fatalf("requests=%d want %d", len(requests), wantCount)
			}
			last := requests[len(requests)-1]
			if last.Status != "completed" || last.Conclusion != conclusion || strings.Contains(last.Output.Text, "SECRET") {
				t.Fatalf("final: %#v", last)
			}
			id, err := os.ReadFile(idFile)
			if err != nil || string(id) != "42" {
				t.Fatalf("id %s %v", id, err)
			}
		})
	}
}

func TestProgressAPIErrorStopsRequests(t *testing.T) {
	for _, failureAt := range []int{1, 2} {
		for _, code := range []int{403, 503} {
			t.Run(fmt.Sprintf("%d-%d", failureAt, code), func(t *testing.T) {
				p := progressFixture(t)
				count := 0
				writeProgressFile(t, filepath.Dir(p.Outputs[p.Cases[0]])+".log", "")
				srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
					count++
					if count == failureAt {
						http.Error(w, "SECRET", code)
						return
					}
					fmt.Fprint(w, `{"id":7}`)
				}))
				defer srv.Close()
				a := progressAPI{client: srv.Client(), base: srv.URL, repo: "owner/repo", token: "sentinel"}
				err := a.watch(p, filepath.Join(t.TempDir(), "id"), "absent", make(chan time.Time))
				if err == nil || strings.Contains(err.Error(), "SECRET") || count != failureAt {
					t.Fatalf("requests=%d err=%v", count, err)
				}
			})
		}
	}
}

func TestProgressFinishPreservesVerdictAndTable(t *testing.T) {
	root := t.TempDir()
	id := filepath.Join(root, "id")
	writeProgressFile(t, id, "42")
	requests := 0
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests++
		if r.Method == "GET" {
			fmt.Fprint(w, `{"id":42,"external_id":"123:2:s7","status":"completed","output":{"title":"done","summary":"tested commit","text":"table"}}`)
			return
		}
		var body checkUpdate
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			t.Error(err)
		}
		if body.Status != "" || body.Conclusion != "" || body.Name != "" || body.Output.Text != "table" || !strings.Contains(body.Output.Summary, "[Shard diagnostics]") {
			t.Errorf("finish changed verdict/table: %#v", body)
		}
	}))
	defer srv.Close()
	a := progressAPI{client: srv.Client(), base: srv.URL, repo: "owner/repo", token: "sentinel"}
	if err := a.finish(id, "123:2:s7", "https://github.com/owner/repo/actions/runs/123/artifacts/99"); err != nil {
		t.Fatal(err)
	}
	if requests != 2 {
		t.Fatal(requests)
	}
	if err := a.finish(id, "123:1:s7", "https://github.com/owner/repo/actions/runs/123/artifacts/99"); err == nil || requests != 3 {
		t.Fatal("updated another attempt")
	}
}

func TestProgressSweepPaginatesAndIsolatesAttempt(t *testing.T) {
	gets, patches := 0, 0
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == "PATCH" {
			patches++
			if r.URL.Path != "/repos/owner/repo/check-runs/201" {
				t.Errorf("swept unrelated check %s", r.URL)
			}
			var body checkUpdate
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
				t.Error(err)
			}
			if body.Status != "completed" || body.Conclusion != "cancelled" {
				t.Errorf("sweep: %#v", body)
			}
			return
		}
		gets++
		if r.URL.Query().Get("filter") != "all" {
			t.Error("rerun checks hidden")
		}
		checks := []checkRun{{ID: 201, ExternalID: "123:2:s7", Status: "in_progress"}, {ID: 202, ExternalID: "123:1:s7", Status: "in_progress"}, {ID: 203, ExternalID: "123:2:s8", Status: "completed"}, {ID: 204, ExternalID: "999:2:s7", Status: "in_progress"}, {ID: 205, ExternalID: "123:2:unrelated", Status: "in_progress"}}
		if r.URL.Query().Get("page") == "1" {
			checks = make([]checkRun, 100)
		}
		if err := json.NewEncoder(w).Encode(struct {
			Checks []checkRun `json:"check_runs"`
		}{checks}); err != nil {
			t.Error(err)
		}
	}))
	defer srv.Close()
	a := progressAPI{client: srv.Client(), base: srv.URL, repo: "owner/repo", token: "sentinel"}
	if err := a.sweep(strings.Repeat("a", 40), "123:2:"); err != nil {
		t.Fatal(err)
	}
	if gets != 2 || patches != 1 {
		t.Fatalf("gets=%d patches=%d", gets, patches)
	}
}

func TestProgressPlanUsesTrustedRegistryAndRoleOrder(t *testing.T) {
	root := t.TempDir()
	t.Setenv("GITHUB_RUN_ID", "123")
	t.Setenv("GITHUB_RUN_ATTEMPT", "2")
	t.Setenv("GITHUB_SHA", strings.Repeat("a", 40))
	registered := cases.All()
	if len(registered) < 2 {
		t.Fatal("trusted registry empty")
	}
	first, second := registered[0], registered[1]
	selection := fmt.Sprintf(`{"shards":[{"id":"s7","cases":[%q,%q]}],"cases":[{"name":%q,"mod_role":"production"},{"name":%q,"mod_role":"fixture"}]}`, first.Name, second.Name, first.Name, second.Name)
	writeProgressFile(t, filepath.Join(root, "selection.json"), selection)
	writeProgressFile(t, filepath.Join(root, "run.json"), `{"tested_commit":"`+strings.Repeat("b", 40)+`"}`)
	p, err := prepareProgress(root, "s7", root)
	if err != nil {
		t.Fatal(err)
	}
	want := filepath.Join((cases.Options{Output: filepath.Join(root, "fixture", "job", "out")}).CaseOutput(second), "result.json")
	if p.Cases[0] != second.Name || p.Outputs[second.Name] != want {
		t.Fatalf("projection: %#v", p)
	}
	writeProgressFile(t, filepath.Join(root, "selection.json"), strings.ReplaceAll(selection, first.Name, "untrusted/name"))
	if _, err := prepareProgress(root, "s7", root); err == nil {
		t.Fatal("accepted unknown case")
	}
	if err := run([]string{"progress", "-prepare", "-root", root, "-shard", "s7"}); err != nil {
		t.Fatal("optional progress failed shard", err)
	}
}
