package snapshot

import (
	"bytes"
	"compress/gzip"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// DirEnv names the directory a serve records every enabled routine
// review's snapshot into, one routine-stream-<tick>-<pid>.jsonl per serve
// (stream.go); unset records nothing.
const DirEnv = "RIMGOVERNOR_SNAPSHOT_DIR"

// Routine is one enabled rounds as recorded: the input the review
// passed policy.DetectRounds and, optionally, the journal's review cursor
// it filed. Facts carry every census the planners of that tick read
// (Upkeep, Research, ...).
type Rounds struct {
	// Recorded is provenance: the world and tick, and whatever the
	// recorder adds (a case name, a commit).
	Recorded string
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Facts    policy.RoundsFacts
	Latches  policy.RoundsLatches
	// Policy is the staged policy the review detected against.
	Policy policy.RoundsPolicy
	// Review is the journal's rounds after this one filed.
	Review *store.Rounds
	// Projection is the colony reading the review took, the planners'
	// inputs beyond Facts (site cells, planning definitions, power
	// topology, rooms, bounds); its Facts are left empty here and restored
	// from Facts on load, and its Zones and Window are not recorded. Nil
	// in a recording that predates it.
	Projection *observation.ColonyProjection
}

// FromReview is the snapshot of an enabled review's result; false when
// the review detected nothing (disabled).
func FromReview(current domain.GenerationSnapshot, tick domain.Tick, result store.RoundsResult, reading observation.ColonyProjection) (Rounds, bool) {
	if result.Detection == nil {
		return Rounds{}, false
	}
	review := result.Review
	// Facts ride once; the zone read holds native protobuf messages and
	// the window only repeats Region and Cells, so neither is recorded.
	var none observation.ColonyProjection
	reading.Facts, reading.Zones, reading.Window = none.Facts, none.Zones, none.Window
	return Rounds{
		Projection: &reading,
		Recorded:   fmt.Sprintf("colony %s load %s map %d tick %d", current.Colony, current.Load, current.Map, tick),
		Snapshot:   current, Tick: tick,
		Facts: result.Detection.Facts, Latches: result.Detection.Latches, Policy: result.Detection.Policy,
		Review: &review,
	}, true
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
func Load(path string) (Rounds, error) {
	data, err := readFile(path)
	if err != nil {
		return Rounds{}, fmt.Errorf("%s: %w", path, err)
	}
	r, err := decodeRounds(data)
	if err != nil {
		return Rounds{}, fmt.Errorf("%s: %w", path, err)
	}
	// A recording from before the item facts (#1734) carries none: it
	// replays with Core's numbers, the game it was recorded on.
	if r.Facts.Items.Market == nil {
		r.Facts.Items = policy.CoreItemFacts()
		r.Facts.MedicalReserve.Catalog = r.Facts.Items
		if r.Projection != nil {
			r.Projection.Facts = r.Facts
		}
	}
	return r, nil
}

// decodeRounds reads one encoded review, restoring the projection's Facts.
func decodeRounds(data []byte) (Rounds, error) {
	var r Rounds
	if err := Decode(data, &r); err != nil {
		return Rounds{}, err
	}
	dropNativeGearCensus(&r.Facts)
	if r.Projection != nil {
		r.Projection.Facts = r.Facts
	}
	return r, nil
}

// dropNativeGearCensus leaves a recording's gear census unknown when a pawn
// in it has no loadout model: a recording from before the model judged gear
// by the native deficit rule, which no planner reads now, and a census that
// cannot be judged is the one a live read would refuse.
func dropNativeGearCensus(f *policy.RoundsFacts) {
	v, known := f.Gear.Value()
	if known && slices.ContainsFunc(v.Pawns, func(p policy.GearPawn) bool {
		_, modeled := p.LoadoutModel.Value()
		return !modeled && p.ModelRefusal == "" && !p.Blocked
	}) {
		f.Gear = domain.Unknown[policy.GearObservation]()
	}
}

// Detect replays the review's need detection over the recorded facts.
func (r Rounds) Detect() (policy.RoundsFindings, error) {
	return policy.InspectRounds(r.Facts, r.Latches, r.Policy)
}

// Assessment is the replayed review's assessment of one goal.
func (r Rounds) Assessment(id policy.ConcernID) (policy.RoundsAssessment, error) {
	needs, err := r.Detect()
	if err != nil {
		return policy.RoundsAssessment{}, err
	}
	for _, a := range needs.All() {
		if a.ID == id {
			return a, nil
		}
	}
	return policy.RoundsAssessment{}, errors.New("snapshot: review assessed no " + string(id))
}

// TrimCells drops the planning window's site cells, most of a recording's
// size: a committed snapshot keeps them only when its test runs a site
// search.
func (r *Rounds) TrimCells() {
	if r.Projection != nil {
		r.Projection.Cells = nil
	}
}

// Compress is r as committed testdata: compact JSON, gzipped (a .json.gz
// file Load reads), a tenth of the indented recording.
func Compress(r Rounds) ([]byte, error) {
	if r.Projection != nil {
		// Facts ride once, as in Record.
		trimmed := *r.Projection
		trimmed.Facts = policy.RoundsFacts{}
		r.Projection = &trimmed
	}
	return compress(r)
}

// CompressStep is s as committed testdata, without its planning cells
// unless keepCells (a site-search test needs them).
func CompressStep(s Step, keepCells bool) ([]byte, error) {
	if !keepCells {
		s.Projection.Cells = nil
	}
	return compress(s)
}

func compress(v any) ([]byte, error) {
	data, err := Encode(v)
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
