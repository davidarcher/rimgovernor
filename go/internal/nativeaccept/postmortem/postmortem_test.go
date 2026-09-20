package postmortem

import (
	"context"
	"database/sql"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// fixture writes a case output directory shaped like a failed serve-driven
// run: result.json, service/stderr.log, a flight recording with a rotated
// segment, and a service.sqlite holding only the tables the digest reads
// (an old schema: no actions table at all).
func fixture(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	write := func(name, content string) {
		t.Helper()
		path := filepath.Join(dir, name)
		if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte(content), 0644); err != nil {
			t.Fatal(err)
		}
	}
	write("result.json", `{"case":"storage/food","error":"no meal hauled","passed":false,"authority_reacquisitions":{"attempts":2}}`)
	write("service/stderr.log", strings.Join([]string{
		"[clock-scheduler] EvaluateClockWindow: work=false admitted=false refused=[] watched=0",
		"[worker] routine-haul-abc-0 stage=pending attempt=0 receipt=- effect=- refused=[] err=native write refused: FAILURE_CODE_INVALID_REQUEST: JobFailReason: no empty place configured",
		"[clock-scheduler] poll: interrupting gap=false events=*clockpb.Event_AuthorityChanged",
		"[clock-worker] step failed: building authority changed or disabled",
		"[worker] routine-haul-abc-1 stage=awaiting_observation err=bridge contract failure: pawn order job mismatch",
		"[worker] routine-haul-abc-0 stage=pending attempt=0 receipt=- effect=- refused=[] err=native write refused: FAILURE_CODE_INVALID_REQUEST: JobFailReason: no empty place configured",
	}, "\n")+"\n")
	write("flight.jsonl.1", `{"version":1,"run":"r","sequence":1,"wall_time":1,"kind":"native_request","context":{},"payload":{}}`+"\n")
	write("flight.jsonl", `{"version":1,"run":"r","sequence":2,"wall_time":2,"kind":"native_error","context":{},"payload":{"native_tool":"rimgovernor/orders_haul","error":"refused","refused_text":"the target is not a haulable item"}}`+"\n")

	db, err := sql.Open(store.DriverName, "file:"+filepath.ToSlash(filepath.Join(dir, "service.sqlite")))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	exec := func(q string, args ...any) {
		t.Helper()
		if _, err := db.Exec(q, args...); err != nil {
			t.Fatalf("%s: %v", q, err)
		}
	}
	exec(`CREATE TABLE routine_review(singleton INTEGER PRIMARY KEY, payload BLOB NOT NULL)`)
	exec(`CREATE TABLE goals(id TEXT PRIMARY KEY, revision TEXT NOT NULL, payload BLOB NOT NULL, retired INTEGER NOT NULL DEFAULT 0)`)
	exec(`CREATE TABLE plans(id TEXT PRIMARY KEY, revision TEXT NOT NULL, retired INTEGER NOT NULL DEFAULT 0)`)
	exec(`CREATE TABLE goal_methods(goal_id TEXT NOT NULL, epoch TEXT NOT NULL, method_id TEXT NOT NULL, plan_id TEXT NOT NULL)`)
	exec(`CREATE TABLE transitions(sequence INTEGER PRIMARY KEY, action_id TEXT NOT NULL, payload BLOB NOT NULL)`)
	review := map[string]any{"Revision": 7, "Tick": 1200, "Enabled": true, "AsOf": map[string]int64{"colony": 1200, "planning_cells": 1200, "research": 1175}, "Development": map[string]any{"Capacity": 2, "Rows": []map[string]any{
		{"Goal": "EnsureComfort", "Score": 10, "Selected": false, "Reason": "startup_survival"},
		{"Goal": "EnsureFoodStorage", "Score": 50, "Selected": true, "Reason": ""},
	}}}
	data, _ := json.Marshal(review)
	exec(`INSERT INTO routine_review VALUES(1,?)`, data)
	goal := func(id, status, need string) {
		payload, _ := json.Marshal(map[string]any{"ID": id, "Status": status, "Need": need, "Priority": 2})
		exec(`INSERT INTO goals(id,revision,payload) VALUES(?,'0',?)`, id, payload)
	}
	goal("routine-c-EnsureComfort", "active", "deficit")
	goal("routine-c-EnsureFoodStorage", "active", "deficit")
	goal("routine-c-EnsureCooking", "satisfied", "met")
	transition := func(seq int, action string, event map[string]any) {
		payload, _ := json.Marshal(event)
		exec(`INSERT INTO transitions VALUES(?,?,?)`, seq, action, payload)
	}
	transition(1, "a1", map[string]any{"Kind": "prepare", "Snapshot": map[string]any{"Native": 3}, "Tick": 10})
	transition(2, "a1", map[string]any{"Kind": "hold", "Snapshot": map[string]any{"Native": 0}, "Tick": 11})
	transition(3, "a1", map[string]any{"Kind": "observe", "Snapshot": map[string]any{"Native": 4}, "Tick": 40, "Observation": map[string]any{"Effect": "unsuccessful", "UnsuccessfulReason": "native_failure"}})
	transition(4, "a2", map[string]any{"Kind": "receipt", "Snapshot": map[string]any{"Native": 4}, "Tick": 41, "Receipt": "refused"})
	return dir
}

func section(t *testing.T, d Digest, name string) Section {
	t.Helper()
	for _, s := range d.Sections {
		if s.Name == name {
			return s
		}
	}
	t.Fatalf("no section %q in %v", name, d.Sections)
	return Section{}
}

func hasLine(s Section, text, evidence string) bool {
	for _, l := range s.Lines {
		if strings.Contains(l.Text, text) && strings.Contains(l.Evidence, evidence) {
			return true
		}
	}
	return false
}

func TestCollectReadsEachStepWithEvidence(t *testing.T) {
	dir := fixture(t)
	d := Collect(context.Background(), dir, nil)
	if d.Case != "storage/food" || d.Error != "no meal hauled" {
		t.Fatalf("header = %q %q", d.Case, d.Error)
	}
	names := make([]string, 0, len(d.Sections))
	for _, s := range d.Sections {
		names = append(names, s.Name)
	}
	want := []string{"revision", "stage graph", "native refusals (last first)", "routine review", "colony extent", "colony grid", "unsuccessful plan stages", "native job failures", "authority generations", "pooled-job mismatches"}
	if strings.Join(names, ",") != strings.Join(want, ",") {
		t.Fatalf("sections = %v", names)
	}
	if s := section(t, d, "revision"); s.Note == "" {
		t.Fatalf("a report without a source revision should say so: %+v", s)
	}
	refusals := section(t, d, "native refusals (last first)")
	if !hasLine(refusals, "JobFailReason: no empty place configured (x2)", "service/stderr.log:6") {
		t.Fatalf("refusals = %+v", refusals)
	}
	if hasLine(refusals, "EvaluateClockWindow", "") {
		t.Fatalf("an empty refused=[] list is not a refusal: %+v", refusals)
	}
	if !hasLine(refusals, "native_error rimgovernor/orders_haul: the target is not a haulable item", "flight.jsonl:1 seq 2") {
		t.Fatalf("flight native_error missing: %+v", refusals)
	}
	review := section(t, d, "routine review")
	if !hasLine(review, "EnsureComfort not selected: startup_survival", "Development.Rows[EnsureComfort]") {
		t.Fatalf("review = %+v", review)
	}
	// Sections read at different ticks show on the review line (#354).
	if !hasLine(review, "review revision 7 at tick 1200 enabled=true; development capacity 2 committed=[] as_of_spread=25", "routine_review") {
		t.Fatalf("as_of_spread missing: %+v", review)
	}
	if !hasLine(review, "goal routine-c-EnsureFoodStorage deficit/active priority 2 epoch 0: 0 live methods", "goals#routine-c-EnsureFoodStorage") {
		t.Fatalf("selected goal without methods missing: %+v", review)
	}
	if hasLine(review, "goal routine-c-EnsureComfort", "") || hasLine(review, "EnsureCooking", "") {
		t.Fatalf("a development-refused or satisfied goal is not a zero-method finding: %+v", review)
	}
	stages := section(t, d, "unsuccessful plan stages")
	if !hasLine(stages, "a1 () unsuccessful at tick 40: native_failure", "transitions#3") || !hasLine(stages, "a2 () receipt refused at tick 41", "transitions#4") {
		t.Fatalf("stages = %+v", stages)
	}
	jobs := section(t, d, "native job failures")
	if !hasLine(jobs, "JobFailReason", "service/stderr.log:2") {
		t.Fatalf("jobs = %+v", jobs)
	}
	authority := section(t, d, "authority generations")
	if !hasLine(authority, "generation 3 -> 4", "transitions#3") || !hasLine(authority, "generation 3 first, 4 last, 1 flips", "transitions#4") {
		t.Fatalf("authority = %+v", authority)
	}
	if !hasLine(authority, "1 poll lines carried AuthorityChanged", "service/stderr.log:3") || !hasLine(authority, "1 steps failed on authority", "service/stderr.log:4") {
		t.Fatalf("authority log lines = %+v", authority)
	}
	if !hasLine(authority, "authority_reacquisitions=map[attempts:2]", "result.json") {
		t.Fatalf("report reacquisitions = %+v", authority)
	}
	pooled := section(t, d, "pooled-job mismatches")
	if !hasLine(pooled, "pawn order job mismatch", "service/stderr.log:5") {
		t.Fatalf("pooled = %+v", pooled)
	}
	text := d.Text()
	if !strings.HasPrefix(text, "case: storage/food\nerror: no meal hauled\n## revision\n") || !strings.Contains(text, "  - a1 () unsuccessful at tick 40: native_failure  [service.sqlite transitions#3]\n") {
		t.Fatalf("text:\n%s", text)
	}
}

func TestCollectToleratesMissingEvidence(t *testing.T) {
	dir := t.TempDir()
	d := Collect(context.Background(), dir, map[string]any{"case": "x", "error": "boom"})
	for _, s := range d.Sections {
		if s.Note == "" && len(s.Lines) == 0 {
			t.Fatalf("section %s is silent on missing evidence", s.Name)
		}
	}
	if s := section(t, d, "routine review"); !strings.Contains(s.Note, "no service.sqlite") {
		t.Fatalf("store note = %q", s.Note)
	}
	// A store without the tables the digest reads is reported per step.
	db, err := sql.Open(store.DriverName, "file:"+filepath.ToSlash(filepath.Join(dir, "service.sqlite")))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := db.Exec(`CREATE TABLE metadata(singleton INTEGER PRIMARY KEY)`); err != nil {
		t.Fatal(err)
	}
	db.Close()
	d = Collect(context.Background(), dir, map[string]any{})
	if s := section(t, d, "routine review"); !strings.Contains(s.Note, "no such table") {
		t.Fatalf("old schema note = %+v", s)
	}
	if s := section(t, d, "unsuccessful plan stages"); !strings.Contains(s.Note, "no such table") {
		t.Fatalf("old schema note = %+v", s)
	}
}

func TestRevisionAgainstMain(t *testing.T) {
	// The test runs inside the repository; a revision that is HEAD's
	// parent or HEAD itself resolves either to "at main" or to landings.
	d := Collect(context.Background(), t.TempDir(), map[string]any{"installed_package": map[string]any{"source_revision": "HEAD"}})
	s := section(t, d, "revision")
	if !hasLine(s, "run binary built from HEAD", "installed_package.source_revision") {
		t.Fatalf("revision = %+v", s)
	}
	if len(s.Lines) < 2 && s.Note == "" {
		t.Fatalf("no comparison against main: %+v", s)
	}
}
