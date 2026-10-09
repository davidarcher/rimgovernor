// Package postmortem builds the digest `acceptance why` prints for a failed
// case: the manual sequence every diagnosis started with, read from
// the case's own evidence in a fixed order. Each line names the file and
// row it came from so the digest is checkable, not a verdict.
//
// The store is read raw (a run's service.sqlite may predate the current
// schema, which store.Open refuses); a missing table or file is reported
// as a line, never an error, so a bridge-only case still gets a digest.
package postmortem

import (
	"bufio"
	"context"
	"database/sql"
	"encoding/json"
	"fmt"
	"io"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/inputs"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/stepresult"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Digest is the ordered postmortem of one case output directory.
type Digest struct {
	// Case and Error come from result.json (or the live report).
	Case  string `json:"case,omitempty"`
	Error string `json:"error,omitempty"`
	// Sections are in the order the digest is read: revision, refusals,
	// rounds, unsuccessful stages, job failures, authority
	// generations.
	Sections []Section `json:"sections"`
}

// Section is one step of the sequence; Lines are its findings, each with
// the evidence it was read from. An empty Lines means the step found
// nothing and says so in Note.
type Section struct {
	Name  string `json:"name"`
	Note  string `json:"note,omitempty"`
	Lines []Line `json:"lines,omitempty"`
}

// Line is one finding: Text is the reading, Evidence the file (relative to
// the case directory) and row it came from ("flight.jsonl#1834",
// "service.sqlite transitions#412", "flight.jsonl.2#77"; a flight row's
// number is its sequence).
type Line struct {
	Text     string `json:"text"`
	Evidence string `json:"evidence"`
}

// maxLines bounds every section: the digest is the first thing read, not
// the whole log.
const maxLines = 12

// Collect reads dir's evidence and returns the digest. report is the
// case's result (result.json decoded, or the live Report); nil reads
// result.json from dir. Collect never fails on missing or malformed
// evidence: it records what it could not read as section notes.
func Collect(ctx context.Context, dir string, report map[string]any) Digest {
	if report == nil {
		report = map[string]any{}
		if data, err := os.ReadFile(filepath.Join(dir, "result.json")); err == nil {
			_ = json.Unmarshal(data, &report)
		}
	}
	d := Digest{Case: asString(report["case"]), Error: asString(report["error"])}
	rows := flightRows(dir)
	d.Sections = append(d.Sections, revision(ctx, report))
	d.Sections = append(d.Sections, stageGraph(report))
	d.Sections = append(d.Sections, refusals(dir, rows))
	db, storeNote := openRaw(dir)
	if db != nil {
		defer db.Close()
	}
	d.Sections = append(d.Sections, roundsSection(ctx, db, storeNote))
	d.Sections = append(d.Sections, extentEligibility(dir))
	d.Sections = append(d.Sections, unsuccessfulStages(ctx, db, storeNote))
	d.Sections = append(d.Sections, jobFailures(rows))
	d.Sections = append(d.Sections, authorityGenerations(ctx, db, storeNote, rows, report))
	return d
}

// Text renders the digest as the lines `acceptance why` prints.
func (d Digest) Text() string {
	var b strings.Builder
	if d.Case != "" {
		fmt.Fprintf(&b, "case: %s\n", d.Case)
	}
	if d.Error != "" {
		fmt.Fprintf(&b, "error: %s\n", d.Error)
	}
	for _, s := range d.Sections {
		fmt.Fprintf(&b, "## %s\n", s.Name)
		if s.Note != "" {
			fmt.Fprintf(&b, "  %s\n", s.Note)
		}
		for _, l := range s.Lines {
			fmt.Fprintf(&b, "  - %s  [%s]\n", l.Text, l.Evidence)
		}
	}
	return b.String()
}

// Write renders the digest to w.
func (d Digest) Write(w io.Writer) error {
	_, err := io.WriteString(w, d.Text())
	return err
}

// --- 1. revision -----------------------------------------------------

// revision compares the run binary's source revision (the installed
// package manifest on the report) with main: a failure already fixed on
// main is the cheapest diagnosis there is.
func revision(ctx context.Context, report map[string]any) Section {
	s := Section{Name: "revision"}
	pkg, _ := report["installed_package"].(map[string]any)
	rev := asString(pkg["source_revision"])
	if rev == "" {
		s.Note = "result.json has no installed_package.source_revision (bridge-only case or no manifest)"
		return s
	}
	dirty, _ := pkg["source_dirty"].(bool)
	text := "run binary built from " + short(rev)
	if dirty {
		text += " (dirty worktree)"
	}
	s.Lines = append(s.Lines, Line{Text: text, Evidence: "result.json installed_package.source_revision"})
	cwd, err := os.Getwd()
	if err != nil {
		return s
	}
	repo, ok := inputs.FindRepo(cwd)
	if !ok {
		s.Note = "no enclosing checkout: compare against main by hand"
		return s
	}
	gitCtx, cancel := context.WithTimeout(ctx, 20*time.Second)
	defer cancel()
	git := func(args ...string) (string, error) {
		cmd := exec.CommandContext(gitCtx, "git", append([]string{"-C", repo}, args...)...)
		out, err := cmd.Output()
		return strings.TrimSpace(string(out)), err
	}
	tip, err := git("rev-parse", "--short", "main")
	if err != nil {
		s.Note = "git could not resolve main: " + err.Error()
		return s
	}
	if _, err := git("cat-file", "-e", rev+"^{commit}"); err != nil {
		s.Lines = append(s.Lines, Line{Text: "revision is not in this checkout (a peer's worktree?); main is " + tip, Evidence: "git rev-parse main"})
		return s
	}
	if _, err := git("merge-base", "--is-ancestor", rev, "main"); err != nil {
		s.Lines = append(s.Lines, Line{Text: "revision is not an ancestor of main " + tip + " (unlanded branch build)", Evidence: "git merge-base --is-ancestor"})
		return s
	}
	log, err := git("log", "--oneline", rev+"..main")
	if err != nil {
		s.Note = "git log failed: " + err.Error()
		return s
	}
	if log == "" {
		s.Lines = append(s.Lines, Line{Text: "run binary is at main " + tip, Evidence: "git log " + short(rev) + "..main"})
		return s
	}
	landings := strings.Split(log, "\n")
	s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("%d landings on main since the run binary; check them before diagnosing", len(landings)), Evidence: "git log " + short(rev) + "..main"})
	for i, l := range landings {
		if i >= maxLines-1 {
			s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("... %d more", len(landings)-i), Evidence: "git log"})
			break
		}
		s.Lines = append(s.Lines, Line{Text: l, Evidence: "git log " + short(rev) + "..main"})
	}
	return s
}

func short(rev string) string {
	if len(rev) > 12 {
		return rev[:12]
	}
	return rev
}

// --- 2. refusals -------------------------------------------------------

// flightRow is one decoded flight row with the recording it came from. A
// row's evidence is "<file>#<sequence>": the sequence is the recorder's own,
// so it is the number `rimgovernor log` and `trace` print.
type flightRow struct {
	file string // relative to the case directory
	rec  bridge.TimelineRecord
}

func (r flightRow) evidence() string { return fmt.Sprintf("%s#%d", r.file, r.rec.Sequence) }

// fields is the row's data as one flat map (a decision row's attrs with its
// verdict, reason and target).
func (r flightRow) fields() map[string]any { return bridge.RowFields(r.rec) }

// flightRows decodes every flight recording under dir, oldest first (see
// flightFiles). A line that is not a row is skipped: the digest is read from
// whatever survived.
func flightRows(dir string) []flightRow {
	var rows []flightRow
	for _, file := range flightFiles(dir) {
		f, err := os.Open(filepath.Join(dir, file))
		if err != nil {
			continue
		}
		scanner := bufio.NewScanner(f)
		scanner.Buffer(make([]byte, 1<<20), 8<<20)
		for scanner.Scan() {
			if rec, ok := bridge.DecodeFlightLine(scanner.Bytes()); ok {
				rows = append(rows, flightRow{file: file, rec: rec})
			}
		}
		f.Close()
	}
	return rows
}

func launchIndex(name string) int {
	if name == "service" {
		return 1
	}
	i, _ := strconv.Atoi(strings.TrimPrefix(name, "service-"))
	return i
}

// refusals lists the last native refusals: refused step results, the
// Worker's refused dispatch rows, and native_call rows that carry an error
// or a failure code. Repeats collapse (distinct texts are counted) and the
// most recent come first, so a refusal repeated every window shows once with
// its count.
func refusals(dir string, rows []flightRow) Section {
	s := Section{Name: "native refusals (last first)"}
	s.Lines = refusedSteps(dir)
	for _, read := range []func(flightRow) (string, bool){dispatchRefusal, nativeCallRefusal} {
		for _, l := range groupRows(rows, read) {
			if len(s.Lines) >= 2*maxLines {
				break
			}
			s.Lines = append(s.Lines, l)
		}
	}
	if len(s.Lines) == 0 {
		s.Note = "no refused step result, refused dispatch row, or native_call error or failure in flight.jsonl*"
	}
	return s
}

// groupRows reads every row with read and returns the distinct texts, most
// recent first, each with its count and the evidence of its last row.
func groupRows(rows []flightRow, read func(flightRow) (string, bool)) []Line {
	type group struct {
		text     string
		evidence string
		last     int
		count    int
	}
	var groups []*group
	index := map[string]*group{}
	for i, row := range rows {
		text, ok := read(row)
		if !ok {
			continue
		}
		key := normalize(text)
		g, seen := index[key]
		if !seen {
			g = &group{text: text}
			index[key] = g
			groups = append(groups, g)
		}
		g.count++
		g.last = i
		g.evidence = row.evidence()
	}
	sort.SliceStable(groups, func(i, j int) bool { return groups[i].last > groups[j].last })
	lines := make([]Line, 0, len(groups))
	for _, g := range groups {
		text := clip(g.text)
		if g.count > 1 {
			text = fmt.Sprintf("%s (x%d)", text, g.count)
		}
		lines = append(lines, Line{Text: text, Evidence: g.evidence})
	}
	return lines
}

// isOutcomeKind reports whether kind is the Worker's per-action outcome row:
// the legacy worker_outcome or the v2 dispatch. (The per-run worker_dispatch
// tally is not an outcome.)
func isOutcomeKind(kind string) bool { return kind == "dispatch" }

// outcome is one outcome row read under either shape: the legacy payload
// (action, outcome, refused, err) or the v2 decision (target, verdict,
// reason, attrs.error, attrs.refused).
type outcome struct {
	action, verdict, reason, refused, err, stage string
}

func readOutcome(row flightRow) (outcome, bool) {
	if !isOutcomeKind(row.rec.Kind) {
		return outcome{}, false
	}
	f := row.fields()
	return outcome{
		action:  firstText(f, "action", "target"),
		verdict: firstText(f, "outcome", "verdict"),
		reason:  firstText(f, "reason"),
		refused: firstText(f, "refused"),
		err:     firstText(f, "err", "error"),
		stage:   firstText(f, "stage"),
	}, true
}

// firstText is the first of keys the row carries as text; a list renders
// comma-joined, and an empty list is no text.
func firstText(fields map[string]any, keys ...string) string {
	for _, key := range keys {
		switch v := fields[key].(type) {
		case string:
			if v != "" {
				return v
			}
		case []any:
			parts := make([]string, 0, len(v))
			for _, item := range v {
				if s, ok := item.(string); ok && s != "" {
					parts = append(parts, s)
				}
			}
			if len(parts) > 0 {
				return strings.Join(parts, ",")
			}
		case float64:
			return strconv.FormatFloat(v, 'f', -1, 64)
		case bool:
			return strconv.FormatBool(v)
		}
	}
	return ""
}

// dispatchRefusal reads a Worker outcome row the native side or the planner
// turned down: verdict refused, or a non-empty refused list.
func dispatchRefusal(row flightRow) (string, bool) {
	o, ok := readOutcome(row)
	if !ok || (o.verdict != "refused" && o.refused == "") {
		return "", false
	}
	text := "dispatch " + o.action + " refused"
	if o.refused != "" {
		text += " [" + o.refused + "]"
	}
	if o.reason != "" {
		text += ": " + o.reason
	}
	if o.err != "" {
		text += ": " + o.err
	}
	return text, true
}

// nativeCallRefusal reads a native_call row that failed: a transport or
// refusal error (the text the native side gave, when it gave one), or a
// reply carrying a {"failure":{"code":...}} payload, a refusal the native
// side answered rather than raised.
func nativeCallRefusal(row flightRow) (string, bool) {
	if !bridge.IsNativeReply(row.rec.Kind) {
		return "", false
	}
	payload := row.rec.Payload
	tool := asString(payload["native_tool"])
	if tool == "" {
		tool = asString(payload["tool"])
	}
	if errText := asString(payload["error"]); errText != "" {
		if refused := asString(payload["refused_text"]); refused != "" {
			errText = refused
		}
		return fmt.Sprintf("native_call error %s: %s", tool, clip(errText)), true
	}
	reply, ok := bridge.RecordedReply(payload)
	if !ok {
		return "", false
	}
	failure, _ := reply["failure"].(map[string]any)
	code, _ := failure["code"].(string)
	if code == "" {
		return "", false
	}
	text := fmt.Sprintf("native_call refused %s: %s", tool, code)
	if detail := asString(failure["detail"]); detail != "" {
		text += ": " + clip(detail)
	}
	return text, true
}

func refusedSteps(dir string) []Line {
	files, _ := filepath.Glob(filepath.Join(dir, "[0-9][0-9][0-9][0-9]*-*.json"))
	// Sequence numbers can grow beyond the four-digit minimum width.
	sequence := func(path string) int {
		n, _, _ := strings.Cut(filepath.Base(path), "-")
		v, _ := strconv.Atoi(n)
		return v
	}
	sort.Slice(files, func(i, j int) bool { return sequence(files[i]) > sequence(files[j]) })
	var lines []Line
	for _, file := range files {
		data, err := os.ReadFile(file)
		if err != nil {
			continue
		}
		var step struct {
			Request struct {
				Tool string `json:"tool"`
			} `json:"request"`
			Result json.RawMessage `json:"result"`
		}
		if json.Unmarshal(data, &step) != nil {
			continue
		}
		failure, ok := stepresult.Parse(step.Result)
		if !ok {
			continue
		}
		kind := failure.Kind
		if kind == "native exception" && strings.HasPrefix(step.Request.Tool, "test/") {
			kind = "fixture exception"
		}
		text := fmt.Sprintf("%s: %s: %s", kind, step.Request.Tool, failure.Summary)
		if kind == "blocking attention" {
			text += "\n" + failure.Detail
		}
		lines = append(lines, Line{Text: text, Evidence: filepath.Base(file) + " result"})
		if len(lines) == maxLines {
			break
		}
	}
	return lines
}

// normalize strips ids and numbers so repeats of one refusal collapse.
func normalize(text string) string {
	var b strings.Builder
	for _, r := range strings.TrimSpace(text) {
		if r >= '0' && r <= '9' {
			continue
		}
		b.WriteRune(r)
	}
	return b.String()
}

func clip(text string) string {
	text = strings.TrimSpace(text)
	if len(text) > 300 {
		return text[:300] + "..."
	}
	return text
}

// flightFiles lists every flight recording under dir (the case's own and
// each relaunch's service-N/flight.jsonl), each with its retained segments
// oldest first, relative to dir.
func flightFiles(dir string) []string {
	var out []string
	add := func(sub string) {
		base := filepath.Join(dir, sub)
		entries, err := os.ReadDir(base)
		if err != nil {
			return
		}
		type segment struct {
			index int
			name  string
		}
		var segments []segment
		active := false
		for _, e := range entries {
			name := e.Name()
			if name == "flight.jsonl" {
				active = true
				continue
			}
			if !strings.HasPrefix(name, "flight.jsonl.") {
				continue
			}
			if i, err := strconv.Atoi(strings.TrimPrefix(name, "flight.jsonl.")); err == nil {
				segments = append(segments, segment{i, name})
			}
		}
		sort.Slice(segments, func(i, j int) bool { return segments[i].index > segments[j].index })
		for _, s := range segments {
			out = append(out, filepath.ToSlash(filepath.Join(sub, s.name)))
		}
		if active {
			out = append(out, filepath.ToSlash(filepath.Join(sub, "flight.jsonl")))
		}
	}
	add(".")
	entries, _ := os.ReadDir(dir)
	var relaunches []string
	for _, e := range entries {
		if e.IsDir() && strings.HasPrefix(e.Name(), "service-") && e.Name() != "service-profile" {
			relaunches = append(relaunches, e.Name())
		}
	}
	sort.Slice(relaunches, func(i, j int) bool { return launchIndex(relaunches[i]) < launchIndex(relaunches[j]) })
	for _, r := range relaunches {
		add(r)
	}
	return out
}

// --- 3. rounds (raw store) -------------------------------------

// openRaw opens dir/service.sqlite read-only through the store's driver
// without the store's schema checks; the note explains a nil db.
func openRaw(dir string) (*sql.DB, string) {
	path := filepath.Join(dir, "service.sqlite")
	if _, err := os.Stat(path); err != nil {
		return nil, "no service.sqlite (bridge-only case or the service never started)"
	}
	abs, err := filepath.Abs(path)
	if err != nil {
		return nil, err.Error()
	}
	uriPath := filepath.ToSlash(abs)
	if !strings.HasPrefix(uriPath, "/") {
		uriPath = "/" + uriPath
	}
	u := &url.URL{Scheme: "file", Path: uriPath}
	q := u.Query()
	q.Set("mode", "ro")
	q.Set("_busy_timeout", "2000")
	u.RawQuery = q.Encode()
	db, err := sql.Open(store.DriverName, u.String())
	if err != nil {
		return nil, "open service.sqlite: " + err.Error()
	}
	db.SetMaxOpenConns(1)
	return db, ""
}

// roundsSection reads rounds raw: development rows not selected
// (with the reason, which the goal's own status hides: a goal
// "deficit/open, zero methods" is usually a development refusal, not a
// planner that offered nothing) and every active goal in deficit with its
// live method count.
func roundsSection(ctx context.Context, db *sql.DB, note string) Section {
	s := Section{Name: "rounds"}
	if db == nil {
		s.Note = note
		return s
	}
	// development is need -> refused (ranked and not selected).
	var payload []byte
	err := db.QueryRowContext(ctx, "SELECT payload FROM rounds WHERE singleton=1").Scan(&payload)
	switch {
	case err == sql.ErrNoRows:
		s.Note = "rounds is empty: the service never reviewed, or reset by a world rebuild"
	case err != nil:
		s.Note = "rounds: " + err.Error()
	default:
		var review struct {
			Revision uint64
			Tick     int64
			Enabled  bool
		}
		if err := json.Unmarshal(payload, &review); err != nil {
			s.Note = "rounds payload: " + err.Error()
		} else {
			text := fmt.Sprintf("review revision %d at tick %d enabled=%t", review.Revision, review.Tick, review.Enabled)
			s.Lines = append(s.Lines, Line{Text: text, Evidence: "service.sqlite rounds"})
		}
	}
	// Goals in deficit that development selected (or never ranked) with no
	// live method: the "zero methods" symptom the rows above do not explain.
	rows, err := db.QueryContext(ctx, "SELECT g.id, g.payload, (SELECT count(*) FROM standard_methods m JOIN plans p ON p.id=m.plan_id WHERE m.standard_id=g.id AND p.retired=0) FROM standards g WHERE g.retired=0")
	if err != nil {
		s.Lines = append(s.Lines, Line{Text: "standards: " + err.Error(), Evidence: "service.sqlite standards"})
		return s
	}
	defer rows.Close()
	count := 0
	for rows.Next() {
		var id string
		var payload []byte
		var methods int
		if rows.Scan(&id, &payload, &methods) != nil {
			continue
		}
		var goal struct {
			Source   string
			Priority int
			Status   string
			Finding  string
			Episode  uint64
		}
		if json.Unmarshal(payload, &goal) != nil {
			continue
		}
		if goal.Status != "open" || goal.Finding != "unmet" {
			continue
		}
		count++
		if len(s.Lines) >= 2*maxLines {
			continue
		}
		s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("standard %s deficit/open priority %d episode %d: %d live methods", id, goal.Priority, goal.Episode, methods), Evidence: "service.sqlite standards#" + id})
	}
	rows.Close()
	// Projects are their own rows: an open Project in deficit with
	// no live method is the same symptom.
	projects, err := db.QueryContext(ctx, "SELECT p.id, p.payload, (SELECT count(*) FROM project_methods m JOIN plans pl ON pl.id=m.plan_id WHERE m.project_id=p.id AND pl.retired=0) FROM projects p WHERE p.retired=0")
	if err != nil {
		s.Lines = append(s.Lines, Line{Text: "projects: " + err.Error(), Evidence: "service.sqlite projects"})
		return s
	}
	defer projects.Close()
	for projects.Next() {
		var id string
		var payload []byte
		var methods int
		if projects.Scan(&id, &payload, &methods) != nil {
			continue
		}
		var project struct {
			Kind     string
			Priority int
			Status   string
			Finding  string
		}
		if json.Unmarshal(payload, &project) != nil || project.Status != "open" || project.Finding != "unmet" {
			continue
		}
		count++
		if len(s.Lines) >= 2*maxLines {
			continue
		}
		s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("project %s deficit/open priority %d: %d live methods", id, project.Priority, methods), Evidence: "service.sqlite projects#" + id})
	}
	if count == 0 && s.Note == "" {
		s.Lines = append(s.Lines, Line{Text: "no selected standard in deficit", Evidence: "service.sqlite standards"})
	}
	return s
}

// --- 4. unsuccessful stages (raw store) --------------------------------

// unsuccessfulStages lists every observe transition that ended an attempt
// unsuccessful, with the action's kind and the native reason.
func unsuccessfulStages(ctx context.Context, db *sql.DB, note string) Section {
	s := Section{Name: "unsuccessful plan stages"}
	if db == nil {
		s.Note = note
		return s
	}
	rows, err := db.QueryContext(ctx, "SELECT t.sequence, t.action_id, t.payload, COALESCE(a.kind,''), COALESCE(a.definition,''), COALESCE(a.pawn,'') FROM transitions t LEFT JOIN actions a ON a.id=t.action_id ORDER BY t.sequence")
	if err != nil {
		// An older schema without the actions columns still has the
		// transitions; the action's kind is then left blank.
		rows, err = db.QueryContext(ctx, "SELECT sequence, action_id, payload, '', '', '' FROM transitions ORDER BY sequence")
	}
	if err != nil {
		s.Note = "transitions: " + err.Error()
		return s
	}
	defer rows.Close()
	total := 0
	for rows.Next() {
		var sequence int64
		var action, kind, definition, pawn string
		var payload []byte
		if rows.Scan(&sequence, &action, &payload, &kind, &definition, &pawn) != nil {
			continue
		}
		var event struct {
			Kind        string
			Tick        int64
			Receipt     string
			Observation struct {
				Effect             string
				UnsuccessfulReason string
			}
		}
		if json.Unmarshal(payload, &event) != nil {
			continue
		}
		unsuccessful := event.Observation.Effect == "unsuccessful"
		refused := event.Kind == "receipt" && (event.Receipt == "refused" || event.Receipt == "unsent")
		if !unsuccessful && !refused {
			continue
		}
		total++
		if len(s.Lines) >= maxLines {
			continue
		}
		what := kind
		if definition != "" {
			what += " " + definition
		}
		if pawn != "" {
			what += " by " + pawn
		}
		var text string
		if unsuccessful {
			text = fmt.Sprintf("%s (%s) unsuccessful at tick %d: %s", action, what, event.Tick, event.Observation.UnsuccessfulReason)
		} else {
			text = fmt.Sprintf("%s (%s) receipt %s at tick %d", action, what, event.Receipt, event.Tick)
		}
		s.Lines = append(s.Lines, Line{Text: text, Evidence: fmt.Sprintf("service.sqlite transitions#%d", sequence)})
	}
	if total > len(s.Lines) {
		s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("... %d more", total-len(s.Lines)), Evidence: "service.sqlite transitions"})
	}
	if total == 0 {
		s.Note = "no unsuccessful observation or refused receipt in transitions"
	}
	return s
}

// --- 5. job failures ---------------------------------------------------

// jobFailures lists the Worker outcome rows that failed (verdict failed, or
// an error text): the native refusal or contract failure a pawn order
// carried, and the pooled-job mismatch a completed order raised when
// the native record read a pooled Job, grouped by text with a count.
func jobFailures(rows []flightRow) Section {
	s := Section{Name: "native job failures"}
	lines := groupRows(rows, func(row flightRow) (string, bool) {
		o, ok := readOutcome(row)
		if !ok || (o.verdict != "failed" && o.err == "") {
			return "", false
		}
		text := "dispatch " + o.action + " " + o.verdict
		if o.stage != "" {
			text += " at " + o.stage
		}
		if o.err != "" {
			text += ": " + o.err
		}
		return text, true
	})
	for i, l := range lines {
		if i >= maxLines {
			s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("... %d more distinct failures", len(lines)-i), Evidence: "flight.jsonl*"})
			break
		}
		s.Lines = append(s.Lines, l)
	}
	if len(s.Lines) == 0 {
		s.Note = "no failed dispatch row in flight.jsonl*"
	}
	return s
}

// --- 6. authority generations ------------------------------------------

// authorityMovement reports how an authority row moved the grant: "changed"
// (an AuthorityChanged event), "lost" or "retaken", from the authority row's
// change field.
func authorityMovement(row flightRow) (string, bool) {
	switch {
	case row.rec.Kind == "authority":
		if change := firstText(row.fields(), "change"); change != "" {
			return change, true
		}
		return "changed", true
	}
	return "", false
}

// authorityGenerations reports how the native authority generation moved:
// each distinct Snapshot.Native across transitions in sequence order, the
// authority rows (changed, lost, retaken) and the Rounder steps that failed
// on a lost control, and the report's own reacquisition count. A thrashing
// generation invalidates every open attempt.
func authorityGenerations(ctx context.Context, db *sql.DB, note string, rows []flightRow, report map[string]any) Section {
	s := Section{Name: "authority generations"}
	if v, ok := report["authority_reacquisitions"]; ok {
		s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("report authority_reacquisitions=%v", v), Evidence: "result.json authority_reacquisitions"})
	}
	if db != nil {
		rows, err := db.QueryContext(ctx, "SELECT sequence, payload FROM transitions ORDER BY sequence")
		if err != nil {
			s.Lines = append(s.Lines, Line{Text: "transitions: " + err.Error(), Evidence: "service.sqlite transitions"})
		} else {
			var last int64 = -1
			flips := 0
			var first, lastSeq int64
			for rows.Next() {
				var sequence int64
				var payload []byte
				if rows.Scan(&sequence, &payload) != nil {
					continue
				}
				var event struct {
					Snapshot struct{ Native int64 }
				}
				if json.Unmarshal(payload, &event) != nil || event.Snapshot.Native <= 0 {
					// A transition without a native snapshot (a hold, a
					// cancel) says nothing about the generation.
					continue
				}
				lastSeq = sequence
				if event.Snapshot.Native == last {
					continue
				}
				if last >= 0 {
					flips++
					if flips <= maxLines/2 {
						s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("generation %d -> %d", last, event.Snapshot.Native), Evidence: fmt.Sprintf("service.sqlite transitions#%d", sequence)})
					}
				} else {
					first = event.Snapshot.Native
				}
				last = event.Snapshot.Native
			}
			rows.Close()
			if last >= 0 {
				s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("generation %d first, %d last, %d flips across transitions", first, last, flips), Evidence: fmt.Sprintf("service.sqlite transitions#%d", lastSeq)})
			}
		}
	} else if note != "" {
		s.Note = note
	}
	counts := map[string]int{}
	last := map[string]flightRow{}
	controlLost := 0
	var lastControlLost flightRow
	for _, row := range rows {
		if change, ok := authorityMovement(row); ok {
			counts[change]++
			last[change] = row
		}
		if row.rec.Kind == "planner_step" && firstText(row.fields(), "reason") == "control_lost" {
			controlLost++
			lastControlLost = row
		}
	}
	for _, change := range []string{"changed", "lost", "retaken"} {
		if counts[change] == 0 {
			continue
		}
		f := last[change].fields()
		text := fmt.Sprintf("%d authority rows %s; last", counts[change], change)
		if g := firstText(f, "generation"); g != "" {
			text += " generation " + g
		}
		if r := firstText(f, "reason"); r != "" {
			text += " (" + r + ")"
		}
		s.Lines = append(s.Lines, Line{Text: text, Evidence: last[change].evidence()})
	}
	if controlLost > 0 {
		s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("%d Rounder steps failed on lost control", controlLost), Evidence: lastControlLost.evidence()})
	}
	if len(s.Lines) == 0 && s.Note == "" {
		s.Note = "no authority movement recorded"
	}
	return s
}

func asString(v any) string {
	s, _ := v.(string)
	return s
}
