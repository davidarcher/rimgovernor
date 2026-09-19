package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestRoutineLaborCountsEnabledWorkTypes(t *testing.T) {
	builder := WorkPawn{ID: "a", Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]WorkPriority{{Work: WorkConstruction, Priority: 1}, {Work: WorkResearch, Priority: 0}, {Work: WorkHauling, Priority: 3, Disabled: true}})}
	researcher := WorkPawn{ID: "b", Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]WorkPriority{{Work: WorkResearch, Priority: 2}, {Work: WorkConstruction, Priority: 4}})}
	downed := WorkPawn{ID: "c", Available: domain.Known(false), Work: domain.Unknown[[]WorkPriority]()}
	labor, known := RoutineLabor([]WorkPawn{builder, researcher, downed}).Value()
	if !known || !reflect.DeepEqual(labor, map[WorkType]int{WorkConstruction: 2, WorkResearch: 1}) {
		t.Fatal(labor, known)
	}
	unknownWork := WorkPawn{ID: "d", Available: domain.Known(true), Applies: domain.Known(true)}
	if _, known := RoutineLabor([]WorkPawn{builder, unknownWork}).Value(); known {
		t.Fatal("unknown work settings became labor evidence")
	}
	emptyWork := WorkPawn{ID: "e", Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]WorkPriority{})}
	if _, known := RoutineLabor([]WorkPawn{builder, emptyWork}).Value(); known {
		t.Fatal("empty work list became labor evidence")
	}
	if GoalLabor(EnsureComfort)[0] != WorkConstruction || GoalLabor(EnsureResearch)[0] != WorkResearch || GoalLabor(ActiveCombat) != nil || GoalLabor(ProductionPolicy) != nil {
		t.Fatal("unexpected goal labor profiles")
	}
	// Feed is cooked and hauled, never handled (#311).
	if feed := GoalLabor(MaintainAnimalFeed); len(feed) != 3 || feed[0] != WorkCooking || feed[1] != WorkHauling || feed[2] != WorkGrowing || GoalLabor(MaintainHerd)[0] != WorkHandling {
		t.Fatal("unexpected animal labor profiles", feed)
	}
}

// One builder cannot serve two construction goals; a researcher's slot is
// still granted to research, and the deferral names the missing work type.
func TestDevelopmentLaborBottleneck(t *testing.T) {
	r := developmentFixture()
	r.Limit = 3
	r.Labor = domain.Known(map[WorkType]int{WorkConstruction: 1, WorkResearch: 1})
	r.Goals = []DevelopmentGoal{
		{ID: "comfort", Source: AutopilotGoal, Priority: 4, Deficit: domain.Known(0.9), Labor: GoalLabor(EnsureComfort)},
		{ID: "expansion", Source: AutopilotGoal, Priority: 4, Deficit: domain.Known(0.6), Labor: GoalLabor(EnsureExpansion)},
		{ID: "research", Source: AutopilotGoal, Priority: 4, Deficit: domain.Known(0.3), Labor: GoalLabor(EnsureResearch)},
		{ID: "resource", Source: AutopilotGoal, Priority: 4, Deficit: domain.Known(0.2), Labor: GoalLabor(MaintainResource)},
	}
	s := rank(t, r)
	requireSelected(t, s, "comfort", "research")
	rows := map[GoalID]DevelopmentRow{}
	for _, row := range s.Rows {
		rows[row.Goal] = row
	}
	if rows["expansion"].Reason != DevelopmentLabor || rows["expansion"].Bottleneck != WorkConstruction {
		t.Fatal(rows["expansion"])
	}
	// Every alternative of a multi-type profile is exhausted: the bottleneck
	// names the first alternative in stable order.
	if rows["resource"].Reason != DevelopmentLabor || rows["resource"].Bottleneck != WorkCrafting {
		t.Fatal(rows["resource"])
	}
	if err := ValidateDevelopmentState(s); err != nil {
		t.Fatal(err)
	}
	// A committed player construction project occupies the only builder.
	r.Labor = domain.Known(map[WorkType]int{WorkConstruction: 1, WorkResearch: 1, WorkMining: 1})
	r.Commitments = []Commitment{{Goal: "player-room", Source: PlayerGoal, Priority: 2, Progress: developmentProgress(t), Labor: LaborProfile{WorkConstruction}}}
	s = rank(t, r)
	requireSelected(t, s, "research", "resource")
	// Unknown labor keeps only the coarse worker bound; an empty profile is
	// never labor-gated.
	r.Commitments = nil
	r.Labor = domain.Unknown[map[WorkType]int]()
	requireSelected(t, rank(t, r), "comfort", "expansion", "research")
	r.Labor = domain.Known(map[WorkType]int{})
	r.Goals = append(r.Goals, DevelopmentGoal{ID: "monitor", Source: AutopilotGoal, Priority: 4, Deficit: domain.Known(0.1)})
	s = rank(t, r)
	requireSelected(t, s, "monitor")
	// Labor deferral is not capacity deferral: yielding a slot does not hand
	// it to a goal whose work type is still occupied.
	r.Labor = domain.Known(map[WorkType]int{WorkResearch: 1})
	s = rank(t, r)
	requireSelected(t, s, "research", "monitor")
	if y := YieldDevelopment(s, "research"); !reflect.DeepEqual(selected(y), []GoalID{"monitor"}) {
		t.Fatal(selected(y))
	}
	r.Labor = domain.Known(map[WorkType]int{"": 1})
	if _, err := RankDevelopment(r); err == nil {
		t.Fatal("invalid labor census accepted")
	}
}

func TestEquipmentDevelopmentCanUseBuilderBeforeTailoringExists(t *testing.T) {
	r := developmentFixture()
	r.Labor = domain.Known(map[WorkType]int{WorkConstruction: 1})
	r.Goals = []DevelopmentGoal{{ID: MaintainEquipment, Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(1.0), Labor: GoalLabor(MaintainEquipment)}}
	requireSelected(t, rank(t, r), MaintainEquipment)
}
