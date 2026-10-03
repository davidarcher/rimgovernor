package buildingruntime

import (
	"compress/gzip"
	"io"
	"os"
	"reflect"
	"slices"
	"sync"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// Snapshot tests (#747): each replays a routine review recorded from the
// native case it replaced (docs/developers/testing/colony-snapshots.md) and
// asserts the planner decision that case asserted live. Every recording
// here was taken at the commit that deleted its case.

// loadRecorded loads testdata/<name>.json.gz and checks the replayed
// review still latches what the live one did.
func loadRecorded(t *testing.T, name string) snapshot.Routine {
	t.Helper()
	r, err := snapshot.Load("testdata/" + name + ".json.gz")
	if err != nil {
		t.Fatal(err)
	}
	if r.Projection == nil || r.Review == nil {
		t.Fatalf("%s: recorded without its projection or review; re-record it", name)
	}
	needs, err := r.Detect()
	if err != nil {
		t.Fatal(err)
	}
	// The journal stamps when refrigeration latched after detection.
	needs.Latches.RefrigerationSince = r.Review.Latches.RefrigerationSince
	// A recording from before #950 carries no floor rows in its flooring
	// census: the traffic tier cannot price a floor on replay, so its latch
	// is left out of the comparison until the recording is re-recorded.
	if v, known := r.Facts.Upkeep.Flooring.Value(); known && v.Floors == nil {
		drop := func(keys []string) []string {
			return slices.DeleteFunc(append([]string{}, keys...), func(k string) bool { return k == "traffic" })
		}
		r.Review.Latches.Flooring, needs.Latches.Flooring = drop(r.Review.Latches.Flooring), drop(needs.Latches.Flooring)
	}
	if !reflect.DeepEqual(needs.Latches, r.Review.Latches) {
		t.Fatalf("%s: replayed latches %+v, recorded %+v", name, needs.Latches, r.Review.Latches)
	}
	// The catalog-derived power rows are not in a recording: read them from
	// the recorded planning catalog.
	r.Projection.PowerSources, r.Projection.PowerBattery = recordedPowerRows(t)
	return r
}

var recordedPower struct {
	once    sync.Once
	sources map[string]policy.PowerSourceProfile
	battery policy.PowerBattery
	err     error
}

// recordedPowerRows are the power source profiles and the battery of the
// planning catalog recorded from the game (observation/testdata).
func recordedPowerRows(t *testing.T) (map[string]policy.PowerSourceProfile, policy.PowerBattery) {
	t.Helper()
	recordedPower.once.Do(func() {
		file, err := os.Open("../observation/testdata/planning_catalog.pb.gz")
		if err != nil {
			recordedPower.err = err
			return
		}
		defer file.Close()
		zr, err := gzip.NewReader(file)
		if err != nil {
			recordedPower.err = err
			return
		}
		data, err := io.ReadAll(zr)
		if err != nil {
			recordedPower.err = err
			return
		}
		wire := &o.DefinitionCatalog{}
		if recordedPower.err = proto.Unmarshal(data, wire); recordedPower.err != nil {
			return
		}
		catalog, err := bridge.DecodeDefinitionCatalog(wire, wire.GetContext().GetIdentity())
		if err != nil {
			recordedPower.err = err
			return
		}
		if recordedPower.sources, recordedPower.err = catalog.PowerSources(); recordedPower.err != nil {
			return
		}
		recordedPower.battery, recordedPower.err = catalog.PowerBattery(policy.BatteryDefinition)
	})
	if recordedPower.err != nil {
		t.Fatal(recordedPower.err)
	}
	return recordedPower.sources, recordedPower.battery
}

// recordedPlanner is goal's building planner over the recording's policy,
// with no native source: select* only read the projection.
func recordedPlanner(r snapshot.Routine, goal policy.GoalID) *RoutineBuildingPlanner {
	return &RoutineBuildingPlanner{reviewer: &RoutineReviewer{policy: r.Policy}, goal: goal}
}

// loadStep loads testdata/<name>.json.gz, a planner step's own colony read
// (#794), for a test to replay a select* over in place of the review's.
func loadStep(t *testing.T, name string, goal policy.GoalID) snapshot.Step {
	t.Helper()
	s, err := snapshot.LoadStep("testdata/" + name + ".json.gz")
	if err != nil {
		t.Fatal(err)
	}
	if s.Goal != goal {
		t.Fatalf("%s: recorded %s's step, want %s", name, s.Goal, goal)
	}
	return s
}

// clean/separation: the kitchen holds both a stove and the only butcher
// spot. The food-supply step reads rooms (the review does not), sees every
// butcher bench share a room with cooking and admits a separated
// ButcherSpot, which the placement keeps out of the protected kitchen;
// over the review's projection alone the selector answers
// existing_facility. Recorded at ecef484fd plus this change (review tick 28157, step tick
// 34157).
func TestSnapshotCleanSeparationAdmitsSeparatedSpot(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "clean-separation-colocated")
	step := loadStep(t, "clean-separation-step-butcher", policy.EnsureFoodSupply)
	planner := recordedPlanner(r, policy.EnsureFoodSupply)
	planner.goal, planner.definition = policy.MaintainButcherSpot, "ButcherSpot"
	benches, known := step.Projection.ButcheringBenches.Value()
	if !known || len(benches) == 0 || !butchersAllColocated(benches, step.Projection.Rooms) {
		t.Fatalf("step read: benches %+v, want every butcher bench in the kitchen", benches)
	}
	if _, method, reason := planner.selection(step.Projection); method != "butcher-spot-separated" {
		t.Fatalf("step read: method %q reason %q, want butcher-spot-separated", method, reason)
	}
	if _, method, reason := planner.selection(*r.Projection); reason != BuildingExistingFacility {
		t.Fatalf("review projection: method %q reason %q, want %s", method, reason, BuildingExistingFacility)
	}
}

// light/outage: an unpowered StandingLamp in reach of the dark stove. The
// review latches the stove, and the lighting planner holds for the power
// family (lamp_power_needed) instead of doubling up with a torch.
func TestSnapshotLightOutageHoldsForPower(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "light-outage-unpowered-lamp")
	if len(r.Review.Latches.Lighting) != 1 {
		t.Fatalf("review latched %v, want the one dark stove", r.Review.Latches.Lighting)
	}
	resolved, reason, err := recordedPlanner(r, policy.MaintainLighting).selectLighting(*r.Projection, r.Review.Latches)
	if err != nil || resolved != nil || reason != awaitingMethod(policy.LightingPowerNeeded) {
		t.Fatalf("lighting: resolved %v reason %q err %v, want %s", resolved != nil, reason, err, policy.LightingPowerNeeded)
	}
}

// light/fungus: the dark stove's room grows a cave plant that dies to
// light. The census marks its work cell light-sensitive, the review never
// latches it and the lighting planner has no deficit to answer.
func TestSnapshotLightFungusNeverLatches(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "light-fungus-protected-room")
	census, known := r.Facts.Upkeep.Lighting.Value()
	if !known || len(census.WorkCells) != 1 || !census.WorkCells[0].LightSensitive {
		t.Fatalf("lighting census %+v: want the one light-sensitive work cell", census)
	}
	if len(r.Review.Latches.Lighting) != 0 {
		t.Fatalf("review latched %v in a protected fungus room", r.Review.Latches.Lighting)
	}
	resolved, reason, err := recordedPlanner(r, policy.MaintainLighting).selectLighting(*r.Projection, r.Review.Latches)
	if err != nil || resolved != nil || reason != BuildingReasonNoDeficit {
		t.Fatalf("lighting: resolved %v reason %q err %v, want %s", resolved != nil, reason, err, BuildingReasonNoDeficit)
	}
}

// light/partial: a lit torch at the far end of a wider room reaches the
// stove's cell only below lit, from beyond the placement radius. The review
// latches the stove anyway, and the planner admits a TorchLamp of its own
// on cells within the placement radius of the dark cell, never on it.
func TestSnapshotLightPartialAdmitsOwnLamp(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "light-partial-far-torch")
	census, known := r.Facts.Upkeep.Lighting.Value()
	if !known || len(census.WorkCells) != 1 || len(census.Lamps) != 1 || !census.Lamps[0].Lit {
		t.Fatalf("lighting census %+v: want one dark work cell and the far lit torch", census)
	}
	work := census.WorkCells[0]
	if len(r.Review.Latches.Lighting) != 1 || r.Review.Latches.Lighting[0] != work.Bench {
		t.Fatalf("review latched %v, want %s", r.Review.Latches.Lighting, work.Bench)
	}
	resolved, reason, err := recordedPlanner(r, policy.MaintainLighting).selectLighting(*r.Projection, r.Review.Latches)
	if err != nil || resolved == nil {
		t.Fatalf("lighting: reason %q err %v, want a build", reason, err)
	}
	if resolved.definition != "TorchLamp" || len(resolved.lighting.Cells) == 0 {
		t.Fatalf("lighting admits %s on %v, want a TorchLamp", resolved.definition, resolved.lighting.Cells)
	}
	radius := r.Policy.Lighting.PlacementRadius
	for _, c := range resolved.lighting.Cells {
		if c == work.Cell || max(abs32(c.X-work.Cell.X), abs32(c.Z-work.Cell.Z)) > radius {
			t.Fatalf("candidate %v is the work cell %v or beyond radius %d", c, work.Cell, radius)
		}
	}
}

// light/repair: the dark room lit by the admitted lamp (tick 10460) is
// released; once the lamp is removed (the layout change) the restarted
// controller's review re-latches the stove from the measured dark census
// (tick 15982) and the planner admits a replacement TorchLamp.
func TestSnapshotLightRepairRelatchesAfterLampRemoved(t *testing.T) {
	t.Parallel()
	lit := loadRecorded(t, "light-repair-lit")
	if len(lit.Review.Latches.Lighting) != 0 {
		t.Fatalf("lit room still latched: %v", lit.Review.Latches.Lighting)
	}
	if _, reason, err := recordedPlanner(lit, policy.MaintainLighting).selectLighting(*lit.Projection, lit.Review.Latches); err != nil || reason != BuildingReasonNoDeficit {
		t.Fatalf("lit room: reason %q err %v, want %s", reason, err, BuildingReasonNoDeficit)
	}
	dark := loadRecorded(t, "light-repair-lamp-removed")
	census, known := dark.Facts.Upkeep.Lighting.Value()
	if !known || len(census.Lamps) != 0 || len(dark.Review.Latches.Lighting) != 1 {
		t.Fatalf("after removal: census %+v latches %v, want no lamp and the stove latched", census, dark.Review.Latches.Lighting)
	}
	resolved, reason, err := recordedPlanner(dark, policy.MaintainLighting).selectLighting(*dark.Projection, dark.Review.Latches)
	if err != nil || resolved == nil || resolved.definition != "TorchLamp" {
		t.Fatalf("replacement: resolved %v reason %q err %v, want a TorchLamp build", resolved != nil, reason, err)
	}
}

// selectRecordedPower is the power planner's choice over a recording.
func selectRecordedPower(t *testing.T, r snapshot.Routine) *RoutineBuildingPlanner {
	t.Helper()
	resolved, reason, err := recordedPlanner(r, policy.EnsureBasicPower).selectPower(*r.Projection, nil)
	if err != nil || resolved == nil {
		t.Fatalf("power: reason %q err %v, want a method", reason, err)
	}
	return resolved
}

// power/geothermal: a lamp, no generator, a free steam geyser in reach and
// GeothermalPower researched. The power planner raises a
// GeothermalGenerator anchored on the geyser ahead of every other
// generator.
func TestSnapshotPowerGeothermalOnTheGeyser(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "power-geothermal-free-geyser")
	topology, known := r.Projection.PowerPlanning.Value()
	if !known || len(topology.Geysers) == 0 {
		t.Fatal("power census read no geyser")
	}
	p := selectRecordedPower(t, r).power
	if p.Method != policy.PowerGenerate || p.Definition != policy.GeothermalDefinition || !p.FixedSite() {
		t.Fatalf("power: %s %s at %v (fixed %v), want %s", p.Method, p.Definition, p.Center, p.FixedSite(), policy.GeothermalDefinition)
	}
	for _, g := range topology.Geysers {
		if g.Cell == p.Center && !g.Occupied {
			return
		}
	}
	t.Fatalf("geothermal centered on %v, not a free geyser of %+v", p.Center, topology.Geysers)
}

// power/reserve: every consumer runs, but a partly charged battery drains
// faster than the one generator supplies, a runway under a day. The power
// planner admits one more generator rather than resting on the reserve.
func TestSnapshotPowerReserveAddsGenerator(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "power-reserve-draining-battery")
	p := selectRecordedPower(t, r).power
	if p.Method != policy.PowerGenerate || p.Definition == "" {
		t.Fatalf("power: %s %s, want one more generator", p.Method, p.Definition)
	}
}

// power/wind: a cleared field and WindTurbine researched. The power planner
// raises a wind turbine.
func TestSnapshotPowerWindRaisesTurbine(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "power-wind-cleared-field")
	p := selectRecordedPower(t, r).power
	if p.Method != policy.PowerGenerate || p.Definition != "WindTurbine" {
		t.Fatalf("power: %s %s, want a WindTurbine", p.Method, p.Definition)
	}
}

// refrigeration/power: the storeroom's cooler has lost its conduit run to
// the generator. The review latches the warm room, and the power family
// (not refrigeration) answers: it routes conduits to the cooler.
func TestSnapshotRefrigerationPowerRoutesConduit(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "refrigeration-power-cooler-cut-off")
	if !r.Review.Latches.Refrigeration {
		t.Fatal("review did not latch the warm storeroom")
	}
	p := selectRecordedPower(t, r).power
	if p.Method != policy.PowerConnect || len(p.Cells) == 0 {
		t.Fatalf("power: %s on %v, want a conduit route", p.Method, p.Cells)
	}
}

// refrigeration/season: heat waves warmed the settled cold storeroom and
// its stock through the native season turn. The review latches the room on
// the warmed stock and the planner patches the cooler it already has.
func TestSnapshotRefrigerationSeasonLatchesWarmedStock(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "refrigeration-season-warmed-stock")
	if !r.Review.Latches.Refrigeration {
		t.Fatal("review did not latch the season-warmed storeroom")
	}
	assertSetpointPatch(t, replayRecordedCooler(t, r, false))
}

// refrigeration/setpoint: the storeroom's outward cooler idles at a warm
// setpoint. The review latches the room (tick 17); the census then reads
// the cooler unpowered (its generator not yet fueled), so the planner waits
// on power rather than building a second cooler, and once it runs (powered
// in the tick-16761 recording) patches its target. Chilled, the review
// releases the room.
func TestSnapshotRefrigerationSetpointLatchesThenReleases(t *testing.T) {
	t.Parallel()
	warm := loadRecorded(t, "refrigeration-setpoint-warm-cooler")
	if !warm.Review.Latches.Refrigeration {
		t.Fatal("review did not latch the warm storeroom")
	}
	if got := replayRecordedCooler(t, warm, false); got.Method != policy.RefrigerationPowerNeeded {
		t.Fatalf("refrigeration: %+v, want %s", got, policy.RefrigerationPowerNeeded)
	}
	assertSetpointPatch(t, replayRecordedCooler(t, warm, true))
	if cooled := loadRecorded(t, "refrigeration-setpoint-cooled"); cooled.Review.Latches.Refrigeration {
		t.Fatal("review still latched the chilled storeroom")
	}
}

// temperature/heatwave: a heat wave overheats the colonists' room. The
// review latches Hot and the temperature planner answers with a powered
// Cooler (tick 15); once the room is cool the latch releases (tick 6865).
func TestSnapshotTemperatureHeatwaveBuildsCooler(t *testing.T) {
	t.Parallel()
	hot := loadRecorded(t, "temperature-heatwave-hot-room")
	if !hot.Review.Latches.Hot {
		t.Fatal("review did not latch the hot room")
	}
	resolved, reason, err := recordedPlanner(hot, policy.EnsureTemperatureSafety).selectTemperature(*hot.Projection, hot.Review.Latches)
	if err != nil || resolved == nil || resolved.temperature.Method != policy.TemperatureCoolPowered {
		t.Fatalf("temperature: resolved %v reason %q err %v, want %s", resolved != nil, reason, err, policy.TemperatureCoolPowered)
	}
	if cooled := loadRecorded(t, "temperature-heatwave-cooled"); cooled.Review.Latches.Hot {
		t.Fatal("review still latched the cooled room")
	}
}

// condition/response: a solar flare, an eclipse and a psychic drone at once
// (tick 15). The review opens a mood state for every drone-struck pawn and
// none other (same pinned mood, ordinary margin), keeps refrigeration
// latched under the flare (the cook-ahead bill's hold), and latches the
// eclipse-dark outdoor stove. After the conditions end (tick 8870) the
// stove is unlatched.
func TestSnapshotConditionResponse(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "condition-response-flare-eclipse-drone")
	if !policy.PowerOutageHold(r.Facts.DisasterConditions) || !r.Review.Latches.Refrigeration {
		t.Fatal("want the flare hold with refrigeration latched")
	}
	if len(r.Review.Latches.Lighting) != 1 {
		t.Fatalf("review latched %v, want the dark stove", r.Review.Latches.Lighting)
	}
	needs, err := r.Detect()
	if err != nil {
		t.Fatal(err)
	}
	opened := map[domain.PawnID]bool{}
	for _, a := range needs.Incidents {
		opened[a.Subject] = opened[a.Subject] || a.ID == policy.EnsureMood
	}
	pawns, _ := r.Facts.MoodPawns.Value()
	var struck int
	for _, p := range pawns {
		drone := false
		thoughts, _ := p.Thoughts.Value()
		for _, th := range thoughts {
			drone = drone || th.Def == policy.PsychicDroneThought
		}
		if drone {
			struck++
		}
		if opened[domain.PawnID(p.ID)] != drone {
			t.Errorf("pawn %s: drone %v, mood state opened %v", p.ID, drone, opened[domain.PawnID(p.ID)])
		}
	}
	if struck == 0 || struck == len(pawns) {
		t.Fatalf("%d of %d pawns drone-struck, want some of each", struck, len(pawns))
	}
	// The lighting step's own read (#759, #794) requests every policy lamp,
	// so under the eclipse it finds TorchLamp and admits one for the dark
	// stove; the review's read holds only StandingLamp.
	step := loadStep(t, "condition-response-step-lighting", policy.MaintainLighting)
	resolved, reason, err := recordedPlanner(r, policy.MaintainLighting).selectLighting(step.Projection, r.Review.Latches)
	if err != nil || resolved == nil || resolved.definition != "TorchLamp" {
		t.Fatalf("eclipse lighting: resolved %v reason %q err %v, want a TorchLamp", resolved != nil, reason, err)
	}
	if after := loadRecorded(t, "condition-response-recovered"); len(after.Review.Latches.Lighting) != 0 {
		t.Fatalf("stove still latched after the conditions: %v", after.Review.Latches.Lighting)
	}
}

// power/battery: a solar-only network with nothing banked for the night.
// The power planner stores the surplus in a Battery.
func TestSnapshotPowerBatteryBanksTheNight(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "power-battery-solar-only")
	p := selectRecordedPower(t, r).power
	if p.Method != policy.PowerStore || p.Definition != "Battery" {
		t.Fatalf("power: %s %s, want a Battery", p.Method, p.Definition)
	}
}

// condition/response, cook-ahead: under the solar flare the coolers are
// dark, so MaintainRefrigeration answers the warm at-risk stock with a
// CookMealSimple bill on one of the fixture's fuelled benches (the bill
// planner's cook-ahead path; the case ran with no other cooking bill, so no
// bench is already claimed).
func TestSnapshotConditionCookAheadBill(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "condition-response-flare-eclipse-drone")
	p := r.Projection
	if !policy.PowerOutageHold(p.Facts.DisasterConditions) {
		t.Fatal("no flare hold")
	}
	reviewer := &RoutineReviewer{policy: r.Policy}
	warm, err := policy.ReviewRefrigeration(p.Facts.FoodStorageUpkeep, r.Review.Latches.Refrigeration, r.Policy.FoodStorage)
	if err != nil {
		t.Fatal(err)
	}
	bill, known := policy.SelectProductionBill(policy.CookAheadFood, p.ProductionBenches, p.Facts.Colonists, p.Facts.FoodDays, warm.WarmNutrition, reviewer.seasonal(p.Facts).FoodTargetDays)
	if !known || (bill.Recipe != "CookMealSimple" && bill.Recipe != "CookMealSimpleBulk") || bill.Bench == "" || bill.Target <= 0 {
		t.Fatalf("cook-ahead: known %v bill %+v, want a CookMealSimple bill on a bench", known, bill)
	}
}

// refrigeration/power, after: once the conduit run powers the cooler and
// the patched setpoint chills the room (tick 57568), the power family has
// no deficit left and the review releases the storeroom.
func TestSnapshotRefrigerationPowerReleasesOnceReconnected(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "refrigeration-power-reconnected-cooled")
	if r.Review.Latches.Refrigeration {
		t.Fatal("storeroom still latched after the cooler was reconnected")
	}
	if resolved, reason, err := recordedPlanner(r, policy.EnsureBasicPower).selectPower(*r.Projection, nil); err != nil || resolved != nil || reason != BuildingReasonNoDeficit {
		t.Fatalf("power: resolved %v reason %q err %v, want %s", resolved != nil, reason, err, BuildingReasonNoDeficit)
	}
}

// power/battery, after: with the Battery built and charged through a night
// (tick 60017), the power planner has nothing more to store or raise.
func TestSnapshotPowerBatteryBankedHasNoDeficit(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "power-battery-banked")
	if resolved, reason, err := recordedPlanner(r, policy.EnsureBasicPower).selectPower(*r.Projection, nil); err != nil || resolved != nil || reason != BuildingReasonNoDeficit {
		t.Fatalf("power: resolved %v reason %q err %v, want %s", resolved != nil, reason, err, BuildingReasonNoDeficit)
	}
}

// replayRecordedCooler replays the refrigeration method over r with the
// recording's one PowerPlanning cooler read back as native would: turned so
// its cold cell is inside the latched storeroom, venting outdoors, at the
// vanilla 21 C default target (the recording holds no building read), and
// powered when power says so.
func replayRecordedCooler(t *testing.T, r snapshot.Routine, power bool) policy.RefrigerationProposal {
	t.Helper()
	p := r.Policy.FoodStorage
	review, err := policy.ReviewRefrigeration(r.Facts.FoodStorageUpkeep, r.Review.Latches.Refrigeration, p)
	if err != nil || !review.Active || len(review.Rooms) == 0 {
		t.Fatalf("refrigeration review %+v, %v: want an active room", review, err)
	}
	rooms, _ := r.Projection.Rooms.Value()
	inside := map[domain.Cell]bool{}
	for _, room := range rooms.Rooms {
		if room.ID == review.Rooms[0] {
			for _, c := range room.Cells {
				inside[c] = true
			}
		}
	}
	topology, _ := r.Projection.PowerPlanning.Value()
	var coolers []policy.RefrigerationCooler
	for _, b := range topology.Buildings {
		if b.Definition != "Cooler" {
			continue
		}
		cooler := policy.RefrigerationCooler{ID: b.ID, Position: b.Cell, Connected: b.Connected, PowerOn: b.Powered, Target: domain.Known(21.0), HotIndoors: domain.Known(false)}
		for _, rot := range []domain.Rotation{domain.North, domain.East, domain.South, domain.West} {
			if cooler.Rotation = rot; inside[cooler.Cold()] {
				break
			}
		}
		if !inside[cooler.Cold()] {
			t.Fatalf("cooler %s at %v borders no storeroom cell", b.ID, b.Cell)
		}
		if power {
			cooler.PowerOn = domain.Known(true)
		}
		coolers = append(coolers, cooler)
	}
	if len(coolers) != 1 {
		t.Fatalf("recorded %d coolers, want the storeroom's one", len(coolers))
	}
	proposal, err := policy.SelectRefrigerationMethod(review, observation.RefrigerationFacts(*r.Projection, domain.Known(coolers)), p, false)
	if err != nil {
		t.Fatal(err)
	}
	return proposal
}

// assertSetpointPatch checks the planner patches the existing cooler's
// target (to the freezer setpoint) rather than building a second one.
func assertSetpointPatch(t *testing.T, got policy.RefrigerationProposal) {
	t.Helper()
	if got.Method != policy.RefrigerationSetTarget || got.Cooler == "" {
		t.Fatalf("refrigeration: %+v, want %s on the recorded cooler", got, policy.RefrigerationSetTarget)
	}
}
