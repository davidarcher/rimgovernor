package observation

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

// The scenario emits this same-tick native/Python pair after ordinary comfort
// use. Replaying it here exercises the actual Go boundary and durable journal.
func TestNativeUpkeepReplay(t *testing.T) {
	path := os.Getenv("RIMBOT_NATIVE_UPKEEP_REPLAY")
	if path == "" {
		t.Skip("requires upkeep-replay.json from native comfort acceptance")
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var fixture struct {
		Medical map[string]struct {
			Known, Active                     bool
			Stock, Entry, Recovery, Replenish int64
		}
		Colony   json.RawMessage
		Expected map[policy.GoalID]struct {
			Need     domain.NeedState
			Priority int
			Targets  []string
			Metric   float64
		}
	}
	if err = json.Unmarshal(data, &fixture); err != nil {
		t.Fatal(err)
	}
	var reply o.ColonyFactsReply
	if err = protojson.Unmarshal(fixture.Colony, &reply); err != nil {
		t.Fatal(err)
	}
	identity, err := contextIdentity(reply.GetObserved().GetContext())
	if err != nil {
		t.Fatal(err)
	}
	projection, err := DecodeColony(&reply, identity)
	if err != nil {
		t.Fatal(err)
	}
	if fixture.Medical != nil {
		if len(fixture.Medical) != 2 {
			t.Fatal("incomplete medical reference")
		}
		for phase, previous := range map[string]bool{"initial": false, "retained": true} {
			want, exists := fixture.Medical[phase]
			got, err := policy.ReviewMedicalReserve(projection.Facts.MedicalReserve, previous, policy.DefaultMedicalReservePolicy())
			stock, known := got.Stock.Value()
			entry, _ := got.Entry.Value()
			target, _ := got.Target.Value()
			replenish, _ := got.Replenish.Value()
			if err != nil || !exists || !want.Known || !known || got.Active != want.Active || stock != want.Stock || entry != want.Entry || target != want.Recovery || replenish != want.Replenish {
				t.Fatal(phase, got, want, err)
			}
		}
	}
	upkeep, err := policy.ReviewUpkeep(projection.Facts.Upkeep, policy.UpkeepHistory{}, nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(fixture.Expected) != 4 || len(upkeep.Needs) != 4 {
		t.Fatal("incomplete native upkeep replay")
	}
	for _, need := range upkeep.Needs {
		want, ok := fixture.Expected[need.Goal]
		targets, known := need.Targets.Value()
		metric, measured := need.Metric.Value()
		if !ok || !known || !measured || !reflect.DeepEqual(targets, want.Targets) || metric != want.Metric || want.Need != domain.NeedDeficit || !need.Active {
			t.Fatal("native upkeep policy differs", need, want)
		}
	}
	ctx := context.Background()
	dbpath := filepath.Join(t.TempDir(), "native-upkeep.db")
	db, err := store.Open(ctx, dbpath)
	if err != nil {
		t.Fatal(err)
	}
	native, known := identity.NativeGeneration.Value()
	if !known {
		t.Fatal("native generation unavailable")
	}
	scope := domain.GenerationSnapshot{Colony: identity.Colony, Load: identity.Load, Map: identity.Map, Native: native, Plan: "native-upkeep-replay", Revision: 1}
	request := store.RoutineReviewRequest{Current: scope, Tick: identity.Tick, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: projection.Facts}
	active, err := db.ReviewRoutine(ctx, request)
	if err != nil {
		t.Fatal(err)
	}
	if len(active.Goals) != 23 {
		t.Fatal("incomplete maintained goals")
	}
	medicalNeed := domain.NeedRecovered
	if fixture.Medical != nil && fixture.Medical["initial"].Active {
		medicalNeed = domain.NeedDeficit
	}
	for i, binding := range active.Review.Goals {
		if fixture.Medical != nil && binding.Need == policy.MaintainMedicalReserves {
			g, err := db.LoadGoal(ctx, binding.Goal)
			if err != nil || g.Goal.Need != medicalNeed {
				t.Fatal(g, err)
			}
		}
		if want, ok := fixture.Expected[binding.Need]; ok {
			g := active.Goals[i].Goal
			if g.Need != want.Need || g.Priority != want.Priority {
				t.Fatal(g, want)
			}
		}
	}
	request.Revision = active.Review.Revision
	request.Enabled = false
	if _, err = db.ReviewRoutine(ctx, request); err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	db, err = store.Open(ctx, dbpath)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	retained, err := db.LoadRoutineReview(ctx)
	if err != nil || retained.Enabled {
		t.Fatal(retained, err)
	}
	for _, binding := range retained.Goals {
		if fixture.Medical != nil && binding.Need == policy.MaintainMedicalReserves {
			g, err := db.LoadGoal(ctx, binding.Goal)
			if err != nil || g.Goal.Need != medicalNeed {
				t.Fatal(g, err)
			}
		}
		if want, ok := fixture.Expected[binding.Need]; ok {
			g, err := db.LoadGoal(ctx, binding.Goal)
			if err != nil || g.Goal.Need != want.Need || g.Goal.Status != domain.GoalInvalidated {
				t.Fatal(g, err)
			}
		}
	}
}
