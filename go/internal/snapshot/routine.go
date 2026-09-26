package snapshot

import (
	"bytes"
	"compress/gzip"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// DirEnv names the directory a serve records every enabled routine
// review's snapshot into, as routine-<tick>-<seq>.json; unset records nothing.
const DirEnv = "RIMGOVERNOR_SNAPSHOT_DIR"

// Routine is one enabled routine review as recorded: the input the review
// passed policy.DetectRoutine and, optionally, the journal's review cursor
// it filed. Facts carry every census the planners of that tick read
// (ColonyGrid, Upkeep, Research, ...).
type Routine struct {
	// Recorded is provenance: the world and tick, and whatever the
	// recorder adds (a case name, a commit).
	Recorded string
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Facts    policy.RoutineFacts
	Latches  policy.RoutineLatches
	// Policy is the staged policy the review detected against.
	Policy policy.RoutinePolicy
	// Review is the journal's routine review after this one filed.
	Review *store.RoutineReview
	// Projection is the colony reading the review took, the planners'
	// inputs beyond Facts (site cells, planning definitions, power
	// topology, rooms, bounds); its Facts are left empty here and restored
	// from Facts on load, and its Zones and Window are not recorded. Nil
	// in a recording that predates it.
	Projection *observation.ColonyProjection
}

// FromReview is the snapshot of an enabled review's result; false when
// the review detected nothing (disabled).
func FromReview(current domain.GenerationSnapshot, tick domain.Tick, result store.RoutineReviewResult, reading observation.ColonyProjection) (Routine, bool) {
	if result.Detection == nil {
		return Routine{}, false
	}
	review := result.Review
	// Facts ride once; the zone read holds native protobuf messages and
	// the window only repeats Region and Cells, so neither is recorded.
	var none observation.ColonyProjection
	reading.Facts, reading.Zones, reading.Window = none.Facts, none.Zones, none.Window
	return Routine{
		Projection: &reading,
		Recorded:   fmt.Sprintf("colony %s load %s map %d tick %d", current.Colony, current.Load, current.Map, tick),
		Snapshot:   current, Tick: tick,
		Facts: result.Detection.Facts, Latches: result.Detection.Latches, Policy: result.Detection.Policy,
		Review: &review,
	}, true
}

// Record writes r into dir as routine-<tick>-<seq>.json, seq the first
// from 1 not already taken: several reviews at one paused tick each keep
// their own file.
func Record(dir string, r Routine) error {
	data, err := Encode(r)
	if err != nil {
		return err
	}
	if err = os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	for seq := 1; ; seq++ {
		f, err := os.OpenFile(filepath.Join(dir, fmt.Sprintf("routine-%d-%d.json", r.Tick, seq)), os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o644)
		if errors.Is(err, fs.ErrExist) {
			continue
		}
		if err != nil {
			return err
		}
		_, err = f.Write(data)
		return errors.Join(err, f.Close())
	}
}

// readFile reads a recording, gunzipping one named *.gz: a colony's cell
// census runs to megabytes of JSON, so committed testdata is compressed.
func readFile(path string) ([]byte, error) {
	data, err := os.ReadFile(path)
	if err != nil || !strings.HasSuffix(path, ".gz") {
		return data, err
	}
	z, err := gzip.NewReader(bytes.NewReader(data))
	if err != nil {
		return nil, err
	}
	defer z.Close()
	return io.ReadAll(z)
}

// Load reads a recorded routine snapshot.
func Load(path string) (Routine, error) {
	data, err := readFile(path)
	if err != nil {
		return Routine{}, fmt.Errorf("%s: %w", path, err)
	}
	var r Routine
	if err = Decode(data, &r); err != nil {
		return Routine{}, fmt.Errorf("%s: %w", path, err)
	}
	if r.Projection != nil {
		r.Projection.Facts = r.Facts
	}
	return r, nil
}

// Detect replays the review's need detection over the recorded facts.
func (r Routine) Detect() (policy.RoutineNeeds, error) {
	return policy.DetectRoutine(r.Facts, r.Latches, r.Policy)
}

// Assessment is the replayed review's assessment of one goal.
func (r Routine) Assessment(id policy.GoalID) (policy.RoutineAssessment, error) {
	needs, err := r.Detect()
	if err != nil {
		return policy.RoutineAssessment{}, err
	}
	for _, a := range needs.Assessments {
		if a.ID == id {
			return a, nil
		}
	}
	return policy.RoutineAssessment{}, errors.New("snapshot: review assessed no " + string(id))
}

// TrimCells drops the planning window's site cells, most of a recording's
// size: a committed snapshot keeps them only when its test runs a site
// search.
func (r *Routine) TrimCells() {
	if r.Projection != nil {
		r.Projection.Cells = nil
	}
}

// Compress is r as committed testdata: compact JSON, gzipped (a .json.gz
// file Load reads), a tenth of the indented recording.
func Compress(r Routine) ([]byte, error) {
	if r.Projection != nil {
		// Facts ride once, as in Record.
		trimmed := *r.Projection
		trimmed.Facts = policy.RoutineFacts{}
		r.Projection = &trimmed
	}
	data, err := Encode(r)
	if err != nil {
		return nil, err
	}
	var compact, out bytes.Buffer
	if err = json.Compact(&compact, data); err != nil {
		return nil, err
	}
	zw, _ := gzip.NewWriterLevel(&out, gzip.BestCompression)
	if _, err = zw.Write(compact.Bytes()); err != nil {
		return nil, err
	}
	if err = zw.Close(); err != nil {
		return nil, err
	}
	return out.Bytes(), nil
}
