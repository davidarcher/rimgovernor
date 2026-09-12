package observation

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"sort"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

// Uses a backup of the real, joined service journal. Fixture IDs cannot supply
// ownership; completed native transitions must survive Store.Open and review.
func TestNativeFacilityUpkeepReplay(t *testing.T) {
	root := os.Getenv("RIMBOT_NATIVE_FACILITY_REPLAY")
	if root == "" {
		t.Skip("requires native --shelter-methods --facility-upkeep output")
	}
	data, err := os.ReadFile(filepath.Join(root, "facility-replay.json"))
	if err != nil {
		t.Fatal(err)
	}
	var fixture struct {
		Colony, Buildings json.RawMessage
		IDs               []string
		Expected          struct{ Home, Stone, Owned []string }
	}
	if err = json.Unmarshal(data, &fixture); err != nil {
		t.Fatal(err)
	}
	var colony o.ColonyFactsReply
	var buildings o.ListBuildingsReply
	if err = protojson.Unmarshal(fixture.Colony, &colony); err != nil {
		t.Fatal(err)
	}
	if err = protojson.Unmarshal(fixture.Buildings, &buildings); err != nil {
		t.Fatal(err)
	}
	identity, err := contextIdentity(colony.GetObserved().GetContext())
	if err != nil {
		t.Fatal(err)
	}
	projection, err := DecodeColony(&colony, identity)
	if err != nil {
		t.Fatal(err)
	}
	if err = bridge.ValidateConstructionBuildings(buildings.GetObserved(), colony.GetObserved().Context.Identity, fixture.IDs); err != nil {
		t.Fatal(err)
	}
	observed, err := contextIdentity(buildings.GetObserved().GetContext())
	if err != nil || !sameColonyBoundary(observed, identity) {
		t.Fatal("ownership query outside colony tick", err)
	}
	current, err := constructionBuildings(buildings.GetObserved(), fixture.IDs)
	if err != nil {
		t.Fatal(err)
	}
	data, err = os.ReadFile(filepath.Join(root, "facility-journal.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "facility.sqlite")
	if err = os.WriteFile(path, data, 0600); err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	db, err := store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	retained, err := db.LoadRoutineReview(ctx)
	if err != nil || retained.Enabled {
		t.Fatal(retained, err)
	}
	claims, err := db.ConstructionClaims(ctx, retained.Snapshot, identity.Tick)
	if err != nil {
		t.Fatal(err)
	}
	owned, err := policy.OwnedConstructions(claims, current)
	if err != nil {
		t.Fatal(err)
	}
	rows, known := owned.Value()
	ids := []string{}
	for _, row := range rows {
		ids = append(ids, row.Identity.Current)
	}
	sort.Strings(ids)
	if !known || len(ids) != 35 || !reflect.DeepEqual(ids, fixture.Expected.Owned) {
		t.Fatal("native ownership lost", ids, fixture.Expected.Owned)
	}
	home, err := policy.ReviewHomeCoverage(owned, domain.Known([]policy.OwnedStockpile{}), projection.Facts.HomeCoverage)
	homes, known := home.Value()
	if err != nil || !known {
		t.Fatal(home, err)
	}
	ids = []string{}
	excluded := false
	for _, row := range homes {
		ids = append(ids, row.ID)
		n, _ := row.Excluded.Value()
		excluded = excluded || n > 0
	}
	if !excluded || len(ids) == 0 || !reflect.DeepEqual(ids, fixture.Expected.Home) {
		t.Fatal("player exclusions or native targets lost", homes)
	}
	stone, err := policy.ReviewStoneShell(owned, projection.Facts.StoneStructures)
	stones, known := stone.Value()
	if err != nil || !known || len(stones) != 31 || !reflect.DeepEqual(stones, fixture.Expected.Stone) {
		t.Fatal(stones, err)
	}
	projection.Facts.CurrentConstruction = current
	request := store.RoutineReviewRequest{Revision: retained.Revision, WorkPreferenceRevision: retained.WorkPreferenceRevision, Current: retained.Snapshot, Tick: identity.Tick, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: projection.Facts}
	active, err := db.ReviewRoutine(ctx, request)
	if err != nil {
		t.Fatal(err)
	}
	assertNeeds := func(review store.RoutineReview, want domain.NeedState) {
		t.Helper()
		count := 0
		for _, binding := range review.Goals {
			if binding.Need != policy.MaintainHomeCoverage && binding.Need != policy.MaintainStoneShell {
				continue
			}
			g, err := db.LoadGoal(ctx, binding.Goal)
			if err != nil || g.Goal.Need != want {
				t.Fatal(g, want, err)
			}
			count++
		}
		if count != 2 {
			t.Fatal("facility goals missing")
		}
	}
	assertNeeds(active.Review, domain.NeedDeficit)
	request.Revision = active.Review.Revision
	request.Facts.CurrentConstruction = domain.Unknown[policy.CurrentConstruction]()
	active, err = db.ReviewRoutine(ctx, request)
	if err != nil {
		t.Fatal(err)
	}
	assertNeeds(active.Review, domain.NeedUnknown)
	if !active.Review.Latches.HomeCoverage || !active.Review.Latches.StoneShell {
		t.Fatal("unknown query erased native history")
	}
	request.Revision, request.Enabled = active.Review.Revision, false
	if _, err = db.ReviewRoutine(ctx, request); err != nil {
		t.Fatal(err)
	}
	db.Close()
	db, err = store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	retained, err = db.LoadRoutineReview(ctx)
	if err != nil || retained.Enabled || !retained.Latches.HomeCoverage || !retained.Latches.StoneShell {
		t.Fatal(retained, err)
	}
	assertNeeds(retained, domain.NeedUnknown)
}
