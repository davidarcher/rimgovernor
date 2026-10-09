package postmortem

import (
	"bytes"
	"compress/gzip"
	"context"
	"database/sql"
	"encoding/base64"
	"encoding/json"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

// fixture writes a case output directory shaped like a failed serve-driven
// run: result.json, a flight recording with a rotated segment, and a
// service.sqlite holding only the tables the digest reads
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
	// The flight recording is the whole evidence: v2 rows (dispatch, authority,
	// planner_step) read
	// side by side. An empty refused list and a completed outcome are not
	// refusals.
	row := func(seq int, kind, payload string) string {
		return `{"version":2,"run":"r","sequence":` + strconv.Itoa(seq) + `,"wall_time":1,"kind":"` + kind + `","context":{},"payload":` + payload + "}\n"
	}
	const haulRefusal = `"native write refused: FAILURE_CODE_INVALID_REQUEST: JobFailReason: no empty place configured"`
	write("flight.jsonl.1",
		row(1, "native_request", `{}`)+
			row(2, "dispatch", `{"verdict":"refused","reason":"","target":"routine-haul-abc-0","dur_ms":0,"attrs":{"stage":"pending","refused":"no_empty_place","error":`+haulRefusal+`}}`)+
			row(3, "authority", `{"change":"changed","reason":"Manual","generation":4}`))
	write("flight.jsonl",
		row(4, "dispatch", `{"verdict":"failed","reason":"contract","target":"routine-haul-abc-1","dur_ms":0,"attrs":{"stage":"awaiting_observation","error":"bridge contract failure: pawn order job mismatch"}}`)+
			row(5, "planner_step", `{"verdict":"failed","reason":"control_lost","target":"rounds","dur_ms":1,"attrs":{"error":"control lost"}}`)+
			row(6, "authority", `{"change":"lost","reason":"manual","generation":4}`)+
			row(7, "native_call", `{"native_tool":"rimgovernor/orders_haul","error":"refused","refused_text":"the target is not a haulable item"}`)+
			row(8, "dispatch", `{"verdict":"refused","reason":"","target":"routine-haul-abc-0","dur_ms":0,"attrs":{"stage":"pending","refused":"no_empty_place","error":`+haulRefusal+`}}`)+
			row(9, "dispatch", `{"verdict":"completed","reason":"","target":"routine-haul-abc-2","dur_ms":0,"attrs":{"stage":"done"}}`))

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
	exec(`CREATE TABLE rounds(singleton INTEGER PRIMARY KEY, payload BLOB NOT NULL)`)
	exec(`CREATE TABLE standards(id TEXT PRIMARY KEY, revision TEXT NOT NULL, payload BLOB NOT NULL, retired INTEGER NOT NULL DEFAULT 0)`)
	exec(`CREATE TABLE plans(id TEXT PRIMARY KEY, revision TEXT NOT NULL, retired INTEGER NOT NULL DEFAULT 0)`)
	exec(`CREATE TABLE standard_methods(standard_id TEXT NOT NULL, episode TEXT NOT NULL, method_id TEXT NOT NULL, plan_id TEXT NOT NULL)`)
	exec(`CREATE TABLE transitions(sequence INTEGER PRIMARY KEY, action_id TEXT NOT NULL, payload BLOB NOT NULL)`)
	review := map[string]any{"Revision": 7, "Tick": 1200, "Enabled": true}
	data, _ := json.Marshal(review)
	exec(`INSERT INTO rounds VALUES(1,?)`, data)
	goal := func(id, status, need string) {
		payload, _ := json.Marshal(map[string]any{"ID": id, "Status": status, "Finding": need, "Priority": 2})
		exec(`INSERT INTO standards(id,revision,payload) VALUES(?,'0',?)`, id, payload)
	}
	goal("routine-c-EnsureComfort", "open", "unmet")
	goal("routine-c-EnsureFoodStorage", "open", "unmet")
	goal("routine-c-EnsureCooking", "settled", "met")
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
	want := []string{"revision", "stage graph", "native refusals (last first)", "rounds", "colony extent", "unsuccessful plan stages", "native job failures", "authority generations"}
	if strings.Join(names, ",") != strings.Join(want, ",") {
		t.Fatalf("sections = %v", names)
	}
	if s := section(t, d, "revision"); s.Note == "" {
		t.Fatalf("a report without a source revision should say so: %+v", s)
	}
	refusals := section(t, d, "native refusals (last first)")
	if !hasLine(refusals, "dispatch routine-haul-abc-0 refused [no_empty_place]: native write refused: FAILURE_CODE_INVALID_REQUEST: JobFailReason: no empty place configured (x2)", "flight.jsonl#8") {
		t.Fatalf("refusals = %+v", refusals)
	}
	if hasLine(refusals, "routine-haul-abc-2", "") || hasLine(refusals, "job mismatch", "") {
		t.Fatalf("a completed outcome or a failed (not refused) dispatch is not a refusal: %+v", refusals)
	}
	if !hasLine(refusals, "native_call error rimgovernor/orders_haul: the target is not a haulable item", "flight.jsonl#7") {
		t.Fatalf("flight native_call error missing: %+v", refusals)
	}
	review := section(t, d, "rounds")
	if !hasLine(review, "standard routine-c-EnsureComfort deficit/open priority 2 episode 0: 0 live methods", "standards#routine-c-EnsureComfort") {
		t.Fatalf("review = %+v", review)
	}
	if !hasLine(review, "review revision 7 at tick 1200 enabled=true", "rounds") {
		t.Fatalf("review line missing: %+v", review)
	}
	if !hasLine(review, "standard routine-c-EnsureFoodStorage deficit/open priority 2 episode 0: 0 live methods", "standards#routine-c-EnsureFoodStorage") {
		t.Fatalf("selected goal without methods missing: %+v", review)
	}
	if hasLine(review, "EnsureCooking", "") {
		t.Fatalf("a satisfied goal is not a zero-method finding: %+v", review)
	}
	stages := section(t, d, "unsuccessful plan stages")
	if !hasLine(stages, "a1 () unsuccessful at tick 40: native_failure", "transitions#3") || !hasLine(stages, "a2 () receipt refused at tick 41", "transitions#4") {
		t.Fatalf("stages = %+v", stages)
	}
	jobs := section(t, d, "native job failures")
	if !hasLine(jobs, "dispatch routine-haul-abc-0 refused at pending: native write refused: FAILURE_CODE_INVALID_REQUEST: JobFailReason: no empty place configured (x2)", "flight.jsonl#8") ||
		!hasLine(jobs, "dispatch routine-haul-abc-1 failed at awaiting_observation: bridge contract failure: pawn order job mismatch", "flight.jsonl#4") {
		t.Fatalf("jobs = %+v", jobs)
	}
	if hasLine(jobs, "routine-haul-abc-2", "") {
		t.Fatalf("a completed outcome is not a job failure: %+v", jobs)
	}
	authority := section(t, d, "authority generations")
	if !hasLine(authority, "generation 3 -> 4", "transitions#3") || !hasLine(authority, "generation 3 first, 4 last, 1 flips", "transitions#4") {
		t.Fatalf("authority = %+v", authority)
	}
	if !hasLine(authority, "1 authority rows changed; last generation 4 (Manual)", "flight.jsonl.1#3") ||
		!hasLine(authority, "1 authority rows lost; last generation 4 (manual)", "flight.jsonl#6") ||
		!hasLine(authority, "1 Rounder steps failed on lost control", "flight.jsonl#5") {
		t.Fatalf("authority rows = %+v", authority)
	}
	if !hasLine(authority, "authority_reacquisitions=map[attempts:2]", "result.json") {
		t.Fatalf("report reacquisitions = %+v", authority)
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
	if s := section(t, d, "rounds"); !strings.Contains(s.Note, "no service.sqlite") {
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
	if s := section(t, d, "rounds"); !strings.Contains(s.Note, "no such table") {
		t.Fatalf("old schema note = %+v", s)
	}
	if s := section(t, d, "unsuccessful plan stages"); !strings.Contains(s.Note, "no such table") {
		t.Fatalf("old schema note = %+v", s)
	}
}

func TestRevisionAgainstMain(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
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

// A native_call row answering with a failure payload is a refusal too:
// rows group by tool, code and detail with a count, the latest
// group first, and a success response stays out.
func TestRefusalsReportNativeResponseFailures(t *testing.T) {
	dir := t.TempDir()
	// Rows as the recorder writes them: the binary "proto" as
	// received, named by reply_type.
	row := func(seq int, tool string, reply proto.Message) string {
		data, err := proto.Marshal(reply)
		if err != nil {
			t.Fatal(err)
		}
		var buffer bytes.Buffer
		w := gzip.NewWriter(&buffer)
		w.Write(data)
		w.Close()
		line, _ := json.Marshal(map[string]any{"version": 1, "run": "r", "sequence": seq, "kind": "native_call", "context": map[string]any{}, "payload": map[string]any{
			"native_tool": tool,
			"reply_type":  string(reply.ProtoReflect().Descriptor().FullName()),
			"result":      map[string]any{"proto": base64.StdEncoding.EncodeToString(buffer.Bytes())},
		}})
		return string(line)
	}
	failed := func(code commonpb.FailureCode, detail string) proto.Message {
		return &lifecyclepb.IdentityReply{Outcome: &lifecyclepb.IdentityReply_Failure{Failure: &commonpb.Failure{Code: &code, Detail: &detail}}}
	}
	rows := []string{
		row(1, "rimgovernor/colony_facts", failed(commonpb.FailureCode_FAILURE_CODE_STALE_IDENTITY, "context changed")),
		row(2, "rimgovernor/colony_facts", &lifecyclepb.IdentityReply{}),
		row(3, "rimgovernor/orders_build", failed(commonpb.FailureCode_FAILURE_CODE_UNAVAILABLE, "blocked")),
		row(4, "rimgovernor/colony_facts", failed(commonpb.FailureCode_FAILURE_CODE_STALE_IDENTITY, "context changed")),
	}
	if err := os.WriteFile(filepath.Join(dir, "flight.jsonl"), []byte(strings.Join(rows, "\n")+"\n"), 0644); err != nil {
		t.Fatal(err)
	}
	s := refusals(dir, flightRows(dir))
	if len(s.Lines) != 2 {
		t.Fatalf("want two failure groups, got %+v", s)
	}
	if s.Lines[0] != (Line{Text: "native_call refused rimgovernor/colony_facts: FAILURE_CODE_STALE_IDENTITY: context changed (x2)", Evidence: "flight.jsonl#4"}) {
		t.Fatalf("latest group: %+v", s.Lines[0])
	}
	if s.Lines[1] != (Line{Text: "native_call refused rimgovernor/orders_build: FAILURE_CODE_UNAVAILABLE: blocked", Evidence: "flight.jsonl#3"}) {
		t.Fatalf("older group: %+v", s.Lines[1])
	}
}
