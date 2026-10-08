package snapshot

import (
	"encoding/json"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// A review read with a catalog carries BedPrice, a func; the recorder drops
// it so the review still encodes (#2141: starving-tribal recorded none).
func TestFromReviewDropsBedPrice(t *testing.T) {
	reading := observation.ColonyProjection{BedPrice: func(_, _ policy.Resource) (float64, bool) { return 1, true }}
	rec, ok := FromReview(domain.GenerationSnapshot{}, 1, store.RoundsResult{Detection: &store.RoundsDetection{}}, reading)
	if !ok {
		t.Fatal("review not recorded")
	}
	if _, err := Encode(rec); err != nil {
		t.Fatal(err)
	}
}

// Recorded from acceptance run clean/filthy at e24c531b (the last review of
// the run, tick 158107): blood filth in a kitchen with no cleaner.
const cleanFilthy = "testdata/clean-filthy-kitchen.json.gz"

func TestReplayReproducesTheRecordedReview(t *testing.T) {
	r, err := Load(cleanFilthy)
	if err != nil {
		t.Fatal(err)
	}
	needs, err := r.Detect()
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(needs.Latches, r.Review.Latches) {
		t.Fatalf("latches %+v, recorded %+v", needs.Latches, r.Review.Latches)
	}
	bound := map[domain.ConcernID]bool{}
	for _, g := range r.Review.Standards {
		bound[g.Concern] = true
	}
	// AllowStartingSupplies is gone from the catalog (#2188).
	delete(bound, "AllowStartingSupplies")
	for _, a := range needs.All() {
		// MaintainSurgery (#1164), MaintainShelter (#1325), MaintainFirebreak (#1536) and MaintainButcherSpot, ManagePollution EnsureMechCharger MaintainGeneBank, MaintainBurial and MaintainIncineration are newer than the recording.
		if !bound[a.ID] && a.ID != policy.MaintainSurgery && a.ID != policy.MaintainShelter && a.ID != policy.MaintainFirebreak && a.ID != policy.MaintainPsylink && a.ID != policy.ManageCreepJoiners && a.ID != policy.MaintainPermits && a.ID != policy.MaintainIdeoRoles && a.ID != policy.MaintainRituals && a.ID != policy.MaintainBabyFeeding && a.ID != policy.MaintainMechs && a.ID != policy.MaintainButcherSpot && a.ID != policy.ManagePollution && a.ID != policy.EnsureMechCharger && a.ID != policy.MaintainGeneBank && a.ID != policy.MaintainBurial && a.ID != policy.MaintainIncineration {
			t.Error("replay assessed unbound", a.ID)
		}
		delete(bound, a.ID)
	}
	if len(bound) > 0 {
		t.Error("recorded goals replay did not assess", bound)
	}
}

func TestReplayFilthyKitchenOpensCleaning(t *testing.T) {
	r, err := Load(cleanFilthy)
	if err != nil {
		t.Fatal(err)
	}
	a, err := r.Assessment(policy.MaintainCleanFacilities)
	if err != nil || a.Finding != domain.FindingUnmet || a.MethodUnavailable {
		t.Fatal(a, err)
	}
}

func TestRecordStreamsKeyframesAndPatches(t *testing.T) {
	slowtest.Skip(t, "streams a full recorded game; runs under cmd/test -full and nightly")
	r, err := Load(cleanFilthy)
	if err != nil {
		t.Fatal(err)
	}
	dir := t.TempDir()
	// Reviews that differ from one another: a tick, a changed pawn list and
	// a dropped projection, twice at one tick, across a keyframe boundary.
	var want []Rounds
	for i := 0; i < KeyEvery+3; i++ {
		v, _ := Load(cleanFilthy)
		v.Tick = r.Tick + domain.Tick(i/2)
		v.Recorded = fmt.Sprint("review ", i)
		if i%3 == 1 {
			v.Projection = nil
		}
		if i%4 == 2 && v.Review != nil && len(v.Review.Standards) > 0 {
			v.Review.Standards = v.Review.Standards[:len(v.Review.Standards)-1]
		}
		if err = Record(dir, v); err != nil {
			t.Fatal(err)
		}
		want = append(want, v)
	}
	paths, _ := filepath.Glob(filepath.Join(dir, "routine-stream-*.jsonl"))
	if len(paths) != 1 {
		t.Fatal("want one stream per serve, got", paths)
	}
	reviews, err := Reviews(paths[0])
	if err != nil || len(reviews) != len(want) {
		t.Fatal(reviews, err)
	}
	if reviews[1] != (Review{r.Tick, 2}) {
		t.Error("second review at one tick is seq 2:", reviews[1])
	}
	i := 0
	err = Replay(paths[0], nil, func(at Review, got Rounds) (bool, error) {
		if !reflect.DeepEqual(got, want[i]) {
			t.Errorf("review %d (%s) does not round-trip", i, at)
		}
		i++
		return true, nil
	})
	if err != nil || i != len(want) {
		t.Fatal(i, err)
	}
	last := want[len(want)-1]
	got, err := LoadReview(paths[0], last.Tick, 0)
	if err != nil || !reflect.DeepEqual(got, last) {
		t.Fatal("LoadReview did not materialise the last review", err)
	}
	if _, err = LoadReview(paths[0], 1, 0); err == nil {
		t.Fatal("an unrecorded tick loaded")
	}
}

func TestPatchNodes(t *testing.T) {
	old, _ := parseTree([]byte(`{"rows":[{"ID":"p1","x":1},{"ID":"p2","x":2},{"ID":"p3"}],"a":1,"b":[1,2,3],"c":{"d":"x"},"gone":true,"n":null}`))
	new, _ := parseTree([]byte(`{"rows":[{"ID":"p0"},{"ID":"p2","x":3},{"ID":"p1","x":1}],"a":1,"b":[1,5],"c":{"d":"y","e":[]},"n":null,"m":null}`))
	patch, changed := diffTree(old, new)
	if !changed {
		t.Fatal("no patch")
	}
	data, _ := json.Marshal(patch) // as a stream line carries it
	node, _ := parseTree(data)
	got, err := applyPatch(old, node.(map[string]any))
	if err != nil || !reflect.DeepEqual(got, new) {
		t.Fatal(got, err)
	}
	if _, changed = diffTree(new, new); changed {
		t.Fatal("equal trees patched")
	}
}
