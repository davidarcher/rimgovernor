package buildingruntime

import (
	"context"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// countingSurvey counts the definition reads the layout survey makes.
type countingSurvey struct {
	observation.RoundsSource
	reads *int
}

func (n countingSurvey) DefinitionCatalog(context.Context, *c.Identity) (*bridge.DefinitionCatalog, error) {
	*n.reads++
	return recordedcatalog.Decode()
}

// openWindow fills projection's planning window with an n x n open map and the
// game's roof support radius (a plan without it is skipped as facts_unknown).
func openWindow(projection *observation.ColonyProjection, n int32) {
	projection.Bounds = policy.Bounds{Width: n, Height: n}
	projection.Region = policy.Rectangle{Width: n, Height: n}
	projection.RoofSupport = 6
	projection.Cells = nil
	for z := int32(0); z < n; z++ {
		for x := int32(0); x < n; x++ {
			projection.Cells = append(projection.Cells, policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Fertility: domain.Known(1.0), BaseTerrain: domain.Known("Soil")})
		}
	}
}

// Every layout trigger is hourly. An unchanged colony reads the
// survey at most once an hour and replans nothing; a new pawn or a new
// tier replans at the next hourly review.
func TestLayoutTriggersHourly(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s, _ := schedulerFixture(t)
	ctx := context.Background()
	reads := 0
	r := &Rounder{player: s.player, native: countingSurvey{reads: &reads}}
	snapshot := s.player.session.State().Snapshot
	projection := observation.ColonyProjection{Identity: observation.Identity{Colony: snapshot.Colony, Map: snapshot.Map, Load: snapshot.Load}}
	openWindow(&projection, 120)
	withRecordedCatalog(t, r, &projection)
	projection.Facts.Colonists = domain.Known(int64(3))
	projection.TechTier = domain.Known(policy.TechTierCamp)
	review := func(tick domain.Tick) {
		t.Helper()
		projection.Identity.Tick = tick
		if err := r.reviewLayoutPlan(ctx, snapshot, &projection); err != nil {
			t.Fatal(err)
		}
	}
	planTick := func() domain.Tick {
		t.Helper()
		layout, ok, err := r.layoutPlan(ctx, snapshot, projection.Identity.Tick)
		if err != nil || !ok {
			t.Fatal("no plan", err)
		}
		return layout.Tick
	}

	review(100)
	if reads != 1 {
		t.Fatal("no plan derived", reads)
	}
	// Unchanged: the first hourly check replans once (inputs unknown), then
	// the survey is read at most once an hour and replans nothing.
	review(100 + layoutReplanEvery)
	settled := planTick()
	for tick := 200 + layoutReplanEvery; tick < 100+5*layoutReplanEvery; tick += 500 {
		review(tick)
	}
	if reads > 1+4 {
		t.Fatal("unchanged colony read the survey more than hourly", reads)
	}
	if planTick() != settled {
		t.Fatal("unchanged colony replanned")
	}

	// A new pawn replans at the next hourly review.
	projection.Facts.Colonists = domain.Known(int64(4))
	before := reads
	review(100 + 6*layoutReplanEvery)
	if reads != before+1 || r.planPawns != 4 {
		t.Fatal("a new pawn did not replan", reads, r.planPawns)
	}

	// A tier change replans at the next hourly review.
	grown := r.planGrownFor
	projection.TechTier = domain.Known(policy.TechTierCamp + 1)
	review(100 + 6*layoutReplanEvery + 100)
	if r.planGrownFor != grown {
		t.Fatal("a tier change replanned before the hour")
	}
	review(100 + 7*layoutReplanEvery)
	if r.planGrownFor == grown {
		t.Fatal("a tier change did not replan")
	}
}

// A recorded replan changes the plan, and the plan is among the inputs the next
// review keys on: the inputs are read again over the recorded plan, so an
// unchanged colony does not replan on every consecutive hourly review and the
// tiler does not renumber the rooms it has not built (#3037).
func TestLayoutDoesNotReplanAnUnchangedColonyEveryHour(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s, _ := schedulerFixture(t)
	ctx := context.Background()
	reads := 0
	r := &Rounder{player: s.player, native: countingSurvey{reads: &reads}}
	snapshot := s.player.session.State().Snapshot
	projection := observation.ColonyProjection{Identity: observation.Identity{Colony: snapshot.Colony, Map: snapshot.Map, Load: snapshot.Load}}
	openWindow(&projection, 120)
	withRecordedCatalog(t, r, &projection)
	projection.Facts.Colonists = domain.Known(int64(3))
	projection.TechTier = domain.Known(policy.TechTierCamp)
	review := func(hour int64) store.LayoutPlanRecord {
		t.Helper()
		projection.Identity.Tick = 100 + domain.Tick(hour)*layoutReplanEvery
		if err := r.reviewLayoutPlan(ctx, snapshot, &projection); err != nil {
			t.Fatal(err)
		}
		layout, ok, err := r.layoutPlan(ctx, snapshot, projection.Identity.Tick)
		if err != nil || !ok {
			t.Fatal("no plan", err)
		}
		return layout
	}
	review(0)
	// The first review over a derived plan has no inputs to compare and may
	// replan once; from then on the same colony keeps the same plan.
	settled := review(1)
	for hour := int64(2); hour <= 6; hour++ {
		got := review(hour)
		if got.Tick != settled.Tick || !reflect.DeepEqual(got.Plan.Rooms, settled.Plan.Rooms) {
			t.Fatalf("hour %d replanned an unchanged colony: plan tick %d -> %d", hour, settled.Tick, got.Tick)
		}
	}
}

func TestLayoutReasons(t *testing.T) {
	if got := layoutReasons(map[string]bool{"tomb": true, "pawns": true, "terrain": false}); got != "pawns,tomb" {
		t.Fatal(got)
	}
}

// withRecordedCatalog gives projection the station table, item facts and piece
// shapes the live read takes from the game's definition catalog; a plan without
// them is skipped as facts_unknown.
func withRecordedCatalog(t *testing.T, r *Rounder, projection *observation.ColonyProjection) {
	t.Helper()
	catalog, err := recordedcatalog.Decode()
	if err != nil {
		t.Fatal(err)
	}
	if projection.Stations, err = catalog.StationCatalog(); err != nil {
		t.Fatal(err)
	}
	if projection.Facts.Items, err = catalog.ItemFacts(); err != nil {
		t.Fatal(err)
	}
	if projection.Shapes, err = catalog.PieceShapes(); err != nil {
		t.Fatal(err)
	}
	dining, err := catalog.DiningFurniture()
	if err != nil {
		t.Fatal(err)
	}
	projection.Rooms = domain.Known(policy.RoomObservation{Dining: dining, Shapes: projection.Shapes})
	// A replan needs every census read: a colony with nothing built, nobody
	// hurt or held, no consumer yet, no order on the ledger and a demand for
	// the one thing it will build.
	projection.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true})
	projection.Facts.MedicalPawns = domain.Known([]policy.CarePawn{})
	projection.Facts.Prisoners = domain.Known([]policy.PrisonerFacts{})
	projection.WorkPawns = domain.Known([]policy.WorkPawn{})
	projection.FoodSupply = domain.Known(policy.FoodSupply{Complete: domain.Known(true)})
	r.ledger.view = policy.NewLedgerView(policy.LedgerViewInactive, 0)
	r.stockDemand = map[policy.Resource]int64{"Steel": 100}
}
