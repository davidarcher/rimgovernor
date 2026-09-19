package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var testWorkTypes = []WorkType{WorkConstruction, WorkGrowing, WorkCooking, WorkDoctor, WorkPlantCutting, WorkHunting, WorkMining, WorkSmithing, WorkResearch, WorkWarden, WorkHandling, WorkHauling, WorkCleaning, WorkFirefighter}

func testWorkPawn(id PawnID, manual, ranged bool, skills []WorkSkill, traits ...PawnTrait) WorkPawn {
	work := make([]WorkPriority, 0, len(testWorkTypes))
	for _, w := range testWorkTypes {
		work = append(work, WorkPriority{Work: w})
	}
	return WorkPawn{ID: id, Available: domain.Known(true), Applies: domain.Known(true), Manual: domain.Known(manual), Ranged: domain.Known(ranged), Work: domain.Known(work), Skills: domain.Known(skills), Traits: domain.Known(traits), Incapable: domain.Known([]WorkType{}), Age: domain.Known(30.0)}
}

func workTeam(manual bool) []WorkPawn {
	return []WorkPawn{
		testWorkPawn("builder", manual, false, []WorkSkill{{Name: "Construction", Level: 15}, {Name: "Plants", Level: 1}, {Name: "Cooking", Level: 10}, {Name: "Medicine", Level: 8}, {Name: "Shooting", Level: 5}}),
		testWorkPawn("grower", manual, false, []WorkSkill{{Name: "Construction", Level: 3}, {Name: "Plants", Level: 15}, {Name: "Cooking", Level: 1}, {Name: "Medicine", Level: 3}, {Name: "Shooting", Level: 5}}),
	}
}
func workValue(t *testing.T, d WorkDecision, pawn PawnID, work WorkType) int {
	t.Helper()
	for _, p := range d.Assignments {
		if p.Pawn == pawn {
			for _, v := range p.Priorities {
				if v.Work == work {
					return v.Priority
				}
			}
		}
	}
	t.Fatal("assignment missing", pawn, work)
	return -1
}
func coverageOf(t *testing.T, d WorkDecision, work WorkType) WorkCoverage {
	t.Helper()
	for _, c := range d.Coverage {
		if c.Work == work {
			return c
		}
	}
	t.Fatal("coverage missing", work)
	return WorkCoverage{}
}
func TestWorkAssignmentSpecialistsModesAndReadback(t *testing.T) {
	for _, manual := range []bool{false, true} {
		team := workTeam(manual)
		decision, err := AssignWork(team, nil, nil)
		if err != nil {
			t.Fatal(err)
		}
		if capable, known := decision.Capacity.Value(); !known || !capable {
			t.Fatal(decision)
		}
		if workValue(t, decision, "builder", "Construction") != 1 || workValue(t, decision, "grower", "Growing") != 1 || workValue(t, decision, "builder", "Doctor") != 1 || workValue(t, decision, "builder", "Cooking") != 1 {
			t.Fatal(decision)
		}
		// Construction 3 is under the floor: never enabled, whatever the mode.
		if workValue(t, decision, "grower", "Construction") != 0 || workValue(t, decision, "grower", "Cooking") != 0 {
			t.Fatal(decision)
		}
		if workValue(t, decision, "builder", "Hauling") != 3 || workValue(t, decision, "grower", "Firefighter") != 1 {
			t.Fatal(decision)
		}
		if matches, known := decision.Matches.Value(); !known || matches {
			t.Fatal("unassigned work matched", decision)
		}
		for i := range team {
			values := append([]WorkPriority(nil), decision.Assignments[i].Priorities...)
			if !manual {
				for j := range values {
					if values[j].Priority > 0 {
						values[j].Priority = 3
					}
				}
			}
			team[i].Work = domain.Known(values)
		}
		next, err := AssignWork(team, nil, nil)
		if err != nil {
			t.Fatal(err)
		}
		if matches, known := next.Matches.Value(); !known || !matches {
			t.Fatal("native mode readback did not match", next)
		}
		team[0], team[1] = team[1], team[0]
		reordered, err := AssignWork(team, nil, nil)
		if err != nil || !reflect.DeepEqual(next, reordered) {
			t.Fatal("pawn order changed assignment", reordered, err)
		}
	}
}
func TestWorkAssignmentRequirementsOverridesAndUnknown(t *testing.T) {
	team := workTeam(true)
	d, err := AssignWork(team, []WorkRequirement{{Work: "Construction", Skill: "Construction", Minimum: 10}}, []WorkOverride{{Pawn: "builder", Work: "Construction", Priority: 0}})
	if err != nil {
		t.Fatal(err)
	}
	if capable, known := d.Capacity.Value(); !known || capable {
		t.Fatal("override/minimum ignored", d)
	}
	if workValue(t, d, "builder", "Construction") != 0 {
		t.Fatal(d)
	}
	if c := coverageOf(t, d, WorkConstruction); c.Demand != 1 || c.Owners != 0 || c.Capable != 0 {
		t.Fatal(c)
	}
	team[0].Manual = domain.Unknown[bool]()
	d, err = AssignWork(team, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := d.Matches.Value(); known || len(d.Assignments) != 0 {
		t.Fatal("unknown mode manufactured assignment", d)
	}
	team[0].Available = domain.Known(false)
	d, err = AssignWork(team, nil, nil)
	if err != nil || len(d.Assignments) != 1 || d.Assignments[0].Pawn != "grower" {
		t.Fatal(d, err)
	}
	if _, err = AssignWork(workTeam(true), nil, []WorkOverride{{Pawn: "builder", Work: "Cooking", Priority: 0}, {Pawn: "builder", Work: "Cooking", Priority: 1}}); err == nil {
		t.Fatal("duplicate override")
	}
	if _, err = AssignWork(workTeam(true), nil, []WorkOverride{{Pawn: "builder", Work: "MissingWork", Priority: 1}}); err == nil {
		t.Fatal("unavailable override ignored")
	}
}

// Nobody clears the cooking floor: the best cook still owns it, and the
// coverage row says no capable cook exists.
func TestWorkAssignmentFloorRelaxesForCoreWork(t *testing.T) {
	team := []WorkPawn{
		testWorkPawn("a", true, false, []WorkSkill{{Name: "Cooking", Level: 2}, {Name: "Construction", Level: 6}, {Name: "Medicine", Level: 6}, {Name: "Plants", Level: 6}}),
		testWorkPawn("b", true, false, []WorkSkill{{Name: "Cooking", Level: 4}, {Name: "Construction", Level: 6}, {Name: "Medicine", Level: 6}, {Name: "Plants", Level: 6}}),
	}
	d, err := AssignWork(team, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if workValue(t, d, "b", WorkCooking) != 1 || workValue(t, d, "a", WorkCooking) != 2 {
		t.Fatal(d.Assignments)
	}
	if c := coverageOf(t, d, WorkCooking); c.Owners != 1 || c.Capable != 0 {
		t.Fatal(c)
	}
	if capable, _ := d.Capacity.Value(); !capable {
		t.Fatal("relaxed floor left capacity false", d.Coverage)
	}
}

// Passion beside a no-passion incumbent: the level-12 cook owns Cooking and
// the level-7 burning-passion cook shares it at 2 so it trains; a level-1
// passion is under the poisoning floor and a level-6 no-passion cook is
// merely capable.
func TestWorkAssignmentGrowthSecondary(t *testing.T) {
	team := []WorkPawn{
		testWorkPawn("veteran", true, false, []WorkSkill{{Name: "Cooking", Level: 12}, {Name: "Construction", Level: 6}}),
		testWorkPawn("apprentice", true, false, []WorkSkill{{Name: "Cooking", Level: 7, Passion: "Major"}, {Name: "Construction", Level: 6}}),
		testWorkPawn("novice", true, false, []WorkSkill{{Name: "Cooking", Level: 1, Passion: "Major"}, {Name: "Construction", Level: 6}}),
		testWorkPawn("hauler", true, false, []WorkSkill{{Name: "Cooking", Level: 6}, {Name: "Construction", Level: 6}}),
	}
	d, err := AssignWork(team, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if workValue(t, d, "veteran", WorkCooking) != 1 || workValue(t, d, "apprentice", WorkCooking) != 2 {
		t.Fatal(d.Assignments)
	}
	if workValue(t, d, "novice", WorkCooking) != 0 || workValue(t, d, "hauler", WorkCooking) != 4 {
		t.Fatal(d.Assignments)
	}
	// Four pawns, two cooks wanted at eight: an equal-level passion beats
	// no passion for the second slot.
	team = append(team, testWorkPawn("e", true, false, []WorkSkill{{Name: "Cooking", Level: 9}, {Name: "Construction", Level: 6}}), testWorkPawn("f", true, false, []WorkSkill{{Name: "Cooking", Level: 9, Passion: "Minor"}, {Name: "Construction", Level: 6}}), testWorkPawn("g", true, false, nil), testWorkPawn("h", true, false, nil))
	d, err = AssignWork(team, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if workValue(t, d, "veteran", WorkCooking) != 1 || workValue(t, d, "apprentice", WorkCooking) != 1 || workValue(t, d, "f", WorkCooking) != 2 || workValue(t, d, "e", WorkCooking) != 3 {
		t.Fatal(d.Assignments)
	}
}

// Traits: a Pyromaniac never fights fire, a Brawler holding a bow is not the
// hunter while a shooter exists, an Abrasive pawn never wardens beside a Kind
// one, and Industrious outranks a slightly higher Slothful level.
func TestWorkAssignmentTraits(t *testing.T) {
	team := []WorkPawn{
		testWorkPawn("pyro", true, true, []WorkSkill{{Name: "Shooting", Level: 8}, {Name: "Social", Level: 6}, {Name: "Mining", Level: 9}}, PawnTrait{Name: "Pyromaniac"}, PawnTrait{Name: "Industriousness", Degree: -2}),
		testWorkPawn("brawler", true, true, []WorkSkill{{Name: "Shooting", Level: 12}, {Name: "Social", Level: 6}, {Name: "Mining", Level: 8}}, PawnTrait{Name: "Brawler"}, PawnTrait{Name: "Industriousness", Degree: 2}),
		testWorkPawn("abrasive", true, false, []WorkSkill{{Name: "Social", Level: 12}, {Name: "Mining", Level: 2}}, PawnTrait{Name: "Abrasive"}),
		testWorkPawn("kind", true, false, []WorkSkill{{Name: "Social", Level: 6}, {Name: "Mining", Level: 2}}, PawnTrait{Name: "Kind"}, PawnTrait{Name: "Unknown_ModTrait", Degree: 3}),
	}
	d, err := PlanWork(team, nil, nil, WorkDemand{Prisoners: 1})
	if err != nil {
		t.Fatal(err)
	}
	if workValue(t, d, "pyro", WorkFirefighter) != 0 || workValue(t, d, "kind", WorkFirefighter) != 1 {
		t.Fatal(d.Assignments)
	}
	if workValue(t, d, "brawler", WorkHunting) != 0 || workValue(t, d, "pyro", WorkHunting) != 1 {
		t.Fatal(d.Assignments)
	}
	if workValue(t, d, "abrasive", WorkWarden) != 0 || workValue(t, d, "kind", WorkWarden) != 1 {
		t.Fatal(d.Assignments)
	}
	// Mining: brawler 8 industrious (+3.5) beats pyro 9 slothful (-3.5).
	if workValue(t, d, "brawler", WorkMining) != 1 || workValue(t, d, "pyro", WorkMining) != 2 {
		t.Fatal(d.Assignments)
	}
}

// A backstory-incapable work type is never assigned even with the skill.
func TestWorkAssignmentIncapable(t *testing.T) {
	pawn := testWorkPawn("a", true, false, []WorkSkill{{Name: "Mining", Level: 12}})
	pawn.Incapable = domain.Known([]WorkType{WorkMining})
	d, err := AssignWork([]WorkPawn{pawn}, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if workValue(t, d, "a", WorkMining) != 0 {
		t.Fatal(d.Assignments)
	}
}

// A level-14 miner parked on nothing keeps mining at 2; one who owns other
// work is reported decaying.
func TestWorkAssignmentDecay(t *testing.T) {
	rows := func(mining int) []WorkSkill {
		return []WorkSkill{{Name: "Construction", Level: 15}, {Name: "Mining", Level: mining}, {Name: "Cooking", Level: 8}, {Name: "Medicine", Level: 8}, {Name: "Plants", Level: 8}}
	}
	team := []WorkPawn{testWorkPawn("a", true, false, rows(15)), testWorkPawn("b", true, false, rows(14)), testWorkPawn("c", true, false, rows(13))}
	d, err := AssignWork(team, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	// One owner, one backup, one parked at 3 with a decaying skill; every
	// flagged skill sits at 3 for its pawn.
	mining := 0
	for _, row := range d.Decaying {
		work := WorkMining
		if row.Skill == "Construction" {
			work = WorkConstruction
		}
		if row.Skill == "Mining" {
			mining++
		}
		if workValue(t, d, row.Pawn, work) != 3 || row.Level < 11 {
			t.Fatal(row, d.Assignments)
		}
	}
	if mining != 1 {
		t.Fatal(d.Decaying, d.Assignments)
	}
	idle := testWorkPawn("idle", true, false, []WorkSkill{{Name: "Crafting", Level: 14}})
	d, err = AssignWork([]WorkPawn{idle, team[0], team[1]}, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if workValue(t, d, "idle", WorkSmithing) < 1 || workValue(t, d, "idle", WorkSmithing) > 2 {
		t.Fatal(d.Assignments)
	}
}

// A twelve-pawn roster: every work type with demand has an owner, no pawn's
// only enabled work is hauling and cleaning, and the matrix is stable across
// reviews that read the previous matrix back.
func TestWorkAssignmentCoverageAndStability(t *testing.T) {
	var team []WorkPawn
	skills := []string{"Construction", "Plants", "Cooking", "Medicine", "Shooting", "Mining", "Crafting", "Intellectual", "Social", "Animals"}
	for i := 0; i < 12; i++ {
		var rows []WorkSkill
		for j, name := range skills {
			level := (i*7 + j*3) % 15
			passion := ""
			if (i+j)%5 == 0 {
				passion = "Minor"
			}
			rows = append(rows, WorkSkill{Name: name, Level: level, Passion: passion})
		}
		team = append(team, testWorkPawn(PawnID(string(rune('a'+i))+"pawn"), true, i%3 == 0, rows))
	}
	demand := WorkDemand{GrowingCells: 300, Construction: true, Prisoners: 1}
	required := []WorkRequirement{{Work: WorkMining, Skill: "Mining"}, {Work: WorkSmithing, Skill: "Crafting"}, {Work: WorkResearch, Skill: "Intellectual"}}
	d, err := PlanWork(team, required, nil, demand)
	if err != nil {
		t.Fatal(err)
	}
	for _, c := range d.Coverage {
		if c.Demand > 0 && c.Owners == 0 {
			t.Fatal("uncovered", c)
		}
	}
	for _, a := range d.Assignments {
		skilled := false
		for _, p := range a.Priorities {
			if p.Priority > 0 && !basicWork(p.Work) && !pinnedWork(p.Work) {
				skilled = true
			}
		}
		if !skilled {
			t.Fatal("hauling only", a)
		}
	}
	previous := d
	for round := 0; round < 3; round++ {
		for i := range team {
			team[i].Work = domain.Known(append([]WorkPriority(nil), previous.Assignments[i].Priorities...))
		}
		next, err := PlanWork(team, required, nil, demand)
		if err != nil {
			t.Fatal(err)
		}
		if !reflect.DeepEqual(next.Assignments, previous.Assignments) {
			t.Fatal("matrix flipped", round)
		}
		if matches, _ := next.Matches.Value(); !matches {
			t.Fatal("readback mismatch")
		}
		previous = next
	}
}
