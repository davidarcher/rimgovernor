package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// Snapshot tests (#894) for the planner cases whose decision comes from a
// read the planner makes at step time: the hospital step's room census, the
// research step's research census, the resource, workshop and gear steps'
// bench and recipe censuses. Each replaces the native case it names; the
// recordings were taken from that case's run with RIMGOVERNOR_SNAPSHOT_DIR
// set, at the commit that deleted it.

// loadPlannerStep loads testdata/<name>.json.gz, a planner step's recorded
// policy inputs (snapshot.Planner), and checks it is goal's.
func loadPlannerStep(t *testing.T, name string, goal policy.GoalID) snapshot.Planner {
	t.Helper()
	p, err := snapshot.LoadPlanner("testdata/" + name + ".json.gz")
	if err != nil {
		t.Fatal(err)
	}
	if p.Goal != goal {
		t.Fatalf("%s: recorded %s's step, want %s", name, p.Goal, goal)
	}
	return p
}

// facility/hospital: two seeded flu patients and the startup hut's
// furnished bunks. The hospital step's own room census has no hosted
// medical bed, but the bunks exactly house the colony: converting one would
// drop indoor sleeping capacity (which excludes medical beds) under the
// colonists and fail the shelter gate, so it stages a new spot instead.
func TestSnapshotHospitalStagesBedAtShelterCapacity(t *testing.T) {
	t.Parallel()
	step := loadStep(t, "hospital-step-convert", policy.MaintainMedicalReserves)
	if _, known := step.Projection.Rooms.Value(); !known {
		t.Fatal("hospital step read holds no room census")
	}
	choice, err := policy.SelectHospitalBed(hospitalRequest(step.Projection))
	if err != nil || choice.Method != policy.HospitalBuild || choice.Definition == "" {
		t.Fatalf("hospital: choice %+v err %v, want a staged bed", choice, err)
	}
}

// production/components: a component runway deficit, research done and a
// fabrication bench standing with steel in stock. The resource step's
// bench census funds a Make_ComponentIndustrial bill on that bench.
func TestSnapshotComponentsFundFabricationBill(t *testing.T) {
	t.Parallel()
	p := loadPlannerStep(t, "components-fabrication-bill", policy.MaintainResource)
	for _, request := range p.ResourceMethods {
		if request.Resource != policy.ComponentResource {
			continue
		}
		choice, err := policy.SelectResourceMethod(request)
		if err != nil || choice.Kind != policy.ResourceMethodProduce || choice.Recipe != "Make_ComponentIndustrial" || choice.Bench == "" {
			t.Fatalf("components: choice %+v err %v, want a Make_ComponentIndustrial bill", choice, err)
		}
		return
	}
	t.Fatalf("no %s bill selection recorded: %+v", policy.ComponentResource, p.ResourceMethods)
}

// production/deepdrill: a steel runway deficit, no surface ore, DeepDrilling
// researched and a seeded steel lump under a built scanner. The deep drill
// step's own read offers that lump as a drill site.
func TestSnapshotDeepDrillSitesTheSteelLump(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "deepdrill-steel-runway")
	step := loadStep(t, "deepdrill-step-lump", policy.MaintainResource)
	sites := deepDrillSites(step.Projection, r.Review.ResourceRunwayState())
	if len(sites) == 0 || sites[0].Definition != "Steel" {
		t.Fatalf("deep drill sites %+v, want the steel lump", sites)
	}
}

// production/drillremoval: one unpowered drill over barren ground beside
// the seeded lump. The deep drill step's read marks that drill depleted and
// undesignated, so it is the one drill the step deconstructs.
func TestSnapshotDrillRemovalSelectsTheExhaustedDrill(t *testing.T) {
	t.Parallel()
	step := loadStep(t, "drillremoval-step-exhausted", policy.MaintainResource)
	deep, known := step.Projection.DeepResources.Value()
	if !known || len(deep.Drills) == 0 {
		t.Fatalf("deep resources %+v: want the fixture drill", deep)
	}
	if drills := exhaustedDrills(deep); len(drills) != 1 || drills[0].Definition != "DeepDrill" {
		t.Fatalf("exhausted drills %+v of %+v, want the one depleted drill", drills, deep.Drills)
	}
}

// layout/tidy: the field family's Camp-tier rice patch lies off the grid;
// with Stonecutting finished (Masonry) the review's tidy proposal re-sites
// it onto a Fields sub-cell on the grid with its crop kept, and TidyLayout
// opens a deficit on it. The review's zone read is not recorded, so the
// proposal is the recorded one; the goal is replayed.
func TestSnapshotTidyResitesCampField(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "tidy-camp-field-resite")
	review, known := r.Facts.LayoutTidy.Value()
	if !known || !review.Active || review.Proposal == nil {
		t.Fatalf("tidy review %+v: want an active proposal", review)
	}
	p := review.Proposal
	if p.Item.Kind != policy.TidyField || p.Item.Crop != "Plant_Rice" || p.Gain <= 0 {
		t.Fatalf("tidy proposal %+v: want the rice field re-sited with an alignment gain", p)
	}
	if a, err := r.Assessment(policy.TidyLayout); err != nil || a.Need != domain.NeedDeficit {
		t.Fatalf("TidyLayout assessment %+v err %v, want a deficit", a, err)
	}
}

// research/ladder: Stonecutting seeded at 97% and no research bench. The
// research step's census puts the ladder's first unfinished rung,
// Stonecutting, first and owes a bench for it, and the bench step admits
// a SimpleResearchBench indoors. With Stonecutting finished the same
// census selects the next rung, Electricity.
func TestSnapshotResearchLadderBenchThenNextRung(t *testing.T) {
	t.Parallel()
	p := loadPlannerStep(t, "research-ladder-no-bench", policy.EnsureResearch)
	if len(p.Research) != 1 {
		t.Fatalf("recorded %d research selections, want 1", len(p.Research))
	}
	in := p.Research[0]
	next, reason := researchNext(in)
	if next != "Stonecutting" || reason != "" || !policy.ResearchBenchNeeded(in.Read.Projects[next]) {
		t.Fatalf("research: next %q reason %q, want Stonecutting owing a bench", next, reason)
	}
	step := loadStep(t, "research-ladder-step-bench", policy.EnsureResearch)
	bench, reason, err := (&RoutineBuildingPlanner{goal: policy.EnsureResearch}).selectResearchBench(step.Projection)
	if err != nil || bench == nil || bench.definition != policy.ResearchBenchDefinition || bench.environment != policy.PlacementIndoors {
		t.Fatalf("bench: %+v reason %q err %v, want an indoor %s", bench, reason, err, policy.ResearchBenchDefinition)
	}
	in.Read.Finished = append(append([]string(nil), in.Read.Finished...), "Stonecutting")
	if next, reason = researchNext(in); next != "Electricity" || reason != "" {
		t.Fatalf("after Stonecutting: next %q reason %q, want Electricity", next, reason)
	}
}

// production/apparel: a colonist short of a shirt and no tailoring bench.
// The workshop step's census builds a HandTailoringBench for the shirt;
// once it stands, the gear step's bench census produces the shirt on it.
func TestSnapshotApparelBuildsBenchThenProducesShirt(t *testing.T) {
	t.Parallel()
	p := loadPlannerStep(t, "apparel-workshop-no-bench", policy.MaintainEquipment)
	var request *policy.WorkshopRequest
	for i := range p.Workshops {
		if len(p.Workshops[i].Definitions) > 0 {
			request = &p.Workshops[i]
		}
	}
	if request == nil {
		t.Fatalf("no workshop census recorded: %+v", p.Workshops)
	}
	choice, err := policy.SelectWorkshopBench(*request)
	if err != nil || choice.Method != policy.WorkshopBuild || choice.Definition != "HandTailoringBench" {
		t.Fatalf("workshop %s: choice %+v err %v, want a HandTailoringBench build", request.Resource, choice, err)
	}
	g := loadPlannerStep(t, "apparel-gear-bench-standing", policy.MaintainEquipment)
	if len(g.GearMethods) == 0 {
		t.Fatal("no gear selection recorded")
	}
	method, err := policy.SelectGearMethod(g.GearMethods[0])
	if err != nil || method.Kind != policy.GearProduce || method.Bench == "" || method.Recipe != "Make_Apparel_BasicShirt" {
		t.Fatalf("gear: method %+v err %v, want a shirt produced on the bench", method, err)
	}
}
