package remoteaccept

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

type fixture struct {
	root      string
	run       Run
	selection Selection
	attempts  []Attempts
	shards    []Shard
}

func fixtureRun(t *testing.T) *fixture {
	t.Helper()
	root := t.TempDir()
	source := filepath.Join("..", "..", "..", "docs", "developers", "contracts", "remote-acceptance")
	if err := os.CopyFS(root, os.DirFS(source)); err != nil {
		t.Fatal(err)
	}
	f := &fixture{root: root}
	readTest(t, root, "run.json", &f.run)
	readTest(t, root, "selection.json", &f.selection)
	var a Aggregate
	readTest(t, root, "aggregate.json", &a)
	f.shards = a.Shards
	for _, s := range f.shards {
		var a Attempts
		readTest(t, root, s.Attempts.Path, &a)
		f.attempts = append(f.attempts, a)
	}
	return f
}
func readTest(t *testing.T, root, p string, v any) {
	t.Helper()
	b, err := os.ReadFile(filepath.Join(root, p))
	if err != nil {
		t.Fatal(err)
	}
	if err = Decode(b, v); err != nil {
		t.Fatal(err)
	}
}
func writeTest(t *testing.T, root, p string, v any) Ref {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(filepath.Join(root, p)), 0o755); err != nil {
		t.Fatal(err)
	}
	r, err := WriteJSON(root, p, v)
	if err != nil {
		t.Fatal(err)
	}
	return r
}
func (f *fixture) save(t *testing.T) (Ref, Ref) {
	t.Helper()
	r := writeTest(t, f.root, "run.json", f.run)
	f.selection.Run = r
	s := writeTest(t, f.root, "selection.json", f.selection)
	for i := range f.attempts {
		f.attempts[i].Run = r
		f.attempts[i].Selection = s
		a := writeTest(t, f.root, f.shards[i].ID+"/attempts.json", f.attempts[i])
		f.shards[i].Attempts = &a
	}
	return r, s
}
func (f *fixture) evaluate(t *testing.T) (Evaluation, error) {
	t.Helper()
	r, s := f.save(t)
	return Evaluate(f.root, r, s, f.shards)
}
func (f *fixture) native(t *testing.T, i, j int, edit func(map[string]json.RawMessage)) {
	t.Helper()
	a := &f.attempts[i].Attempts[j]
	var row map[string]json.RawMessage
	readTest(t, f.root, a.Evidence.Path, &row)
	edit(row)
	a.Evidence = writeTest(t, f.root, a.Evidence.Path, row)
}
func raw(s string) json.RawMessage { return json.RawMessage(s) }
func ptr[T any](v T) *T            { return &v }

func TestContractExamples(t *testing.T) {
	f := fixtureRun(t)
	ref, err := FileRef(f.root, "aggregate.json")
	if err != nil {
		t.Fatal(err)
	}
	e, err := Verify(f.root, ref)
	if err != nil {
		t.Fatal(err)
	}
	if !e.Report.Passed || len(e.Report.Cases) != 6 {
		t.Fatalf("%+v", e.Aggregate)
	}
	if err = rejectSynthetic(e); err == nil {
		t.Fatal("fixture accepted as real native evidence")
	}
}

func TestHostedTimeoutLimits(t *testing.T) {
	for _, tc := range []struct {
		job, suite int
		valid      bool
	}{{360, 345, true}, {60, 45, true}, {361, 345, false}, {360, 346, false}, {60, 60, false}} {
		f := fixtureRun(t)
		f.run.Limits.JobMinutes, f.run.Limits.SuiteMinutes = tc.job, tc.suite
		_, err := f.evaluate(t)
		if (err == nil) != tc.valid {
			t.Fatalf("job=%d suite=%d: %v", tc.job, tc.suite, err)
		}
	}
}

func TestHostedConcurrencyLimits(t *testing.T) {
	for _, parallel := range []int{0, 1, 4, 20, 21} {
		f := fixtureRun(t)
		f.run.Limits.Parallel = parallel
		_, err := f.evaluate(t)
		if (err == nil) != (parallel >= 1 && parallel <= 20) {
			t.Fatalf("parallel=%d: %v", parallel, err)
		}
	}
}

func TestArtifactBudgetIsOptionalAndNotAPlanQuota(t *testing.T) {
	for _, cap := range []int64{-1, 0, 1 << 30, 8 << 30} {
		f := fixtureRun(t)
		f.run.Limits.Bytes = cap
		_, err := f.evaluate(t)
		if (err == nil) != (cap >= 0) {
			t.Fatalf("artifact cap %d: %v", cap, err)
		}
	}
}
func TestAggregationRejectsBadEvidence(t *testing.T) {
	tests := map[string]func(*testing.T, *fixture){
		"missing case": func(t *testing.T, f *fixture) { f.attempts[0].Attempts = f.attempts[0].Attempts[1:] },
		"duplicate case": func(t *testing.T, f *fixture) {
			f.attempts[0].Attempts = append(f.attempts[0].Attempts, f.attempts[0].Attempts[0])
		},
		"extra case":             func(t *testing.T, f *fixture) { f.attempts[0].Attempts[0].Case = "unknown/case" },
		"duplicate shard":        func(t *testing.T, f *fixture) { f.shards = append(f.shards, f.shards[0]) },
		"cancelled job":          func(t *testing.T, f *fixture) { f.shards[0].Status = "cancelled" },
		"timed out job":          func(t *testing.T, f *fixture) { f.shards[0].Status = "timed_out" },
		"bad native hash":        func(t *testing.T, f *fixture) { f.attempts[0].Attempts[0].Evidence.SHA256 = strings.Repeat("0", 64) },
		"wrong selection source": func(t *testing.T, f *fixture) { f.selection.Planner = strings.Repeat("0", 40) },
		"wrong shard identity":   func(t *testing.T, f *fixture) { f.attempts[0].ShardID = "s2" },
		"missing native result": func(t *testing.T, f *fixture) {
			if err := os.Remove(filepath.Join(f.root, f.attempts[0].Attempts[0].Evidence.Path)); err != nil {
				t.Fatal(err)
			}
		},
		"gapped attempt": func(t *testing.T, f *fixture) { f.attempts[0].Attempts[0].Number = 2 },
		"unknown status": func(t *testing.T, f *fixture) { f.attempts[0].Attempts[0].Status = "green" },
		"wrong case report": func(t *testing.T, f *fixture) {
			f.native(t, 0, 0, func(m map[string]json.RawMessage) { m["name"] = raw(`"wrong/case"`) })
		},
		"native false with exit zero": func(t *testing.T, f *fixture) {
			f.native(t, 0, 0, func(m map[string]json.RawMessage) { m["passed"] = raw(`false`) })
		},
		"staged": func(t *testing.T, f *fixture) {
			f.native(t, 0, 0, func(m map[string]json.RawMessage) { m["staged_from"] = raw(`{"stage":"ring"}`) })
		},
		"postmortem": func(t *testing.T, f *fixture) {
			f.native(t, 0, 0, func(m map[string]json.RawMessage) { m["postmortem_only"] = raw(`true`) })
		},
		"resumed": func(t *testing.T, f *fixture) {
			f.native(t, 0, 0, func(m map[string]json.RawMessage) { m["resumed_from"] = raw(`{"label":"t+7m"}`) })
		},
		"missing diagnostics": func(t *testing.T, f *fixture) {
			f.native(t, 0, 0, func(m map[string]json.RawMessage) { m["log"] = raw(`"s1/missing.log"`) })
		},
		"absolute log": func(t *testing.T, f *fixture) {
			f.native(t, 0, 0, func(m map[string]json.RawMessage) { m["log"] = raw(`"C:/private/log.txt"`) })
		},
		"missing required passed": func(t *testing.T, f *fixture) {
			f.native(t, 0, 0, func(m map[string]json.RawMessage) { delete(m, "passed") })
		},
	}
	for name, edit := range tests {
		t.Run(name, func(t *testing.T) {
			// Each edit copies the contract fixture into its own directory;
			// the copies are independent, and serial file creation under a
			// loaded Windows suite ran this test past its ceiling (#434).
			t.Parallel()
			f := fixtureRun(t)
			edit(t, f)
			e, err := f.evaluate(t)
			if err == nil && e.Aggregate.Passed {
				t.Fatal("bad evidence passed")
			}
		})
	}
}
func TestMissingShardAndFailurePrecedence(t *testing.T) {
	f := fixtureRun(t)
	r, s := f.save(t)
	e, err := Evaluate(f.root, r, s, f.shards[:1])
	if err != nil {
		t.Fatal(err)
	}
	if e.Aggregate.Status != "incomplete" || e.Aggregate.Passed {
		t.Fatalf("%+v", e.Aggregate)
	}
	for _, c := range e.Aggregate.Cases {
		if c.ShardID == "s2" && (c.AttemptCount != 0 || c.FinalAttempt != nil) {
			t.Fatalf("%+v", c)
		}
	}
}
func (f *fixture) failure(t *testing.T) {
	a := &f.attempts[0].Attempts[0]
	a.Status = "failed"
	a.Classification = "assertion"
	a.Exit = ptr(1)
	a.Error = ptr("injected native postcondition failure")
	f.native(t, 0, 0, func(m map[string]json.RawMessage) {
		m["passed"] = raw(`false`)
		m["exit"] = raw(`1`)
		m["error"] = raw(`"injected native postcondition failure"`)
	})
}
func TestInjectedFailureStaysRed(t *testing.T) {
	f := fixtureRun(t)
	f.failure(t)
	e, err := f.evaluate(t)
	if err != nil {
		t.Fatal(err)
	}
	if e.Aggregate.Passed || e.Aggregate.Status != "failed" {
		t.Fatal("injected failure passed")
	}
	ref := writeTest(t, f.root, "aggregate.json", e.Aggregate)
	if _, err = Verify(f.root, ref); err != nil {
		t.Fatal(err)
	}
	e.Aggregate.Passed = true
	e.Aggregate.Status = "passed"
	e.Aggregate.Error = nil
	ref = writeTest(t, f.root, "aggregate.json", e.Aggregate)
	if _, err = Verify(f.root, ref); err == nil {
		t.Fatal("forged top-level pass accepted")
	}
}
func TestRetryRetainsFailureAndDiagnostics(t *testing.T) {
	f := fixtureRun(t)
	original := f.attempts[0].Attempts[0]
	f.failure(t)
	f.attempts[0].Attempts[0].Classification = "infrastructure"
	log := writeTest(t, f.root, "s1/first-diagnostics.json", struct {
		Message string `json:"message"`
	}{"infrastructure failure"})
	f.native(t, 0, 0, func(m map[string]json.RawMessage) {
		m["evidence_files"], _ = json.Marshal([]Ref{log})
		m["metrics"] = raw(`{"wall_ms":1200}`)
	})
	retry := original
	retry.Number = 2
	retry.RetryOf = ptr(1)
	retry.Started = "2026-09-19T12:01:00Z"
	retry.Finished = "2026-09-19T12:02:00Z"
	retry.Evidence = writeTest(t, f.root, "s1/retry/light-dark.json", struct {
		Name   string `json:"name"`
		Passed bool   `json:"passed"`
		Exit   int    `json:"exit"`
	}{retry.Case, true, 0})
	f.attempts[0].Attempts = append(f.attempts[0].Attempts, retry)
	e, err := f.evaluate(t)
	if err != nil {
		t.Fatal(err)
	}
	if !e.Report.Passed || e.Aggregate.Cases[0].AttemptCount != 2 {
		t.Fatalf("%+v", e.Aggregate)
	}
	if !strings.Contains(strings.Join(e.Files, "\n"), log.Path) {
		t.Fatal("first attempt diagnostics lost")
	}
	f.attempts[0].Attempts[0].Classification = "assertion"
	if e, err = f.evaluate(t); err == nil && e.Report.Passed {
		t.Fatal("assertion retry hid a failure")
	}
}
func TestJSONAndPathsFailClosed(t *testing.T) {
	for _, b := range []string{`{"path":"x","path":"y","sha256":"x"}`, `{"path":"x","Path":"y","sha256":"x"}`, `{"path":"x"}`, `{"path":null,"sha256":"x"}`, `{"path":"x","sha256":"x"} true`} {
		var r Ref
		if err := Decode([]byte(b), &r); err == nil {
			t.Fatalf("accepted %s", b)
		}
	}
	for _, p := range []string{"../escape", "a/../b", "/abs", "C:/abs", "a\\b", "a/file:stream", "a/NUL.json", "a/b.", "a/b "} {
		if validPath(p) {
			t.Fatalf("accepted path %s", p)
		}
	}
}
func TestNativeAndSuiteFormats(t *testing.T) {
	f := fixtureRun(t)
	f.native(t, 0, 0, func(m map[string]json.RawMessage) { m["case"] = m["name"]; delete(m, "name"); delete(m, "exit") })
	if e, err := f.evaluate(t); err != nil || !e.Report.Passed {
		t.Fatalf("native case format: %v", err)
	}
	var rows []json.RawMessage
	for _, a := range f.attempts[1].Attempts {
		b, err := os.ReadFile(filepath.Join(f.root, a.Evidence.Path))
		if err != nil {
			t.Fatal(err)
		}
		rows = append(rows, b)
	}
	suite := struct {
		Passed bool              `json:"passed"`
		Cases  []json.RawMessage `json:"cases"`
	}{true, rows}
	ref := writeTest(t, f.root, "s2/suite.json", suite)
	for i := range f.attempts[1].Attempts {
		f.attempts[1].Attempts[i].Evidence = ref
	}
	if e, err := f.evaluate(t); err != nil || !e.Report.Passed {
		t.Fatalf("suite format: %v", err)
	}
	suite.Cases = append(suite.Cases, suite.Cases[0])
	ref = writeTest(t, f.root, "s2/suite.json", suite)
	for i := range f.attempts[1].Attempts {
		f.attempts[1].Attempts[i].Evidence = ref
	}
	if e, err := f.evaluate(t); err == nil && e.Report.Passed {
		t.Fatal("duplicate suite case passed")
	}
}
