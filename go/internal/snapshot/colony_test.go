package snapshot

import (
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// A review line leaves the colony fields the decoder rebuilds from the
// recorded colony rows to them, and replay materialises exactly what was
// recorded: decoded fields, fields the review changed, and fields the
// review holds zero where the decoder sets them (#795 step 3).
func TestRecordRebuildsDecodedColonyFacts(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	reply := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, reply); err != nil {
		t.Fatal(err)
	}
	base, err := Load(cleanFilthy)
	if err != nil {
		t.Fatal(err)
	}
	dir := t.TempDir()
	m := facts.NewStore()
	m.SetRecorder(MirrorRecorder(dir))
	scope := facts.Scope{Load: "l", Map: 1, Generation: 1}
	var want []Rounds
	var wantSteps []Step
	for i := 0; i < 4; i++ {
		v := proto.Clone(reply.GetObserved()).(*o.ColonyFactsSnapshot)
		v.ColonistCount = proto.Uint32(uint32(3 + i%2))
		for name, rows := range bridge.SplitColonyFacts(v) {
			facts.PutTable(m, scope, name, rows, facts.At(int64(base.Tick)+int64(i)))
		}
		id := v.Context.GetIdentity()
		expected := observation.Identity{Colony: domain.ColonyID(id.GetColonyId()), Load: domain.LoadID(id.GetLoadToken()), Map: domain.MapID(id.GetMapId()), Tick: domain.Tick(v.Context.GetTick())}
		decoded, err := observation.DecodeColony(&o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: v}}, expected, bridge.Tables{})
		if err != nil {
			t.Fatal(err)
		}
		r := base
		r.Tick = base.Tick + domain.Tick(i)
		r.Facts = decoded.Facts
		r.Facts.Colonists = domain.Known(int64(9)) // the review's own value
		r.Facts.BedCapacity = domain.Unknown[int64]()
		projection := decoded
		var none observation.ColonyProjection
		projection.Facts, projection.Zones, projection.Window = r.Facts, none.Zones, none.Window
		projection.Workers = domain.Known(i)
		projection.Bounds.Width = 0 // held zero where the decoder sets it
		r.Projection = &projection
		if err = Record(dir, r); err != nil {
			t.Fatal(err)
		}
		want = append(want, r)
		reading := projection
		reading.Identity.Tick = r.Tick
		if err = RecordStep(dir, "building", policy.MaintainCleanFacilities, r.Snapshot, reading); err != nil {
			t.Fatal(err)
		}
		var step Step
		data, _ := Encode(Step{Snapshot: r.Snapshot, Tick: r.Tick, Concern: policy.MaintainCleanFacilities, Planner: "building", Projection: reading})
		if err = Decode(data, &step); err != nil {
			t.Fatal(err)
		}
		wantSteps = append(wantSteps, step)
	}
	paths, _ := filepath.Glob(filepath.Join(dir, "routine-stream-*.jsonl"))
	if len(paths) != 1 {
		t.Fatal(paths)
	}
	stream, _ := os.ReadFile(paths[0])
	if !strings.Contains(string(stream), `"Mirror":{"colony":`) {
		t.Fatal("no review line names the colony sections")
	}
	i := 0
	err = Replay(paths[0], nil, func(at Review, got Rounds) (bool, error) {
		if !reflect.DeepEqual(got, want[i]) {
			a, _ := Encode(got)
			b, _ := Encode(want[i])
			t.Errorf("review %s does not round-trip:\n%s\nwant\n%s", at, a, b)
		}
		i++
		return true, nil
	})
	if err != nil || i != len(want) {
		t.Fatal(i, err)
	}
	steps, err := Steps(paths[0])
	if err != nil || len(steps) != len(wantSteps) {
		t.Fatal(steps, err)
	}
	for i, s := range steps {
		got, err := LoadStreamStep(paths[0], s.String())
		if err != nil {
			t.Fatal(err)
		}
		got.Recorded = ""
		if !reflect.DeepEqual(got, wantSteps[i]) {
			t.Errorf("step %s does not round-trip", s)
		}
	}
}
