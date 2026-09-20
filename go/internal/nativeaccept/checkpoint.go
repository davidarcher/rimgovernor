package nativeaccept

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// A checkpoint bundle (issue #249) is one directory holding everything a
// later run needs to resume a case where this one was: the game save
// (CheckpointSaveName + ".rws"), the service's durable state
// (CheckpointStoreFile, serve-driven cases only), the native clock journal
// (CheckpointJournalDir) and the CheckpointSidecar describing them. A save
// alone is not the state: rewinding the world without the store and
// journal is the mismatch behind #119's authority thrash.
const (
	// DefaultCheckpointEvery is the ring's cadence in run phase.
	DefaultCheckpointEvery = time.Minute
	// CheckpointKeep is how many periodic entries a ring retains.
	CheckpointKeep = 5
	// CheckpointSidecar is the bundle's description.
	CheckpointSidecar = "checkpoint.json"
	// CheckpointStoreFile is the bundle's copy of the service's SQLite state.
	CheckpointStoreFile = "service.sqlite"
	// CheckpointJournalDir is the bundle's copy of the clock journal.
	CheckpointJournalDir = "journal"
	// CheckpointRingFile is the ring's index (Ring), beside its bundles.
	CheckpointRingFile = "ring.json"
	// FailedCheckpoint is the label of the bundle taken as a failed run
	// ends, after the periodic ring; BreakCheckpoint (breakpoint.go) is
	// its counterpart for a run paused at a breakpoint.
	FailedCheckpoint = "failed"
)

// Checkpoint is one bundle's sidecar: what it holds and what it was taken
// under. Fingerprint fields (Package, Start, Expansions) decide whether a
// later run may resume from it.
type Checkpoint struct {
	Case string `json:"case"`
	// Label names the bundle's directory: "t+7m" for the ring, "failed",
	// or a phase name a case declared.
	Label string `json:"label"`
	// OffsetMs is the run-phase offset the bundle was taken at, counted
	// from the case's original start across resumes (the label's t+...).
	OffsetMs int64  `json:"offset_ms"`
	Tick     uint64 `json:"tick"`
	// Save is the .rws name; Store and Journal say whether the bundle
	// carries those parts.
	Save     string         `json:"save"`
	Store    bool           `json:"store"`
	Journal  bool           `json:"journal"`
	Identity map[string]any `json:"identity,omitempty"`
	// Prepared is the fixture op's reply the resumed session reports as
	// its own, since the op does not run again over the restored world.
	Prepared map[string]any `json:"prepared,omitempty"`
	// State is what the case itself recorded about its progress up to the
	// capture (SetCheckpointState): the fixture prep it ran in its Run
	// body, the coordinates it chose. A resumed run reads it back through
	// Session.Resumed and skips the work the save already carries (#316).
	State map[string]any `json:"state,omitempty"`
	// Serve is the case's declared serve spec at capture, for the record.
	Serve map[string]any `json:"serve,omitempty"`
	// ServiceProfile is the service profile directory the store is bound
	// to (its clock inbox binds to the path); a resumed run's services keep
	// using it, since the store refuses any other.
	ServiceProfile string `json:"service_profile,omitempty"`
	SourceRevision string `json:"source_revision"`
	// Package hashes the installed native package's files (PackageFiles).
	Package string `json:"package"`
	// Start hashes the case's Start (its kind, save, fixture op and args).
	Start      string   `json:"start"`
	Expansions []string `json:"expansions"`
	// Stage and StageKey mark a staged run-phase bundle (#329): the stage
	// the case declared and the hash of the staging code it was taken
	// under (a change to the case's area package invalidates it).
	Stage    string `json:"stage,omitempty"`
	StageKey string `json:"stage_key,omitempty"`
	// Through says which path took the save: "bridge" (lifecycle_save on
	// the harness session) or "service" (/api/lifecycle/save).
	Through string `json:"through"`
	At      string `json:"at"`
	WallMs  int64  `json:"wall_ms"`
	// Path is the bundle directory, set on read and capture, not stored.
	Path string `json:"-"`
}

// Fingerprint is what a checkpoint must agree with the current tree on
// before a run resumes from it: anything that touches world state. A
// Go-only change to the governor keeps the bundle.
type Fingerprint struct {
	Package    string
	Start      string
	Expansions []string
}

// Mismatch says why the sidecar cannot be resumed under f, or "".
func (f Fingerprint) Mismatch(c Checkpoint) string {
	switch {
	case c.Package != f.Package:
		return "the installed native package changed"
	case c.Start != f.Start:
		return "the case's Start changed"
	case strings.Join(c.Expansions, ",") != strings.Join(f.Expansions, ","):
		return "the profile's expansions changed"
	}
	return ""
}

// StartHash is Fingerprint.Start over a Start's description (Describe
// output: kind, save name, fixture op and args, nested start).
func StartHash(described map[string]any) string {
	data, _ := json.Marshal(described)
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:])
}

// PackageHash is Fingerprint.Package over the package installed under the
// game at installation (PackageFiles), "" when there is none.
func PackageHash(installation string) string {
	files, err := PackageFiles(installation)
	if err != nil || len(files) == 0 {
		return ""
	}
	names := make([]string, 0, len(files))
	for name := range files {
		names = append(names, name)
	}
	sort.Strings(names)
	h := sha256.New()
	for _, name := range names {
		fmt.Fprintf(h, "%s=%s\n", name, files[name])
	}
	return hex.EncodeToString(h.Sum(nil))
}

// InstalledGameDir is the game installation configDir's config.json
// launches (its workingDir, whose Mods/RimGovernor is the native package),
// or an error when the root has never been prepared.
func InstalledGameDir(configDir string) (string, error) {
	config, err := loadConfig(filepath.Join(mustAbs(configDir), "config.json"))
	if err != nil {
		return "", err
	}
	game, err := gameSection(config)
	if err != nil {
		return "", err
	}
	workingDir, _ := game["workingDir"].(string)
	if workingDir == "" {
		return "", fmt.Errorf("%s: the game has no workingDir", filepath.Join(configDir, "config.json"))
	}
	return workingDir, nil
}

// Ring is the index a case's checkpoint directory keeps (CheckpointRingFile):
// the bundles present, the last run's outcome and which entry the next run
// resumes from.
type Ring struct {
	Case string `json:"case"`
	// Entries are the bundles by label, oldest first by offset.
	Entries []Checkpoint `json:"entries"`
	// Failed is the last failed run's final bundle, when one was taken.
	Failed *Checkpoint `json:"failed,omitempty"`
	// FailedTick and FailedOffsetMs are where the last run failed;
	// FailedTick is 0 when the failure could not be observed.
	FailedTick     uint64 `json:"failed_tick"`
	FailedOffsetMs int64  `json:"failed_offset_ms"`
	// ResumedFrom is the label the failed run itself resumed from, "" for a
	// run from scratch.
	ResumedFrom string `json:"resumed_from,omitempty"`
	// Next is the label the next run resumes from; "" means fresh.
	Next string `json:"next"`
	// Note is printed by the next run before its resume line: the
	// automatic rewind's reason.
	Note string `json:"note,omitempty"`
	// SourceRevision is the failed run's.
	SourceRevision string `json:"source_revision"`
	// FailedOutput is the failed run's output directory (its result.json
	// holds the timeline a postmortem-only rerun reads back, #275).
	FailedOutput string `json:"failed_output,omitempty"`
	// Break is set when the last run paused at a breakpoint (#280): Next
	// then names its BreakCheckpoint bundle, which `acceptance resume`
	// continues from and `acceptance stop` discards.
	Break *BreakRecord `json:"break,omitempty"`
}

// ReadCheckpoint reads the sidecar of the bundle at dir, with Path set.
func ReadCheckpoint(dir string) (Checkpoint, error) {
	data, err := os.ReadFile(filepath.Join(dir, CheckpointSidecar))
	if err != nil {
		return Checkpoint{}, err
	}
	var c Checkpoint
	if err := json.Unmarshal(data, &c); err != nil {
		return Checkpoint{}, fmt.Errorf("%s: %w", filepath.Join(dir, CheckpointSidecar), err)
	}
	c.Path = dir
	return c, nil
}

// ReadRing reads dir's index; a missing ring is (nil, nil).
func ReadRing(dir string) (*Ring, error) {
	data, err := os.ReadFile(filepath.Join(dir, CheckpointRingFile))
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	var r Ring
	if err := json.Unmarshal(data, &r); err != nil {
		return nil, fmt.Errorf("%s: %w", CheckpointRingFile, err)
	}
	for i := range r.Entries {
		r.Entries[i].Path = filepath.Join(dir, r.Entries[i].Label)
	}
	if r.Failed != nil {
		r.Failed.Path = filepath.Join(dir, r.Failed.Label)
	}
	return &r, nil
}

// Write persists the index under dir.
func (r *Ring) Write(dir string) error {
	if err := os.MkdirAll(dir, 0755); err != nil {
		return err
	}
	data, err := json.MarshalIndent(r, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(filepath.Join(dir, CheckpointRingFile), data, 0644)
}

// Entry returns the entry labelled label.
func (r *Ring) Entry(label string) (Checkpoint, bool) {
	for _, e := range r.Entries {
		if e.Label == label {
			return e, true
		}
	}
	return Checkpoint{}, false
}

// Before returns the entry preceding label, or false at the ring's start.
func (r *Ring) Before(label string) (Checkpoint, bool) {
	for i, e := range r.Entries {
		if e.Label == label {
			if i == 0 {
				return Checkpoint{}, false
			}
			return r.Entries[i-1], true
		}
	}
	return Checkpoint{}, false
}

// Rewind steps Next back n entries; past the ring's start the result is
// "" (fresh).
func (r *Ring) Rewind(n int) string {
	label := r.Next
	for ; n > 0 && label != ""; n-- {
		prev, ok := r.Before(label)
		if !ok {
			return ""
		}
		label = prev.Label
	}
	return label
}

// Plan decides what the run that just ended leaves for the next one (#249):
// a passing run clears the ring; a failing run resumes from its last
// entry, unless it was itself a resumed run that failed at the tick the
// previous attempt failed at, in which case the next run steps back one
// entry ("no progress") and past the ring starts fresh. previous is the
// index before this run (nil when there was none).
func (r *Ring) Plan(previous *Ring, resumedFrom string, failedTick uint64) {
	r.ResumedFrom = resumedFrom
	r.Note = ""
	if len(r.Entries) == 0 {
		r.Next = ""
		return
	}
	last := r.Entries[len(r.Entries)-1]
	r.Next = last.Label
	if previous == nil || resumedFrom == "" || failedTick == 0 || previous.FailedTick != failedTick {
		return
	}
	prev, ok := r.Before(resumedFrom)
	if !ok {
		r.Next = ""
		r.Note = fmt.Sprintf("no progress since %s and nothing earlier in the ring; starting fresh", resumedFrom)
		return
	}
	r.Next = prev.Label
	r.Note = fmt.Sprintf("no progress since %s; rewinding to %s", resumedFrom, prev.Label)
}

// CheckpointRing takes bundles for one run: every Every of run phase at
// the next natural pause (a bridge-only case's next native call or wait
// probe once the game is paused; a serve-driven case's next wait probe,
// through the service's pause/save/resume), plus named phase bundles and
// the final failed bundle, into Dir/<label>/. The runner installs it
// (Activate) for the case's Run and reads its Entries afterwards.
type CheckpointRing struct {
	Dir   string
	Case  string
	Every time.Duration
	Keep  int
	// Base is the run-phase offset the run resumed at (0 from scratch).
	Base time.Duration
	// Config is the run's; the save lands in its game profile.
	Config *Config
	// Bridge returns the harness holding the GABP slot, nil while a service
	// holds it; Service returns the running service holding it, nil
	// otherwise. Both are consulted at every capture.
	Bridge  func() *Harness
	Service func() *ServiceProcess
	// StorePath is the case's service.sqlite, copied when it exists.
	StorePath string
	// Output is the run's output directory, recorded on a failing run's
	// index (Ring.FailedOutput).
	Output string
	// Fingerprint, Serve, Prepared and SourceRevision fill the sidecar.
	Fingerprint    Fingerprint
	Serve          map[string]any
	Prepared       map[string]any
	SourceRevision string
	// State is the case's own progress record, carried into every capture
	// (SetCheckpointState adds to it; a resumed run starts from the
	// entry's).
	State map[string]any
	// StageKey, when set, marks every capture a stage bundle (#329) of the
	// stage its label names, under this staging-code hash.
	StageKey string
	// Prior are the entries of the timeline this run resumed into (the
	// previous ring's up to the resume point); the provisional index a
	// capture writes lists them before this run's own.
	Prior []Checkpoint
	// Saver, when set, replaces the bridge and service save paths: it
	// leaves the game saved as name and returns the save's path, tick and
	// identity (tests only).
	Saver func(ctx context.Context, name, label string, force bool) (path string, tick uint64, identity map[string]any, err error)
	// Break is the run's breakpoint (#280), zero when none; OnBreak runs
	// once when it trips (Trip), with the reason.
	Break   Breakpoint
	OnBreak func(reason string)

	mu        sync.Mutex
	started   time.Time
	last      time.Time
	nextCheck time.Time
	busy      bool
	entries   []Checkpoint
	failed    *Checkpoint
	errs      []string
	// capped, once set, is why no further entry is taken (CapCheckpoints):
	// the case passed a point its body cannot resume after.
	capped string
	// tripped is why the breakpoint fired; nextBreakTick throttles a tick
	// breakpoint's reads under a service.
	tripped       string
	nextBreakTick time.Time
	breakReading  bool
}

// activeRing is the ring the hooks (Harness.Call, WaitProgress, Watch)
// consult: one case runs per process at a time.
var activeRing atomic.Pointer[CheckpointRing]

// Activate starts the ring's run phase and installs it for the hooks.
func (r *CheckpointRing) Activate() {
	r.mu.Lock()
	r.started = time.Now()
	r.last = r.started
	r.mu.Unlock()
	if r.Keep <= 0 {
		r.Keep = CheckpointKeep
	}
	activeRing.Store(r)
}

// Deactivate uninstalls the ring; periodic captures stop.
func (r *CheckpointRing) Deactivate() {
	activeRing.CompareAndSwap(r, nil)
}

// Entries are the bundles this run took, oldest first, with the failed
// bundle last when one was taken.
func (r *CheckpointRing) Entries() []Checkpoint {
	r.mu.Lock()
	defer r.mu.Unlock()
	out := append([]Checkpoint(nil), r.entries...)
	if r.failed != nil {
		out = append(out, *r.failed)
	}
	return out
}

// Capped is why the ring stopped taking entries, "" while it still does.
func (r *CheckpointRing) Capped() string {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.capped
}

// Errors are the captures that failed, for the report.
func (r *CheckpointRing) Errors() []string {
	r.mu.Lock()
	defer r.mu.Unlock()
	return append([]string(nil), r.errs...)
}

// Offset is the current run-phase offset: Base plus the time since
// Activate.
func (r *CheckpointRing) Offset() time.Duration {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.offset()
}

func (r *CheckpointRing) offset() time.Duration {
	if r.started.IsZero() {
		return r.Base
	}
	return r.Base + time.Since(r.started)
}

// OffsetLabel is a bundle's label for a run-phase offset: "t+7m" for
// whole minutes, "t+1m30s" or "t+45s" otherwise. An offset that is not a
// whole second (a sub-second cadence, which rounds its offsets to the
// cadence) keeps its milliseconds, so two captures a few milliseconds
// apart never share a label (#596).
func OffsetLabel(d time.Duration) string {
	if d%time.Second != 0 {
		return "t+" + d.String()
	}
	h, m, sec := d/time.Hour, (d%time.Hour)/time.Minute, (d%time.Minute)/time.Second
	var b strings.Builder
	b.WriteString("t+")
	if h > 0 {
		fmt.Fprintf(&b, "%dh", h)
	}
	if m > 0 {
		fmt.Fprintf(&b, "%dm", m)
	}
	if sec > 0 || (h == 0 && m == 0) {
		fmt.Fprintf(&b, "%ds", sec)
	}
	return b.String()
}

// checkpointPause is the hook the natural-pause sites call: it captures a
// ring entry when one is due and returns how long that took, so a wait can
// leave it out of its stall accounting.
func checkpointPause(ctx context.Context) time.Duration {
	r := activeRing.Load()
	if r == nil || r.Every <= 0 {
		return 0
	}
	return r.maybe(ctx)
}

// CheckpointPause is checkpointPause for a case's own poll loop (a
// sustainedfood watch): a natural pause the ring may capture at.
func CheckpointPause(ctx context.Context) time.Duration { return checkpointPause(ctx) }

// CaptureCheckpoint takes a named bundle on the active ring (a phase
// boundary the case declared: a goal admitted, a stage advanced); ok is
// false when no ring is active.
func CaptureCheckpoint(ctx context.Context, label string) (c Checkpoint, ok bool, err error) {
	r := activeRing.Load()
	if r == nil {
		return Checkpoint{}, false, nil
	}
	c, err = r.Capture(ctx, label)
	return c, true, err
}

// SetCheckpointState records key on the active ring's case state, carried
// by every later capture (a phase the Run body completed, the fixture
// coordinates it chose); a nil value deletes the key. Nothing happens
// when no ring is active.
func SetCheckpointState(key string, value any) {
	r := activeRing.Load()
	if r == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.State == nil {
		r.State = map[string]any{}
	}
	if value == nil {
		delete(r.State, key)
		return
	}
	r.State[key] = value
}

// CapCheckpoints stops the active ring taking any further entry, periodic
// or named, so a resume of a later failure replays from the last entry
// before the cap: a case calls it at a point of no return its Run body
// cannot resume after (defense/layout at its raid, whose sprung traps fail
// the pre-raid audit a resume replays; #330). The failed bundle is still
// taken. reason goes on the report. Nothing happens when no ring is active.
func CapCheckpoints(reason string) {
	r := activeRing.Load()
	if r == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.capped == "" {
		r.capped = reason
	}
}

// errNotPaused is a periodic capture declined because the game runs; the
// ring checks again shortly.
var errNotPaused = errors.New("game is not paused")

// errNoHolder is a capture attempted while no side holds the game (a
// service stopped and the harness not yet reattached); periodic captures
// back off like errNotPaused, a forced one reports it.
var errNoHolder = errors.New("neither the harness nor a service holds the game")

func (r *CheckpointRing) maybe(ctx context.Context) time.Duration {
	// A breakpoint is checked at every natural pause, on its own clock.
	r.checkBreak(ctx)
	r.mu.Lock()
	now := time.Now()
	if r.capped != "" || r.busy || now.Sub(r.last) < r.Every || now.Before(r.nextCheck) {
		r.mu.Unlock()
		return 0
	}
	r.busy = true
	// The label rounds to the cadence so consecutive captures never share
	// one: 61 seconds in at a one-minute cadence is t+1m.
	label := OffsetLabel(r.offset().Round(r.Every))
	if r.holds(label) {
		// The bundle under this label is live; overwriting it would leave
		// two entries on one directory and the prune deleting the newer.
		r.busy = false
		r.mu.Unlock()
		return 0
	}
	r.mu.Unlock()
	began := time.Now()
	entry, err := r.capture(ctx, label, false)
	took := time.Since(began)
	r.mu.Lock()
	defer r.mu.Unlock()
	r.busy = false
	switch {
	case errors.Is(err, errNotPaused), errors.Is(err, errNoHolder):
		// The next call may find the window over (or the game held again);
		// ask again in a moment, not on every call of a running window.
		r.nextCheck = time.Now().Add(5 * time.Second)
		return took
	case err != nil:
		r.errs = append(r.errs, fmt.Sprintf("%s: %v", label, err))
		r.last = time.Now()
		return took
	}
	r.last = time.Now()
	r.entries = append(r.entries, entry)
	for len(r.entries) > r.Keep {
		_ = os.RemoveAll(r.entries[0].Path)
		r.entries = r.entries[1:]
	}
	// A provisional index after every capture: a run the harness never
	// closes (killed with its tool shell) still resumes from its last
	// bundle, with no failure tick to compare against.
	provisional := &Ring{Case: r.Case, SourceRevision: r.SourceRevision, Next: entry.Label, Note: "the last run of " + r.Case + " ended without closing its ring"}
	provisional.Entries = append(append([]Checkpoint(nil), r.Prior...), r.entries...)
	for len(provisional.Entries) > r.Keep {
		provisional.Entries = provisional.Entries[1:]
	}
	if err := provisional.Write(r.Dir); err != nil {
		r.errs = append(r.errs, fmt.Sprintf("%s: write provisional index: %v", label, err))
	}
	return took
}

// holds reports whether a live ring entry carries label; the caller holds
// r.mu.
func (r *CheckpointRing) holds(label string) bool {
	for _, e := range r.entries {
		if e.Label == label {
			return true
		}
	}
	return false
}

// Capture takes a named bundle now (a phase boundary a case declared),
// pausing the game if it runs; the bundle is kept beside the ring and is
// not pruned.
func (r *CheckpointRing) Capture(ctx context.Context, label string) (Checkpoint, error) {
	r.mu.Lock()
	if r.capped != "" {
		r.mu.Unlock()
		return Checkpoint{}, fmt.Errorf("ring capped: %s", r.capped)
	}
	if r.busy {
		r.mu.Unlock()
		return Checkpoint{}, errors.New("a capture is already in progress")
	}
	r.busy = true
	r.mu.Unlock()
	entry, err := r.capture(ctx, label, true)
	r.mu.Lock()
	defer r.mu.Unlock()
	r.busy = false
	if err != nil {
		r.errs = append(r.errs, fmt.Sprintf("%s: %v", label, err))
		return Checkpoint{}, err
	}
	r.entries = append(r.entries, entry)
	return entry, nil
}

// Held reports whether a side holds the game for a capture: the harness,
// a running service, or a Saver.
func (r *CheckpointRing) Held() bool {
	return r.Saver != nil || (r.Bridge != nil && r.Bridge() != nil) || (r.Service != nil && r.Service() != nil)
}

// Fail takes the final bundle of a failed run (FailedCheckpoint), pausing
// the game if it still runs; it never fails the run further.
func (r *CheckpointRing) Fail(ctx context.Context) *Checkpoint {
	r.mu.Lock()
	r.busy = true
	r.mu.Unlock()
	entry, err := r.capture(ctx, FailedCheckpoint, true)
	r.mu.Lock()
	defer r.mu.Unlock()
	r.busy = false
	if err != nil {
		r.errs = append(r.errs, fmt.Sprintf("%s: %v", FailedCheckpoint, err))
		return nil
	}
	r.failed = &entry
	return r.failed
}

// capture takes one bundle through whichever side holds the GABP slot.
// force pauses a running game first; otherwise a running game declines
// with errNotPaused.
func (r *CheckpointRing) capture(ctx context.Context, label string, force bool) (Checkpoint, error) {
	began := time.Now()
	dir := filepath.Join(r.Dir, label)
	if err := os.RemoveAll(dir); err != nil {
		return Checkpoint{}, err
	}
	if err := os.MkdirAll(dir, 0755); err != nil {
		return Checkpoint{}, err
	}
	entry := Checkpoint{
		Case: r.Case, Label: label, OffsetMs: r.Offset().Milliseconds(), Save: CheckpointSaveName(r.Case),
		Prepared: r.Prepared, State: r.StateSnapshot(), Serve: r.Serve, ServiceProfile: r.Config.ServiceProfileDir(), SourceRevision: r.SourceRevision,
		Package: r.Fingerprint.Package, Start: r.Fingerprint.Start, Expansions: r.Fingerprint.Expansions,
		At: began.UTC().Format(time.RFC3339), Path: dir,
	}
	if r.StageKey != "" {
		entry.Stage, entry.StageKey = label, r.StageKey
	}
	var saved string
	var err error
	switch {
	case r.Saver != nil:
		entry.Through = "saver"
		saved, entry.Tick, entry.Identity, err = r.Saver(ctx, entry.Save, label, force)
	case r.Bridge != nil && r.Bridge() != nil:
		entry.Through = "bridge"
		saved, entry.Tick, entry.Identity, err = r.bridgeSave(ctx, r.Bridge(), entry.Save, label, force)
	case r.Service != nil && r.Service() != nil:
		entry.Through = "service"
		saved, entry.Tick, entry.Identity, err = r.serviceSave(ctx, r.Service(), entry.Save, label, force)
	default:
		err = errNoHolder
	}
	if err != nil {
		_ = os.RemoveAll(dir)
		return Checkpoint{}, err
	}
	if err := copyFile(saved, filepath.Join(dir, entry.Save+".rws")); err != nil {
		_ = os.RemoveAll(dir)
		return Checkpoint{}, err
	}
	if r.StorePath != "" {
		if _, statErr := os.Stat(r.StorePath); statErr == nil {
			if err := snapshotStore(ctx, r.StorePath, filepath.Join(dir, CheckpointStoreFile)); err != nil {
				_ = os.RemoveAll(dir)
				return Checkpoint{}, fmt.Errorf("store: %w", err)
			}
			entry.Store = true
		}
	}
	if journal, jerr := ClockJournalDir(r.Config.Configuration); jerr == nil {
		if _, statErr := os.Stat(journal); statErr == nil {
			if err := CopyTree(journal, filepath.Join(dir, CheckpointJournalDir)); err != nil {
				_ = os.RemoveAll(dir)
				return Checkpoint{}, fmt.Errorf("journal: %w", err)
			}
			entry.Journal = true
		}
	}
	entry.WallMs = time.Since(began).Milliseconds()
	data, err := json.MarshalIndent(entry, "", "  ")
	if err != nil {
		return Checkpoint{}, err
	}
	if err := os.WriteFile(filepath.Join(dir, CheckpointSidecar), data, 0644); err != nil {
		return Checkpoint{}, err
	}
	return entry, nil
}

// StateSnapshot copies the case state (for a sidecar, or for a stage
// bundle taken beside the ring); the case may keep adding to it while the
// capture writes.
func (r *CheckpointRing) StateSnapshot() map[string]any {
	r.mu.Lock()
	defer r.mu.Unlock()
	if len(r.State) == 0 {
		return nil
	}
	out := make(map[string]any, len(r.State))
	for k, v := range r.State {
		out[k] = v
	}
	return out
}

// bridgeSave saves through lifecycle_save on the harness session: the game
// must be paused (force pauses it), and the save must complete at the tick
// the identity read observed.
func (r *CheckpointRing) bridgeSave(ctx context.Context, h *Harness, name, label string, force bool) (path string, tick uint64, identity map[string]any, err error) {
	loaded, err := readLoaded(ctx, h, "checkpoint-"+label+"-identity")
	if err != nil {
		return "", 0, nil, err
	}
	paused, _ := AsBool(loaded["paused"])
	if !paused {
		if !force {
			return "", 0, nil, errNotPaused
		}
		if _, err := h.Call(ctx, "checkpoint-"+label+"-pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
			return "", 0, nil, err
		}
		if loaded, err = readLoaded(ctx, h, "checkpoint-"+label+"-identity-paused"); err != nil {
			return "", 0, nil, err
		}
	}
	loadedContext, _ := AsMap(loaded["context"])
	identity, _ = AsMap(loadedContext["identity"])
	tickNumber := AsNumber(loadedContext["tick"])
	reply, err := h.Wire(ctx, "checkpoint-"+label+"-save", "lifecycle_save", map[string]any{
		"player":       map[string]any{"identity": identity, "playerDirection": 1, "requestId": fmt.Sprintf("checkpoint-%s-%d", label, time.Now().UnixNano())},
		"saveName":     name,
		"expectedTick": tickNumber,
	})
	if err != nil {
		return "", 0, nil, fmt.Errorf("lifecycle_save: %w", err)
	}
	if _, _, err := Outcome(reply, "completed"); err != nil {
		return "", 0, nil, fmt.Errorf("lifecycle_save: %w", err)
	}
	path = r.Config.profileSave(name)
	if _, err := os.Stat(path); err != nil {
		return "", 0, nil, fmt.Errorf("lifecycle_save completed but %s is not there", path)
	}
	return path, uint64(tickNumber), identity, nil
}

// readLoaded is the "loaded" outcome of lifecycle_read_identity.
func readLoaded(ctx context.Context, h *Harness, label string) (map[string]any, error) {
	reply, err := h.Wire(ctx, label, "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return nil, err
	}
	_, loaded, err := Outcome(reply, "loaded")
	return loaded, err
}

// serviceSave saves through the running service (ServiceSave), which
// pauses to manual control and resumes afterwards.
func (r *CheckpointRing) serviceSave(ctx context.Context, p *ServiceProcess, name, label string, force bool) (path string, tick uint64, identity map[string]any, err error) {
	if exitErr := p.Exited(); exitErr != nil {
		return "", 0, nil, exitErr
	}
	// A service that is not automating yet (its first resume still in
	// flight), that a case holds in manual, or that has not observed a
	// tick yet (its save would 503 "Controller data is unavailable", #309)
	// cannot be paused for a save; a periodic capture waits for the next
	// cadence, a forced one tries.
	if st, status, err := p.API("GET", "/api/state", nil, ""); !force && (err != nil || status != 200 || !serviceCanCapture(st)) {
		return "", 0, nil, errNotPaused
	}
	prefix := p.Label() + "-checkpoint-" + label
	path, err = ServiceSave(ctx, r.Config, name, p.HoldAuthority, p.API, p.Identity, p.Token, prefix)
	if err != nil {
		return "", 0, nil, err
	}
	if t, ok := serviceTick(p.API); ok {
		tick = t
	}
	return path, tick, p.Identity, nil
}

// serviceCanCapture says whether a service's /api/state body admits a
// periodic capture: automating, with a fresh (non-stale) game read, since
// /api/lifecycle/save refuses until the service knows the game's tick and
// identity.
func serviceCanCapture(state map[string]any) bool {
	if AsString(state["mode"]) != "automate" {
		return false
	}
	game, _ := state["game"].(map[string]any)
	if stale, _ := game["stale"].(bool); stale {
		return false
	}
	_, ok := game["tick"].(float64)
	return ok
}

// serviceTick reads the live game tick from the service's /api/state.
func serviceTick(apiCall func(string, string, map[string]any, string) (map[string]any, int, error)) (uint64, bool) {
	st, status, err := apiCall("GET", "/api/state", nil, "")
	if err != nil || status != 200 {
		return 0, false
	}
	game, _ := st["game"].(map[string]any)
	tick, ok := game["tick"].(float64)
	if !ok || tick < 0 {
		return 0, false
	}
	return uint64(tick), true
}

// ServiceSave pauses the service's clock, saves the live game as name
// through /api/lifecycle/save (which needs manual control) and resumes,
// holding the keep-alive (hold) throughout so it does not re-acquire under
// the save. It returns the save's path in the game's profile.
func ServiceSave(ctx context.Context, cfg *Config, name string, hold func(bool), apiCall func(string, string, map[string]any, string) (map[string]any, int, error), identity map[string]any, token, prefix string) (string, error) {
	hold(true)
	defer hold(false)
	stamp := time.Now().UnixNano()
	paused, status, err := apiCall("POST", "/api/player/control/pause", map[string]any{"requestId": fmt.Sprintf("%s-pause-%d", prefix, stamp), "expected": identity}, token)
	if err != nil {
		return "", fmt.Errorf("pause: %w", err)
	}
	if status != 200 {
		return "", fmt.Errorf("pause status=%d body=%#v", status, paused)
	}
	// Once the pause is acknowledged the case's play is stopped: every
	// exit below resumes, or a failed save would strand the game paused for
	// the rest of the run.
	resume := func() error {
		resumed, status, err := apiCall("POST", "/api/player/control/resume", map[string]any{"requestId": fmt.Sprintf("%s-resume-%d", prefix, stamp), "expected": identity}, token)
		if err != nil {
			return fmt.Errorf("resume: %w", err)
		}
		if status != 200 {
			return fmt.Errorf("resume status=%d body=%#v", status, resumed)
		}
		return nil
	}
	src, err := serviceSaveWhilePaused(ctx, cfg, name, apiCall, token, prefix, stamp)
	if rerr := resume(); rerr != nil {
		if err != nil {
			return "", fmt.Errorf("%w; then %v", err, rerr)
		}
		return "", rerr
	}
	return src, err
}

// serviceSaveWhilePaused waits for manual control, posts the save and
// checks the file; ServiceSave resumes play afterwards whatever it returns.
func serviceSaveWhilePaused(ctx context.Context, cfg *Config, name string, apiCall func(string, string, map[string]any, string) (map[string]any, int, error), token, prefix string, stamp int64) (string, error) {
	// The pause is acknowledged before the mode reads manual; wait for it.
	manualDeadline := time.Now().Add(30 * time.Second)
	for {
		state, status, err := apiCall("GET", "/api/state", nil, "")
		if err == nil && status == 200 && AsString(state["mode"]) == "manual" {
			break
		}
		if time.Now().After(manualDeadline) {
			return "", fmt.Errorf("service did not reach manual control after pause: %#v", state)
		}
		select {
		case <-ctx.Done():
			return "", ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
	saved, status, err := apiCall("POST", "/api/lifecycle/save", map[string]any{"requestId": fmt.Sprintf("%s-save-%d", prefix, stamp), "saveName": name}, token)
	if err != nil {
		return "", fmt.Errorf("save: %w", err)
	}
	if status != 201 {
		return "", fmt.Errorf("save status=%d body=%#v", status, saved)
	}
	src := cfg.profileSave(name)
	if _, err := os.Stat(src); err != nil {
		return "", fmt.Errorf("save completed but %s is missing: %w", src, err)
	}
	return src, nil
}

// profileSave is where the running game writes a save named name: the
// headless profile under Prepare, the windowed one under PrepareRendered.
func (c *Config) profileSave(name string) string {
	profile := "profile"
	if c.Headless {
		profile = "headless-profile"
	}
	return filepath.Join(c.Root, profile, "Saves", name+".rws")
}

// CheckpointSaveName is the save name a case's bundles use, stable per case
// so the game's profile holds one such save at a time.
func CheckpointSaveName(caseName string) string {
	return "RimGovernor-checkpoint-" + strings.ReplaceAll(caseName, "/", "-")
}

// snapshotStore copies the SQLite database at src to dst through the
// online backup, so a service holding it open leaves a consistent copy.
func snapshotStore(ctx context.Context, src, dst string) error {
	s, err := store.Open(ctx, src)
	if err != nil {
		return err
	}
	defer s.Close()
	return s.Snapshot(ctx, dst)
}

// CopyTree copies the regular files under src into dst, recreating the
// directory layout.
func CopyTree(src, dst string) error {
	return filepath.WalkDir(src, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		rel, err := filepath.Rel(src, path)
		if err != nil {
			return err
		}
		target := filepath.Join(dst, rel)
		if d.IsDir() {
			return os.MkdirAll(target, 0755)
		}
		if !d.Type().IsRegular() {
			return nil
		}
		return copyFile(path, target)
	})
}

// RestoreClockJournal replaces the clock journal of the game configDir
// launches with the bundle's copy. Only before a fresh games_start: a
// running process holds the journal's cursor in static state (#119), so
// the resumed run relaunches the game.
func RestoreClockJournal(configDir, bundle string) error {
	journal, err := ClockJournalDir(configDir)
	if err != nil {
		return err
	}
	if err := os.RemoveAll(journal); err != nil {
		return err
	}
	src := filepath.Join(bundle, CheckpointJournalDir)
	if _, err := os.Stat(src); err != nil {
		return nil
	}
	return CopyTree(src, journal)
}

// StageCheckpoint copies the bundle's save into root/profile/Saves (the
// durable location Prepare mirrors into the game's profile), replacing any
// earlier copy, and the bundle's store to storePath when it carries one.
// It returns the save name the resumed session loads.
func StageCheckpoint(root string, c Checkpoint, storePath string) (string, error) {
	src := filepath.Join(c.Path, c.Save+".rws")
	if _, err := os.Stat(src); err != nil {
		return "", fmt.Errorf("checkpoint %s: %w", c.Label, err)
	}
	target := filepath.Join(root, "profile", "Saves", c.Save+".rws")
	if err := os.MkdirAll(filepath.Dir(target), 0755); err != nil {
		return "", err
	}
	if err := copyFile(src, target); err != nil {
		return "", err
	}
	if c.Store && storePath != "" {
		if err := os.MkdirAll(filepath.Dir(storePath), 0755); err != nil {
			return "", err
		}
		if err := copyFile(filepath.Join(c.Path, CheckpointStoreFile), storePath); err != nil {
			return "", fmt.Errorf("checkpoint %s store: %w", c.Label, err)
		}
	}
	return c.Save, nil
}
