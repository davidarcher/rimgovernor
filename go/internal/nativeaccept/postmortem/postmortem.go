// Package postmortem builds the digest `acceptance why` prints for a failed
// case (#278): the manual sequence every diagnosis started with, read from
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
	// routine review, unsuccessful stages, job failures, authority
	// generations, pooled-job mismatches.
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
// the case directory) and row it came from ("service/stderr.log:1834",
// "service.sqlite transitions#412", "flight.jsonl.2:77").
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
	logs := serviceLogs(dir)
	d.Sections = append(d.Sections, revision(ctx, report))
	d.Sections = append(d.Sections, refusals(dir, logs))
	db, storeNote := openRaw(dir)
	if db != nil {
		defer db.Close()
	}
	d.Sections = append(d.Sections, routineReview(ctx, db, storeNote))
	d.Sections = append(d.Sections, extentEligibility(dir))
	d.Sections = append(d.Sections, unsuccessfulStages(ctx, db, storeNote))
	d.Sections = append(d.Sections, jobFailures(dir, logs))
	d.Sections = append(d.Sections, authorityGenerations(ctx, db, storeNote, logs, report))
	d.Sections = append(d.Sections, pooledJobs(logs))
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
// main is the cheapest diagnosis there is (#212).
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

// logLine is one service log line with where it was read.
type logLine struct {
	file string // relative to the case directory
	n    int
	text string
}

// serviceLogs reads every service*/stderr.log under dir, oldest launch
// first, as the digest's log corpus.
func serviceLogs(dir string) []logLine {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	var names []string
	for _, e := range entries {
		if e.IsDir() && (e.Name() == "service" || strings.HasPrefix(e.Name(), "service-") && e.Name() != "service-profile") {
			names = append(names, e.Name())
		}
	}
	sort.Slice(names, func(i, j int) bool { return launchIndex(names[i]) < launchIndex(names[j]) })
	var lines []logLine
	for _, name := range names {
		rel := filepath.ToSlash(filepath.Join(name, "stderr.log"))
		f, err := os.Open(filepath.Join(dir, rel))
		if err != nil {
			continue
		}
		scanner := bufio.NewScanner(f)
		scanner.Buffer(make([]byte, 1<<20), 1<<20)
		n := 0
		for scanner.Scan() {
			n++
			lines = append(lines, logLine{file: rel, n: n, text: scanner.Text()})
		}
		f.Close()
	}
	return lines
}

func launchIndex(name string) int {
	if name == "service" {
		return 1
	}
	i, _ := strconv.Atoi(strings.TrimPrefix(name, "service-"))
	return i
}

// refusals lists the last native refusals: service log lines carrying a
// refusal and flight-recorder native_error rows with refused text. The
// most recent come last, as in the log; distinct texts are counted so a
// refusal repeated every window shows once with its count.
func refusals(dir string, logs []logLine) Section {
	s := Section{Name: "native refusals (last first)"}
	s.Lines = refusedSteps(dir)
	type hit struct {
		text  string
		last  logLine
		count int
	}
	var hits []*hit
	index := map[string]*hit{}
	for _, l := range logs {
		if !isRefusal(l.text) {
			continue
		}
		key := normalize(l.text)
		h, ok := index[key]
		if !ok {
			h = &hit{text: strings.TrimSpace(l.text)}
			index[key] = h
			hits = append(hits, h)
		}
		h.count++
		h.last = l
	}
	sort.SliceStable(hits, func(i, j int) bool {
		return hits[i].last.n > hits[j].last.n || (hits[i].last.n == hits[j].last.n && hits[i].last.file > hits[j].last.file)
	})
	for i, h := range hits {
		if i >= maxLines {
			s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("... %d more distinct refusals", len(hits)-i), Evidence: "service*/stderr.log"})
			break
		}
		text := clip(h.text)
		if h.count > 1 {
			text = fmt.Sprintf("%s (x%d)", text, h.count)
		}
		s.Lines = append(s.Lines, Line{Text: text, Evidence: fmt.Sprintf("%s:%d", h.last.file, h.last.n)})
	}
	flightErrors := flightNativeErrors(dir)
	for i := len(flightErrors) - 1; i >= 0 && len(s.Lines) < 2*maxLines; i-- {
		s.Lines = append(s.Lines, flightErrors[i])
	}
	if len(s.Lines) == 0 {
		s.Note = "no refused step result, refusal in service*/stderr.log or native_error row in flight.jsonl*"
	}
	return s
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

// isRefusal matches a log line carrying a native refusal: the transport's
// "native read/write refused" wrapping, a FAILURE_CODE_ payload, or a
// non-empty refused=[...] list on a step or worker line (an empty list is
// the normal case and not a refusal).
func isRefusal(text string) bool {
	if strings.Contains(text, "read refused") || strings.Contains(text, "write refused") || strings.Contains(text, "FAILURE_CODE_") {
		return true
	}
	i := strings.Index(text, "refused=[")
	return i >= 0 && i+len("refused=[") < len(text) && text[i+len("refused=[")] != ']'
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

// flightNativeErrors scans the case's flight recording (retained segments
// oldest first, then the active file) for native_error rows and returns
// them in order with their file and line.
func flightNativeErrors(dir string) []Line {
	var lines []Line
	for _, file := range flightFiles(dir) {
		f, err := os.Open(filepath.Join(dir, file))
		if err != nil {
			continue
		}
		scanner := bufio.NewScanner(f)
		scanner.Buffer(make([]byte, 1<<20), 8<<20)
		n := 0
		for scanner.Scan() {
			n++
			line := scanner.Bytes()
			if !strings.Contains(string(line), `"kind":"native_error"`) {
				continue
			}
			var row struct {
				Sequence uint64 `json:"sequence"`
				Payload  struct {
					NativeTool  string `json:"native_tool"`
					Tool        string `json:"tool"`
					Error       string `json:"error"`
					RefusedText string `json:"refused_text"`
				} `json:"payload"`
			}
			if json.Unmarshal(line, &row) != nil {
				continue
			}
			tool := row.Payload.NativeTool
			if tool == "" {
				tool = row.Payload.Tool
			}
			text := row.Payload.Error
			if row.Payload.RefusedText != "" {
				text = row.Payload.RefusedText
			}
			lines = append(lines, Line{Text: fmt.Sprintf("native_error %s: %s", tool, clip(text)), Evidence: fmt.Sprintf("%s:%d seq %d", file, n, row.Sequence)})
		}
		f.Close()
	}
	return lines
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

// --- 3. routine review (raw store) -------------------------------------

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

// routineReview reads routine_review raw: development rows not selected
// (with the reason, which the goal's own status hides: a goal
// "deficit/active, zero methods" is usually a development refusal, not a
// planner that offered nothing) and every active goal in deficit with its
// live method count.
func routineReview(ctx context.Context, db *sql.DB, note string) Section {
	s := Section{Name: "routine review"}
	if db == nil {
		s.Note = note
		return s
	}
	// development is need -> refused (ranked and not selected).
	development := map[string]bool{}
	var payload []byte
	err := db.QueryRowContext(ctx, "SELECT payload FROM routine_review WHERE singleton=1").Scan(&payload)
	switch {
	case err == sql.ErrNoRows:
		s.Note = "routine_review is empty: the service never reviewed"
	case err != nil:
		s.Note = "routine_review: " + err.Error()
	default:
		var review struct {
			Revision    uint64
			Tick        int64
			Enabled     bool
			AsOf        map[string]int64
			Development struct {
				Tick      int64
				Capacity  int
				Committed []string
				Rows      []struct {
					Goal                string
					Score               float64
					Deficit             *float64
					Selected, Committed bool
					Reason              string
					Bottleneck          string
				}
			}
		}
		if err := json.Unmarshal(payload, &review); err != nil {
			s.Note = "routine_review payload: " + err.Error()
		} else {
			text := fmt.Sprintf("review revision %d at tick %d enabled=%t; development capacity %d committed=%v", review.Revision, review.Tick, review.Enabled, review.Development.Capacity, review.Development.Committed)
			// The census sections a review reads all describe one bundle's
			// tick today; a non-zero spread says the review mixed ticks (#354).
			if spread := asOfSpread(review.AsOf); spread != 0 {
				text += fmt.Sprintf(" as_of_spread=%d", spread)
			}
			s.Lines = append(s.Lines, Line{Text: text, Evidence: "service.sqlite routine_review"})
			for _, row := range review.Development.Rows {
				development[row.Goal] = !row.Selected
				if row.Selected {
					continue
				}
				deficit := "-"
				if row.Deficit != nil {
					deficit = strconv.FormatFloat(*row.Deficit, 'f', 2, 64)
				}
				text := fmt.Sprintf("%s not selected: %s (score %.2f deficit %s)", row.Goal, row.Reason, row.Score, deficit)
				if row.Bottleneck != "" {
					text += " bottleneck " + row.Bottleneck
				}
				if len(s.Lines) < maxLines {
					s.Lines = append(s.Lines, Line{Text: text, Evidence: "service.sqlite routine_review Development.Rows[" + row.Goal + "]"})
				}
			}
		}
	}
	// Goals in deficit that development selected (or never ranked) with no
	// live method: the "zero methods" symptom the rows above do not explain.
	rows, err := db.QueryContext(ctx, "SELECT g.id, g.payload, (SELECT count(*) FROM goal_methods m JOIN plans p ON p.id=m.plan_id WHERE m.goal_id=g.id AND p.retired=0) FROM goals g WHERE g.retired=0")
	if err != nil {
		s.Lines = append(s.Lines, Line{Text: "goals: " + err.Error(), Evidence: "service.sqlite goals"})
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
			Need     string
			Epoch    uint64
		}
		if json.Unmarshal(payload, &goal) != nil {
			continue
		}
		if goal.Status != "active" || goal.Need != "deficit" {
			continue
		}
		if refused, known := development[needOf(id)]; known && refused {
			continue
		}
		count++
		if len(s.Lines) >= 2*maxLines {
			continue
		}
		s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("goal %s deficit/active priority %d epoch %d: %d live methods", id, goal.Priority, goal.Epoch, methods), Evidence: "service.sqlite goals#" + id})
	}
	if count == 0 && s.Note == "" {
		s.Lines = append(s.Lines, Line{Text: "no selected goal in deficit", Evidence: "service.sqlite goals"})
	}
	return s
}

// asOfSpread is max - min over the review's per-section ticks, zero when
// the row carries none.
func asOfSpread(asOf map[string]int64) int64 {
	var min, max int64
	first := true
	for _, tick := range asOf {
		if first || tick < min {
			min = tick
		}
		if first || tick > max {
			max = tick
		}
		first = false
	}
	return max - min
}

// needOf is the routine need a goal id names: routine goal ids are
// "routine-<colony>-<Need>[-<subject>]", so the need is the third
// dash-separated part; any other id is returned whole.
func needOf(goalID string) string {
	parts := strings.SplitN(goalID, "-", 4)
	if len(parts) >= 3 && parts[0] == "routine" {
		return parts[2]
	}
	return goalID
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

// jobFailures greps the service logs and flight recording for the native
// JobFailReason a refused or abandoned pawn order carried (#189).
func jobFailures(dir string, logs []logLine) Section {
	s := Section{Name: "native job failures"}
	for _, l := range logs {
		if !strings.Contains(l.text, "JobFailReason") && !strings.Contains(l.text, "job failed") {
			continue
		}
		if len(s.Lines) >= maxLines {
			s.Lines = append(s.Lines, Line{Text: "... more", Evidence: "service*/stderr.log"})
			break
		}
		s.Lines = append(s.Lines, Line{Text: clip(l.text), Evidence: fmt.Sprintf("%s:%d", l.file, l.n)})
	}
	for _, file := range flightFiles(dir) {
		if len(s.Lines) >= 2*maxLines {
			break
		}
		f, err := os.Open(filepath.Join(dir, file))
		if err != nil {
			continue
		}
		scanner := bufio.NewScanner(f)
		scanner.Buffer(make([]byte, 1<<20), 8<<20)
		n := 0
		for scanner.Scan() {
			n++
			text := scanner.Text()
			i := strings.Index(text, "JobFailReason")
			if i < 0 {
				continue
			}
			start := i - 80
			if start < 0 {
				start = 0
			}
			end := i + 200
			if end > len(text) {
				end = len(text)
			}
			s.Lines = append(s.Lines, Line{Text: "..." + text[start:end] + "...", Evidence: fmt.Sprintf("%s:%d", file, n)})
			if len(s.Lines) >= 2*maxLines {
				break
			}
		}
		f.Close()
	}
	if len(s.Lines) == 0 {
		s.Note = "no JobFailReason in service*/stderr.log or flight.jsonl*"
	}
	return s
}

// --- 6. authority generations ------------------------------------------

// authorityGenerations reports how the native authority generation moved:
// each distinct Snapshot.Native across transitions in sequence order, the
// AuthorityChanged events and authority step failures in the log, and the
// report's own reacquisition count. A thrashing generation (#119, #213)
// invalidates every open attempt.
func authorityGenerations(ctx context.Context, db *sql.DB, note string, logs []logLine, report map[string]any) Section {
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
	changed, failed := 0, 0
	var lastChanged, lastFailed logLine
	for _, l := range logs {
		if strings.Contains(l.text, "AuthorityChanged") {
			changed++
			lastChanged = l
		}
		lower := strings.ToLower(l.text)
		if strings.Contains(lower, "authority changed") || strings.Contains(lower, "authority unavailable") || strings.Contains(lower, "authority generation") {
			failed++
			lastFailed = l
		}
	}
	if changed > 0 {
		s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("%d poll lines carried AuthorityChanged", changed), Evidence: fmt.Sprintf("%s:%d", lastChanged.file, lastChanged.n)})
	}
	if failed > 0 {
		s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("%d steps failed on authority; last: %s", failed, clip(lastFailed.text)), Evidence: fmt.Sprintf("%s:%d", lastFailed.file, lastFailed.n)})
	}
	if len(s.Lines) == 0 && s.Note == "" {
		s.Note = "no authority movement recorded"
	}
	return s
}

// --- 7. pooled-job mismatches ------------------------------------------

// pooledJobs lists "pawn order job mismatch" (and the attack/movement
// variants) worker errors: on a completed order these mean the native
// record read a pooled Job, a native-record bug, not a Go contract one
// (#108).
func pooledJobs(logs []logLine) Section {
	s := Section{Name: "pooled-job mismatches"}
	count := 0
	for _, l := range logs {
		if !strings.Contains(l.text, "job mismatch") {
			continue
		}
		count++
		if len(s.Lines) < maxLines {
			s.Lines = append(s.Lines, Line{Text: clip(l.text), Evidence: fmt.Sprintf("%s:%d", l.file, l.n)})
		}
	}
	if count > len(s.Lines) {
		s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("... %d more", count-len(s.Lines)), Evidence: "service*/stderr.log"})
	}
	if count == 0 {
		s.Note = "no job mismatch in service*/stderr.log"
	}
	return s
}

func asString(v any) string {
	s, _ := v.(string)
	return s
}
