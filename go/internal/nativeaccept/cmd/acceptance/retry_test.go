package main

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestProductionRetryClassifierFailsClosed(t *testing.T) {
	for _, message := range []string{"native_error rimgovernor/observations_read_status: context deadline exceeded", "uncertain write", "assertion failed", "stall budget exhausted", "setup failed", "context canceled", "go test failed"} {
		d := classifyAttempt(map[string]any{"passed": false, "error": message, "classification": "infrastructure"})
		if d.eligible || d.classification != "unknown" {
			t.Fatalf("%q: %+v", message, d)
		}
	}
}

func TestBoundedAttempts(t *testing.T) {
	for _, tc := range []struct {
		name, failure                                                                                string
		secondFails, cancelBefore, cancelAfter, cancelCleanup, cleanupFails, resume, missingEvidence bool
		calls                                                                                        int
		disposition                                                                                  string
	}{
		{name: "read then pass", failure: "read", calls: 2, disposition: "retried_passed"},
		{name: "read twice", failure: "read", secondFails: true, calls: 2, disposition: "retried_failed"},
		{name: "uncertain write", failure: "write", calls: 1, disposition: "failed"},
		{name: "assertion", failure: "assertion", calls: 1, disposition: "failed"},
		{name: "budget", failure: "budget", calls: 1, disposition: "failed"},
		{name: "stall", failure: "stall", calls: 1, disposition: "failed"},
		{name: "setup", failure: "setup", calls: 1, disposition: "failed"},
		{name: "unknown", failure: "unknown", calls: 1, disposition: "failed"},
		{name: "cancel before", cancelBefore: true, calls: 0, disposition: "cancelled"},
		{name: "cancel after", failure: "read", cancelAfter: true, calls: 1, disposition: "cancelled"},
		{name: "cancel cleanup", failure: "read", cancelCleanup: true, calls: 1, disposition: "cancelled"},
		{name: "cleanup fails", failure: "read", cleanupFails: true, calls: 1, disposition: "failed"},
		{name: "resume cannot retry", failure: "read", resume: true, calls: 1, disposition: "failed"},
		{name: "missing evidence", failure: "read", missingEvidence: true, calls: 1, disposition: "failed"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			if tc.cancelBefore {
				cancel()
			}
			opts := suiteOptions{Output: t.TempDir(), Resume: tc.resume}
			calls, stops := 0, 0
			run := func(ctx context.Context, e entry, current suiteOptions, self, root string, worker int, stderr io.Writer) map[string]any {
				calls++
				if calls == 2 && (stops != 1 || current.Resume || current.Output == opts.Output) {
					t.Fatal("retry not isolated, cleaned and fresh")
				}
				output := filepath.Join(current.Output, "fake", "case")
				if err := os.MkdirAll(output, 0755); err != nil {
					t.Fatal(err)
				}
				passed := calls == 2 && !tc.secondFails
				exit := 1
				if passed {
					exit = 0
				}
				body := []byte(`{"passed":false}`)
				if passed {
					body = []byte(`{"passed":true}`)
				}
				if !tc.missingEvidence {
					if err := os.WriteFile(filepath.Join(output, "result.json"), body, 0644); err != nil {
						t.Fatal(err)
					}
					if err := os.WriteFile(output+".log", []byte(tc.failure), 0644); err != nil {
						t.Fatal(err)
					}
				}
				if tc.cancelAfter {
					cancel()
				}
				return map[string]any{"name": e.Name, "passed": passed, "exit": exit, "output": output, "log": output + ".log", "failure_kind": tc.failure, "wall_ms": int64(7)}
			}
			// Test-only classifier: no report field can enable this in production.
			classify := func(row map[string]any) retryDecision {
				kind, _ := row["failure_kind"].(string)
				if kind == "read" {
					return retryDecision{"infrastructure", true}
				}
				return retryDecision{"unknown", false}
			}
			cleanup := func(ctx context.Context, root, game string) error {
				stops++
				if tc.cancelCleanup {
					cancel()
				}
				if tc.cleanupFails {
					return errors.New("owned process still running")
				}
				return nil
			}
			row := runEntryAttempts(ctx, entry{Name: "fake/case"}, opts, "self", "owned-root", 1, io.Discard, run, classify, cleanup)
			if calls != tc.calls || row["disposition"] != tc.disposition {
				t.Fatalf("calls=%d row=%+v", calls, row)
			}
			attempts := row["attempts"].([]caseAttempt)
			if len(attempts) != calls || row["wall_ms"] != int64(calls*7) {
				t.Fatal("lost attempt accounting")
			}
			encoded, err := json.Marshal(row)
			if err != nil {
				t.Fatal(err)
			}
			var decoded struct {
				Attempts []caseAttempt `json:"attempts"`
			}
			if err := json.Unmarshal(encoded, &decoded); err != nil {
				t.Fatal(err)
			}
			for i, a := range decoded.Attempts {
				if a.Number != i+1 || a.StartedAt.IsZero() || a.FinishedAt.Before(a.StartedAt) || a.Exit == nil {
					t.Fatalf("bad metadata: %+v", a)
				}
				if i == 0 && a.RetryOf != nil || i == 1 && (a.RetryOf == nil || *a.RetryOf != 1) {
					t.Fatal("bad retry linkage")
				}
				if tc.missingEvidence {
					continue
				}
				if a.Evidence == nil || a.Log == nil {
					t.Fatal("lost evidence")
				}
				ref := evidenceReference(opts.Output, filepath.Join(opts.Output, filepath.FromSlash(a.Evidence.Path)))
				if ref == nil || *ref != *a.Evidence {
					t.Fatal("evidence changed")
				}
				if i == 0 {
					data, err := os.ReadFile(filepath.Join(opts.Output, filepath.FromSlash(a.Evidence.Path)))
					if err != nil || string(data) != `{"passed":false}` {
						t.Fatal("first failure overwritten")
					}
				}
			}
		})
	}
}

func TestAttemptRejectsNonzeroExitDespiteNativePass(t *testing.T) {
	run := func(context.Context, entry, suiteOptions, string, string, int, io.Writer) map[string]any {
		return map[string]any{"passed": true, "exit": 1}
	}
	row := runEntryAttempts(context.Background(), entry{Name: "x/y"}, suiteOptions{Output: t.TempDir()}, "", "", 1, io.Discard, run, classifyAttempt, nil)
	if row["passed"] != false || row["disposition"] != "failed" {
		t.Fatalf("%+v", row)
	}
}

func TestExpiredSuiteCannotStartAttempt(t *testing.T) {
	ctx, cancel := context.WithDeadline(context.Background(), time.Now().Add(-time.Second))
	defer cancel()
	run := func(context.Context, entry, suiteOptions, string, string, int, io.Writer) map[string]any {
		t.Fatal("started after suite deadline")
		return nil
	}
	row := runEntryAttempts(ctx, entry{Name: "x/y"}, suiteOptions{Output: t.TempDir()}, "", "", 1, io.Discard, run, classifyAttempt, nil)
	if row["passed"] != false || row["disposition"] != "timed_out" || row["attempt_count"] != 0 || row["final_attempt"] != nil {
		t.Fatalf("%+v", row)
	}
}

func TestSuccessfulAttemptDoesNotRetry(t *testing.T) {
	run := func(context.Context, entry, suiteOptions, string, string, int, io.Writer) map[string]any {
		return map[string]any{"passed": true, "exit": 0}
	}
	row := runEntryAttempts(context.Background(), entry{Name: "x/y"}, suiteOptions{Output: t.TempDir()}, "", "", 1, io.Discard, run, classifyAttempt, nil)
	attempts := row["attempts"].([]caseAttempt)
	if row["disposition"] != "passed" || len(attempts) != 1 || attempts[0].Status != "passed" || attempts[0].Classification != "none" {
		t.Fatalf("%+v", row)
	}
}

func TestRetryCommandIsFreshRestaged(t *testing.T) {
	list := []entry{{Name: "authority/warm"}}
	if err := resolveEntries(list); err != nil {
		t.Fatal(err)
	}
	argv, _ := entryCommand(list[0], suiteOptions{Output: t.TempDir()}, "acceptance", "root")
	joined := strings.Join(argv, " ")
	for _, flag := range []string{"-fresh", "-checkpoint-every 0", "-restage"} {
		if !strings.Contains(joined, flag) {
			t.Fatalf("missing %s: %s", flag, joined)
		}
	}
}
