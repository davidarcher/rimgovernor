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

func TestNativeRoutineDisasterReplay(t *testing.T) {
	directory := os.Getenv("RIMGOVERNOR_NATIVE_DISASTER_CAPTURE")
	if directory == "" {
		t.Skip("requires native compound disaster planning capture")
	}
	data, err := os.ReadFile(filepath.Join(directory, "work-colony.json"))
	if err != nil {
		t.Fatal(err)
	}
	colony := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, colony); err != nil {
		t.Fatal(err)
	}
	id, err := contextIdentity(colony.GetObserved().GetContext())
	if err != nil {
		t.Fatal(err)
	}
	projection, err := DecodeColony(colony, id)
	if err != nil {
		t.Fatal(err)
	}
	var reference struct {
		Reference struct {
			Work    []policy.RecoveryWork
			Damaged []string
		}
	}
	data, err = os.ReadFile(filepath.Join(directory, "disaster-reference.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err = json.Unmarshal(data, &reference); err != nil {
		t.Fatal(err)
	}
	pending, err := policy.RecoveryPending(projection.Facts.RecoveryBuildings)
	rows, known := pending.Value()
	if err != nil || !known || len(rows) == 0 || !reflect.DeepEqual(rows, reference.Reference.Work) {
		t.Fatal(rows, known, err)
	}
	path := filepath.Join(t.TempDir(), "disaster.sqlite")
	journal, err := store.Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	native, known := id.NativeGeneration.Value()
	if !known {
		t.Fatal("missing native generation")
	}
	request := store.RoutineReviewRequest{Current: domain.GenerationSnapshot{Colony: id.Colony, Load: id.Load, Map: id.Map, Native: native, Plan: "disaster-native-replay", Revision: 1, Direction: 1}, Tick: id.Tick, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: projection.Facts}
	result, err := journal.ReviewRoutine(context.Background(), request)
	if err != nil {
		t.Fatal(err)
	}
	h := result.Review.Disaster
	if h == nil || h.Phase != policy.DisasterDisrupted || !reflect.DeepEqual(h.Work, rows) || !reflect.DeepEqual(h.Damaged, reference.Reference.Damaged) {
		t.Fatal(h)
	}
	request.Revision = result.Review.Revision
	request.Enabled = false
	request.Tick++
	manual, err := journal.ReviewRoutine(context.Background(), request)
	if err != nil || !reflect.DeepEqual(manual.Review.Disaster, h) {
		t.Fatal(manual, err)
	}
	journal.Close()
	journal, err = store.Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	loaded, err := journal.LoadRoutineReview(context.Background())
	if err != nil || loaded.Enabled || !reflect.DeepEqual(loaded.Disaster, h) {
		t.Fatal(loaded, err)
	}
}
