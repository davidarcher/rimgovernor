package observation

import (
	"context"
	"encoding/json"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"math"
	"os"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

// The scenario emits this same-tick native/reference pair after ordinary comfort
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
		Sleeping *struct {
			Targets []policy.SleepingTarget
			Uses    []policy.SleepingUse
		}
		Animals *struct {
			Containment []policy.PawnID
			// Feed are the race groups whose herd feed reserve is short.
			Feed []policy.AnimalFeedGroup
		}
		Medical map[string]struct {
			Known, Active                     bool
			Stock, Entry, Recovery, Replenish int64
		}
		Colony   json.RawMessage
		Expected map[policy.ConcernID]struct {
			Need     domain.Finding
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
	projection, err := DecodeColony(&reply, identity, bridge.Tables{})
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
	if fixture.Animals != nil {
		animals, known := projection.Facts.AnimalUpkeep.Animals.Value()
		if !known || len(animals) < 3 {
			t.Fatal("native animal fixture missing", animals)
		}
		got, err := policy.ReviewAnimalUpkeep(projection.Facts.AnimalUpkeep, policy.AnimalUpkeepHistory{}, policy.DefaultFoodReserveDays)
		containment, ck := got.Containment.Value()
		feed, fk := got.Feed.Value()
		if err != nil || !ck || !fk || !reflect.DeepEqual(containment, fixture.Animals.Containment) || len(feed) != len(fixture.Animals.Feed) {
			t.Fatal(got, fixture.Animals.Feed, err)
		}
		for i, row := range feed {
			want := fixture.Animals.Feed[i]
			if row.Definition != want.Definition || !reflect.DeepEqual(row.Animals, want.Animals) || math.Abs(row.DeficitNutrition-want.DeficitNutrition) > 1e-6 {
				t.Fatal(row, want)
			}
		}
	}
	if fixture.Sleeping != nil {
		got, err := policy.ReviewSleeping(projection.Facts.Sleeping, policy.SleepingHistory{}, identity.Tick)
		targets, known := got.Targets.Value()
		if err != nil || !known || !reflect.DeepEqual(targets, fixture.Sleeping.Targets) || !reflect.DeepEqual(got.History.Uses, fixture.Sleeping.Uses) {
			t.Fatal(got, fixture.Sleeping, err)
		}
	}
	upkeep, err := policy.ReviewUpkeep(projection.Facts.Upkeep, policy.UpkeepHistory{}, nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(fixture.Expected) != 5 || len(upkeep.Needs) != 5 {
		t.Fatal("incomplete native upkeep replay")
	}
	for _, need := range upkeep.Needs {
		want, ok := fixture.Expected[need.Goal]
		targets, known := need.Targets.Value()
		metric, measured := need.Metric.Value()
		if !ok || !known || !measured || !reflect.DeepEqual(targets, want.Targets) || metric != want.Metric || want.Need != domain.FindingUnmet || !need.Active {
			t.Fatal("native upkeep policy differs", need, want)
		}
	}
	ctx := context.Background()
	dbpath := storetest.Path(t)
	db, err := store.Open(ctx, dbpath)
	if err != nil {
		t.Fatal(err)
	}
	native, known := identity.NativeGeneration.Value()
	if !known {
		t.Fatal("native generation unavailable")
	}
	scope := domain.GenerationSnapshot{Colony: identity.Colony, Load: identity.Load, Map: identity.Map, Native: native, Plan: "native-upkeep-replay", Revision: 1}
	request := store.RoundsRequest{Current: scope, Tick: identity.Tick, Enabled: true, Policy: policy.DefaultRoundsPolicy(), Facts: projection.Facts}
	active, err := db.ReviewRounds(ctx, request)
	if err != nil {
		t.Fatal(err)
	}
	if len(active.Goals) != 28 {
		t.Fatal("incomplete maintained goals")
	}
	medicalNeed := domain.FindingMet
	if fixture.Medical != nil && fixture.Medical["initial"].Active {
		medicalNeed = domain.FindingUnmet
	}
	animalNeeds := map[policy.ConcernID]domain.Finding{}
	if fixture.Sleeping != nil {
		animalNeeds[policy.MaintainHousing] = domain.FindingMet
		if len(fixture.Sleeping.Targets) > 0 {
			animalNeeds[policy.MaintainHousing] = domain.FindingUnmet
		}
	}
	if fixture.Animals != nil {
		animalNeeds[policy.MaintainAnimalContainment] = domain.FindingMet
		animalNeeds[policy.MaintainAnimalFeed] = domain.FindingMet
		if len(fixture.Animals.Containment) > 0 {
			animalNeeds[policy.MaintainAnimalContainment] = domain.FindingUnmet
		}
		if len(fixture.Animals.Feed) > 0 {
			animalNeeds[policy.MaintainAnimalFeed] = domain.FindingUnmet
		}
	}
	for i, binding := range active.Review.Goals {
		if want, ok := animalNeeds[binding.Need]; ok && active.Goals[i].Standard.Finding != want {
			t.Fatal(active.Goals[i], want)
		}
		if fixture.Medical != nil && binding.Need == policy.MaintainMedicalReserves {
			g, err := db.LoadStandard(ctx, binding.Goal)
			if err != nil || g.Standard.Finding != medicalNeed {
				t.Fatal(g, err)
			}
		}
		if want, ok := fixture.Expected[binding.Need]; ok {
			g := active.Goals[i].Standard
			if g.Finding != want.Need || g.Priority != want.Priority {
				t.Fatal(g, want)
			}
		}
	}
	request.Revision = active.Review.Revision
	request.Enabled = false
	if _, err = db.ReviewRounds(ctx, request); err != nil {
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
	retained, err := db.LoadRounds(ctx)
	if err != nil || retained.Enabled {
		t.Fatal(retained, err)
	}
	for _, binding := range retained.Goals {
		if want, ok := animalNeeds[binding.Need]; ok {
			g, err := db.LoadStandard(ctx, binding.Goal)
			if err != nil || g.Standard.Finding != want || g.Standard.Status != domain.StandardVoided {
				t.Fatal(g, want, err)
			}
		}
		if fixture.Medical != nil && binding.Need == policy.MaintainMedicalReserves {
			g, err := db.LoadStandard(ctx, binding.Goal)
			if err != nil || g.Standard.Finding != medicalNeed {
				t.Fatal(g, err)
			}
		}
		if want, ok := fixture.Expected[binding.Need]; ok {
			g, err := db.LoadStandard(ctx, binding.Goal)
			if err != nil || g.Standard.Finding != want.Need || g.Standard.Status != domain.StandardVoided {
				t.Fatal(g, err)
			}
		}
	}
}
