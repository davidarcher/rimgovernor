package buildingruntime

import (
	"context"
	"errors"
	"os"
	"slices"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

type routineNative struct {
	reply     *o.ColonyFactsReply
	onRead    func(context.Context)
	reads     int
	planning  bool
	pawnReply *o.ListPawnsReply
	// pawnReads and populationReads count the held-section reads (#625).
	pawnReads, populationReads int
	// identityTick, when set, pins the identity read behind the colony
	// reply's tick the way the step's fact cache serves it under a running
	// window (#662).
	identityTick *int64
	// built is the fake construction census (routine_built_census_test.go).
	built map[domain.ActionID]*o.BuildingState
	// cells is the planning window the fake serves (ReadPlanningWindow);
	// nil serves an empty window.
	cells *bridge.PlanningWindow
	// catalog is the definition catalog's rows (#1340); finished, when
	// set, is the frame's finished research.
	catalog  []*o.PlanningDefinition
	finished []string
	// buildings, pawns and things are the frame's keyed tables (#1343).
	buildings bridge.Buildings
	pawns     bridge.Pawns
	things    bridge.Things
}

func (n *routineNative) FrameTables(context.Context, *c.Identity) (bridge.Tables, error) {
	return bridge.Tables{Buildings: n.buildings, Pawns: n.pawns, Things: n.things}, nil
}

func (n *routineNative) FrameThings(context.Context, *c.Identity) (bridge.Things, error) {
	return n.things, nil
}

// thing puts row in the frame's things table and returns the reference a
// food stock carries to it.
func (n *routineNative) thing(row *o.Thing) *c.Ref {
	if n.things == nil {
		n.things = bridge.Things{}
	}
	n.things[row.Thing.GetId()] = row
	return &c.Ref{Id: row.Thing.Id}
}

// entity puts a things table row with only ref's head in the frame,
// unless the frame already holds one, and returns the reference a section
// carries to it (#1342).
func (n *routineNative) entity(ref *o.EntityRef) *c.Ref {
	if _, ok := n.things[ref.GetId()]; !ok {
		n.thing(&o.Thing{Thing: ref})
	}
	return &c.Ref{Id: ref.Id}
}

// building puts row in the frame's building table and returns the
// reference a section carries to it; a row without service or settings
// gets empty ones.
func (n *routineNative) building(row *o.BuildingState) *o.EntityRef {
	if n.buildings == nil {
		n.buildings = bridge.Buildings{}
	}
	if row.Service == nil {
		row.Service = &o.BuildingServiceState{}
	}
	if row.Settings == nil {
		row.Settings = &o.BuildingSettings{Forbidden: proto.Bool(false)}
	}
	n.buildings[row.Building.GetId()] = row
	return row.Building
}

// head puts a building row with only ref's head in the frame's building
// table, unless it already holds one, and returns the reference a section
// carries to it (#1342).
func (n *routineNative) head(ref *o.EntityRef) *c.Ref {
	if _, ok := n.buildings[ref.GetId()]; !ok {
		n.building(&o.BuildingState{Building: ref})
	}
	return &c.Ref{Id: ref.Id}
}

// pawn puts row in the frame's pawn table and returns the reference a
// section carries to it.
func (n *routineNative) pawn(row *o.PawnState) *o.EntityRef {
	if n.pawns == nil {
		n.pawns = bridge.Pawns{}
	}
	n.pawns[row.Pawn.GetId()] = row
	return &o.EntityRef{Id: row.Pawn.Id}
}

func (n *routineNative) finishedResearch() []string { return n.finished }

// DefinitionCatalog serves the fake's catalog rows under the asked load.
func (n *routineNative) DefinitionCatalog(_ context.Context, id *c.Identity) (*bridge.DefinitionCatalog, error) {
	return testCatalog(id, n.catalog...), nil
}

// testCatalog is a definition catalog of rows under id's load.
func testCatalog(id *c.Identity, rows ...*o.PlanningDefinition) *bridge.DefinitionCatalog {
	catalog := &bridge.DefinitionCatalog{LoadToken: id.GetLoadToken(), Definitions: map[string]*o.PlanningDefinition{}}
	for _, row := range rows {
		catalog.Definitions[row.GetDefinition().GetDefName()] = row
	}
	return catalog
}

// putCatalog replaces or appends catalog rows by name.
func (n *routineNative) putCatalog(rows ...*o.PlanningDefinition) {
	for _, row := range rows {
		n.catalogRow(row.GetDefinition().GetDefName())
		for i, held := range n.catalog {
			if held.GetDefinition().GetDefName() == row.GetDefinition().GetDefName() {
				n.catalog[i] = row
			}
		}
	}
}

// catalogRow is a catalog row by name, appended when absent.
func (n *routineNative) catalogRow(name string) *o.PlanningDefinition {
	for _, row := range n.catalog {
		if row.GetDefinition().GetDefName() == name {
			return row
		}
	}
	row := &o.PlanningDefinition{Definition: &o.DefinitionRef{DefName: proto.String(name)}}
	n.catalog = append(n.catalog, row)
	return row
}

// ReadPlanningWindow serves the fake's planning window whatever region is
// asked, as the frame grid answers it.
func (n *routineNative) ReadPlanningWindow(ctx context.Context, _ *c.Identity, rect policy.Rectangle) (bridge.PlanningWindow, bridge.Result, error) {
	if n.cells == nil {
		return bridge.PlanningWindow{Context: n.reply.GetObserved().GetContext(), Region: rect}, bridge.Result{}, ctx.Err()
	}
	window := *n.cells
	window.Cells = slices.Clone(n.cells.Cells)
	if window.Region == (policy.Rectangle{}) {
		window.Region = rect
	}
	return window, bridge.Result{}, ctx.Err()
}

// openCell is a visible, walkable, unroofed, unzoned outdoor cell with
// light footing and no edifice, as the frame grid reads one.
func openCell(x, z int32) policy.SiteCell {
	return policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), SupportsLight: domain.Known(true), Indoors: domain.Known(false),
		Zone: domain.Known(false), Roofed: domain.Known(false), PlayerEdifice: domain.Known(""), ClaimableRuin: domain.Known("")}
}

// roofed is c under a constructed roof.
func roofed(c policy.SiteCell) policy.SiteCell {
	c.Roofed, c.Roof = domain.Known(true), domain.Known("RoofConstructed")
	return c
}

// fixtureCells is the colony-core fixture's planning window: its one
// fertile cell at the origin.
func fixtureCells(t testing.TB) *bridge.PlanningWindow {
	t.Helper()
	cell := openCell(0, 0)
	cell.Indoors, cell.Fertility = domain.Unknown[bool](), domain.Known(1.0)
	return &bridge.PlanningWindow{Context: &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), MapId: proto.Int32(0), LoadToken: proto.String("load")}, Tick: proto.Int64(7), NativeGeneration: proto.Uint64(1)},
		Region: policy.Rectangle{Width: 1, Height: 1}, Cells: []policy.SiteCell{cell}}
}

// zonesAvailable drops the fixture's farms issue so the zone census reads.
func zonesAvailable(v *o.ColonyFactsSnapshot) {
	var issues []*o.ReadIssue
	for _, i := range v.Issues {
		if i.GetField() != "farms" {
			issues = append(issues, i)
		}
	}
	v.Issues = issues
}

// Translate the legacy colony fixture's zone data at the new list boundary.
func (n *routineNative) ReadZoneSection(ctx context.Context, _ *c.Identity) (bridge.ZonesRead, bridge.Result, error) {
	v := n.reply.GetObserved()
	out := bridge.ZonesRead{Context: v.Context, AsOf: v.Context.GetTick()}
	for _, issue := range v.Issues {
		if issue.GetField() == "farms" {
			return out, bridge.Result{}, bridge.ErrUnavailable
		}
	}
	for _, farm := range v.Farms {
		out.Rows = append(out.Rows, &o.ZoneState{Id: farm.Zone.Id, Farm: farm, FoodStorage: proto.Bool(false)})
	}
	if v.GetFoodStorage() {
		out.Rows = append(out.Rows, &o.ZoneState{Id: proto.String("storage"), FoodStorage: proto.Bool(true)})
	}
	return out, bridge.Result{}, ctx.Err()
}

func (n *routineNative) ReadRoutineFrame(ctx context.Context, id *c.Identity) (bridge.RoutineFrame, error) {
	return fakeFrame(ctx, n, id)
}

// fakeFrame is the frame a test fake serves: the colony reply's context
// and whichever section reads the fake (source, the outermost type, so its
// overrides count) offers.
func fakeFrame(ctx context.Context, source observation.ColonySource, id *c.Identity) (bridge.RoutineFrame, error) {
	colony, _, err := source.ReadColonyFacts(ctx, id, true)
	if err != nil {
		return bridge.RoutineFrame{}, err
	}
	frame := bridge.RoutineFrame{Context: colony.GetObserved().GetContext(), Colony: colony.GetObserved()}
	if frame.Tables, err = source.FrameTables(ctx, id); err != nil {
		return bridge.RoutineFrame{}, err
	}
	if s, ok := source.(interface {
		DefinitionCatalog(context.Context, *c.Identity) (*bridge.DefinitionCatalog, error)
	}); ok {
		if frame.Catalog, err = s.DefinitionCatalog(ctx, id); err != nil {
			return bridge.RoutineFrame{}, err
		}
	}
	if s, ok := source.(interface {
		ReadTemperatureRooms(context.Context, *c.Identity) (*o.ListRoomsReply, bridge.Result, error)
	}); ok {
		rooms, _, err := s.ReadTemperatureRooms(ctx, id)
		if err != nil && !errors.Is(err, bridge.ErrUnavailable) {
			return bridge.RoutineFrame{}, err
		}
		if err == nil {
			frame.Rooms = rooms.GetObserved()
			frame.RoomCells = extentCells(frame.Rooms)
		}
	}
	if s, ok := source.(interface {
		ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	}); ok {
		if frame.Emergency, _, err = s.ReadEmergency(ctx, id); err != nil {
			return bridge.RoutineFrame{}, err
		}
	}
	var ids []string
	if complete, known := frame.Emergency.Facts.ColonistsComplete.Value(); known && complete {
		for _, pawn := range frame.Emergency.Facts.Colonists {
			ids = append(ids, string(pawn.ID))
		}
	}
	if s, ok := source.(interface {
		ReadRoutinePawns(context.Context, *c.Identity, []string) (*o.ListPawnsReply, bridge.Result, error)
	}); ok && len(ids) > 0 {
		reply, _, err := s.ReadRoutinePawns(ctx, id, ids)
		if err != nil {
			return bridge.RoutineFrame{}, err
		}
		frame.Pawns = reply.GetObserved()
	}
	if s, ok := source.(interface {
		ReadRoutinePopulation(context.Context, *c.Identity) (bridge.PrisonerCensus, bridge.Result, error)
	}); ok {
		population, _, err := s.ReadRoutinePopulation(ctx, id)
		if err != nil {
			return bridge.RoutineFrame{}, err
		}
		frame.Population = &population
	}
	if s, ok := source.(RoutineResearchSource); ok {
		research, _, err := s.ReadResearch(ctx, id)
		if err != nil {
			return bridge.RoutineFrame{}, err
		}
		frame.Research = &research
	}
	if s, ok := source.(interface{ finishedResearch() []string }); ok && frame.Research == nil && s.finishedResearch() != nil {
		frame.Research = &bridge.ResearchRead{Context: frame.Context, Finished: s.finishedResearch()}
	}
	if s, ok := source.(interface {
		ReadWorldProgression(context.Context, *c.Identity, bool) (bridge.WorldProgressionRead, bridge.Result, error)
	}); ok {
		quests, _, err := s.ReadWorldProgression(ctx, id, false)
		if err != nil {
			return bridge.RoutineFrame{}, err
		}
		frame.Quests = &quests
	}
	if s, ok := source.(interface {
		ListTraders(context.Context, *c.Identity) (bridge.TradersRead, bridge.Result, error)
	}); ok {
		traders, _, err := s.ListTraders(ctx, id)
		if err != nil {
			return bridge.RoutineFrame{}, err
		}
		frame.Traders = &traders
	}
	if s, ok := source.(interface {
		ReadConstructionBuildings(context.Context, *c.Identity, []string) (*o.ListBuildingsReply, bridge.Result, error)
	}); ok {
		reply, _, err := s.ReadConstructionBuildings(ctx, id, nil)
		if err != nil && reply.GetUnavailable() == nil {
			return bridge.RoutineFrame{}, err
		}
		frame.Construction = reply.GetObserved()
	}
	if s, ok := source.(observation.ZonesNative); ok {
		zones, _, err := s.ReadZoneSection(ctx, id)
		if err != nil && !errors.Is(err, bridge.ErrUnavailable) {
			return bridge.RoutineFrame{}, err
		}
		if err == nil {
			frame.Zones = &zones
		}
	}
	return frame, nil
}

func (n *routineNative) ReadRoutinePawns(ctx context.Context, _ *c.Identity, _ []string) (*o.ListPawnsReply, bridge.Result, error) {
	n.pawnReads++
	if n.pawnReply != nil {
		return n.pawnReply, bridge.Result{}, ctx.Err()
	}
	return &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}, bridge.Result{}, ctx.Err()
}

func (n *routineNative) ReadRoutinePopulation(ctx context.Context, _ *c.Identity) (bridge.PrisonerCensus, bridge.Result, error) {
	n.populationReads++
	return bridge.PrisonerCensus{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Prisoners: domain.Known([]policy.PrisonerFacts{})}, bridge.Result{}, ctx.Err()
}

func (n *routineNative) ReadEmergency(ctx context.Context, _ *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return bridge.EmergencyObservation{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true)}}, bridge.Result{}, ctx.Err()
}

func (n *routineNative) Identity(ctx context.Context) (*l.IdentityReply, bridge.Result, error) {
	observed := proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext)
	if n.identityTick != nil {
		observed.Tick = proto.Int64(*n.identityTick)
	}
	return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: observed, Paused: proto.Bool(true)}}}, bridge.Result{}, ctx.Err()
}
func (n *routineNative) ReadColonyFacts(ctx context.Context, _ *c.Identity, planning bool) (*o.ColonyFactsReply, bridge.Result, error) {
	n.reads++
	n.planning = planning
	if n.onRead != nil {
		n.onRead(ctx)
	}
	return n.reply, bridge.Result{}, nil // A late transport may ignore cancellation.
}

func TestRoutineReviewerUsesConfiguredFieldReserve(t *testing.T) {
	t.Parallel()
	r, db, _, _, n := routineFixture(t)
	v := n.reply.GetObserved()
	v.Issues = v.Issues[1:] // Complete native farm census replaces its unavailable issue.
	v.Farms = []*o.FarmFacts{{Zone: &c.Ref{Id: proto.String("farm")}, Crop: proto.String("Plant_Rice"), EdibleCrop: proto.Bool(true), GrowingCells: proto.Uint32(73), PlantedCells: proto.Uint32(73), UsableCells: proto.Uint32(73)}}
	d := n.catalog[0]
	d.Definition.DefName = proto.String("Plant_Rice")
	d.GrowDays, d.HarvestNutrition = proto.Float64(3), proto.Float64(1)
	for _, reserve := range []float64{7, 14, 7} {
		r.policy.FoodTargetDays = reserve
		out, err := r.Step(context.Background())
		if err != nil {
			t.Fatal(err)
		}
		if !n.planning {
			t.Fatal("routine did not request crop definitions")
		}
		want := domain.NeedUnknown // Stored-food forecast remains unknown.
		if reserve == 14 {
			want = domain.NeedDeficit
		}
		for _, binding := range out.Review.Goals {
			if binding.Need != policy.EnsureFoodSupply {
				continue
			}
			g, err := db.LoadGoal(context.Background(), binding.Goal)
			if err != nil || g.Goal.Need != want {
				t.Fatal("field budget did not reach durable food need", reserve, g, err)
			}
		}
	}
}
func routineFixture(t *testing.T) (*RoutineReviewer, *store.Store, *playerFakeSession, store.ControlRequest, *routineNative) {
	t.Helper()
	p, db, session, _ := playerFixture(t)
	request := playerAcquire(t, p)
	if _, err := p.Resume(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	n := colonyCoreNative(t)
	r, err := NewRoutineReviewer(p, n, testkit.NewManualClock(time.Now()), policy.DefaultRoutinePolicy(), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	return r, db, session, request, n
}

// colonyCoreNative is the reviewer's fact source over the committed
// colony-core fixture: world colony/load/map 0 at tick 7, generation 1.
func colonyCoreNative(t *testing.T) *routineNative {
	t.Helper()
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	n := &routineNative{reply: &o.ColonyFactsReply{}}
	if err = protojson.Unmarshal(data, n.reply); err != nil {
		t.Fatal(err)
	}
	n.cells = fixtureCells(t)
	n.catalog = []*o.PlanningDefinition{{Definition: &o.DefinitionRef{DefName: proto.String("Wall")}, Stuff: proto.String("WoodLog"), ConstructionSkill: proto.Int32(0), Size: &o.MapSize{Width: proto.Uint32(1), Height: proto.Uint32(1)}, Costs: []*o.Quantity{{DefName: proto.String("WoodLog"), Units: proto.Int64(5)}}}}
	return n
}

func TestRoutineReviewerPersistsNeedsAndManualVetoesWithoutRead(t *testing.T) {
	t.Parallel()
	r, db, session, request, n := routineFixture(t)
	got, err := r.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if len(got.Goals) != 43 || got.Review.Revision != 1 || !got.Review.Enabled {
		t.Fatal(got)
	}
	for _, binding := range got.Review.Goals {
		if binding.Need == domain.GoalID(policy.EnsureFoodSupply) {
			g, err := db.LoadGoal(context.Background(), binding.Goal)
			if err != nil || g.Goal.Need != domain.NeedUnknown {
				t.Fatal("raw food became recovery", g, err)
			}
		}
	}
	request.Kind = store.PauseControl
	request.RequestID = "manual-routine"
	request.World.Load = "stale-browser"
	if _, err = r.player.Pause(context.Background(), request); err == nil {
		t.Fatal("stale browser accepted")
	}
	stored, err := db.LoadRoutineReview(context.Background())
	if err != nil || stored.Enabled || stored.Revision != 2 || n.reads != 1 || session.State().Enabled {
		t.Fatal(stored, err)
	}
	for _, binding := range stored.Goals {
		g, err := db.LoadGoal(context.Background(), binding.Goal)
		if err != nil || g.Goal.Status == domain.GoalInvalidated || stored.Veto(g.Goal) != "control paused" {
			t.Fatal(g, err)
		}
	}
}

func TestRoutineReviewerRejectsAuthorityChangesDuringRead(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"direction", "disabled", "native", "load"} {
		t.Run(change, func(t *testing.T) {
			r, db, session, _, n := routineFixture(t)
			n.onRead = func(context.Context) {
				session.mu.Lock()
				defer session.mu.Unlock()
				switch change {
				case "direction":
					session.state.Snapshot.Native++
				case "disabled":
					session.state.Enabled = false
				case "native":
					session.state.Snapshot.Native++
				case "load":
					session.state.Snapshot.Load = "new"
				}
			}
			if _, err := r.Step(context.Background()); err == nil {
				t.Fatal("late facts committed")
			}
			stored, err := db.LoadRoutineReview(context.Background())
			if err != nil || stored.Revision != 0 {
				t.Fatal(stored, err)
			}
		})
	}
}

func TestRoutineReviewerManualCancelsBlockedNativeRead(t *testing.T) {
	t.Parallel()
	r, db, session, request, n := routineFixture(t)
	entered := make(chan struct{})
	n.onRead = func(ctx context.Context) { close(entered); <-ctx.Done() }
	reviewDone := make(chan error, 1)
	go func() { _, err := r.Step(context.Background()); reviewDone <- err }()
	select {
	case <-entered:
	case <-time.After(time.Second):
		t.Fatal("read not entered")
	}
	request.Kind, request.RequestID = store.PauseControl, "manual-blocked"
	if _, err := r.player.Pause(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	if err := <-reviewDone; err == nil {
		t.Fatal("cancelled review succeeded")
	}
	stored, err := db.LoadRoutineReview(context.Background())
	if err != nil || stored.Revision != 0 || session.State().Enabled {
		t.Fatal(stored, err)
	}
}

func TestRoutineReviewerDisabledStepRetiresReviewWithoutReacquiring(t *testing.T) {
	t.Parallel()
	r, db, session, _, n := routineFixture(t)
	if _, err := r.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	if err := session.Disable(); err != nil {
		t.Fatal(err)
	}
	got, err := r.Step(context.Background())
	if err != nil || got.Review.Enabled || got.Review.Revision != 2 {
		t.Fatal(got, err)
	}
	got, err = r.Step(context.Background())
	if err != nil || got.Review.Revision != 2 || n.reads != 1 || session.acquires.Load() != 1 {
		t.Fatal(got, err)
	}
	stored, err := db.LoadRoutineReview(context.Background())
	if err != nil || stored.Enabled {
		t.Fatal(stored, err)
	}
}

type routineMedicalNative struct {
	*routineNative
	unknown bool
}

func (n *routineMedicalNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, receipt, err := n.routineNative.ReadEmergency(ctx, id)
	pawn := policy.EmergencyPawn{ID: "patient", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(true)}
	if n.unknown {
		pawn.NeedsTend = domain.Unknown[bool]()
	}
	v.Facts.Colonists = []policy.EmergencyPawn{pawn}
	return v, receipt, err
}
func TestRoutineReviewerUsesSameTickMedicalCensus(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"needs_tend", "unknown"} {
		t.Run(kind, func(t *testing.T) {
			r, db, _, _, n := routineFixture(t)
			r.native = &routineMedicalNative{routineNative: n, unknown: kind == "unknown"}
			got, err := r.Step(context.Background())
			if err != nil {
				t.Fatal(err)
			}
			// A deficit opens the CriticalMedical incident (#1020); an
			// unknown census opens none.
			binding, ok := got.Review.Incident(policy.CriticalMedicine)
			if kind == "unknown" {
				if ok {
					t.Fatal("unknown census opened an incident", binding)
				}
				return
			}
			if !ok || binding.Need != domain.NeedDeficit {
				t.Fatal("medical incident missing", got.Review.Incidents)
			}
			if _, err := db.LoadIncident(context.Background(), binding.Incident); err != nil {
				t.Fatal(err)
			}
		})
	}
}

func TestRoutineFoodAttrsCarryRunwayThresholdsAndCalendar(t *testing.T) {
	calendar := policy.Calendar{Season: "Fall", DayOfYear: 33, GrowingDays: 30, GrowingDaysRemaining: 1, NonGrowingDays: 33, Sowing: true}
	facts := policy.RoutineFacts{FoodDays: domain.Known(38.5), Calendar: domain.Known(calendar)}
	seasonal := policy.DefaultRoutinePolicy().Seasonal(facts.Calendar, facts.DisasterConditions)
	attrs := routineFoodAttrs(facts, seasonal)
	got := map[string]any{}
	for i := 0; i+1 < len(attrs); i += 2 {
		got[attrs[i].(string)] = attrs[i+1]
	}
	if got["food_days"] != 38.5 || got["food_min_days"] != seasonal.FoodMinDays || got["food_target_days"] != seasonal.FoodTargetDays {
		t.Fatalf("food attrs: %v", got)
	}
	if got["season"] != "Fall" || got["day_of_year"] != int64(33) || got["growing_days_remaining"] != 1.0 || got["growing_days_until"] != 0.0 || got["non_growing_days"] != 33.0 {
		t.Fatalf("calendar attrs: %v", got)
	}
	if seasonal.FoodMinDays <= policy.DefaultRoutinePolicy().FoodMinDays {
		t.Fatalf("seasonal minimum not widened: %v", seasonal.FoodMinDays)
	}
	unknown := routineFoodAttrs(policy.RoutineFacts{}, policy.DefaultRoutinePolicy())
	for i := 0; i < len(unknown); i += 2 {
		if key := unknown[i].(string); key == "food_days" || key == "season" {
			t.Fatalf("unknown facts produced %s", key)
		}
	}
}

func (n *routineMedicalNative) ReadRoutineFrame(ctx context.Context, id *c.Identity) (bridge.RoutineFrame, error) {
	return fakeFrame(ctx, n, id)
}

// extentCells stands in for the frame grid: each test room fills its
// extents.
func extentCells(v *o.RoomsSnapshot) map[string][]domain.Cell {
	out := map[string][]domain.Cell{}
	for _, room := range v.GetRooms() {
		lo, hi := room.GetExtents().GetMinimum(), room.GetExtents().GetMaximum()
		if lo == nil || hi == nil {
			continue
		}
		for z := lo.GetZ(); z <= hi.GetZ(); z++ {
			for x := lo.GetX(); x <= hi.GetX(); x++ {
				out[room.GetId()] = append(out[room.GetId()], domain.Cell{X: x, Z: z})
			}
		}
	}
	return out
}
