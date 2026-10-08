package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestRoundsLaborCountsEnabledWorkTypes(t *testing.T) {
	builder := WorkPawn{ID: "a", Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]WorkPriority{{Work: WorkConstruction, Priority: 1}, {Work: WorkResearch, Priority: 0}, {Work: WorkHauling, Priority: 3, Disabled: true}})}
	researcher := WorkPawn{ID: "b", Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]WorkPriority{{Work: WorkResearch, Priority: 2}, {Work: WorkConstruction, Priority: 4}})}
	downed := WorkPawn{ID: "c", Available: domain.Known(false), Work: domain.Unknown[[]WorkPriority]()}
	labor, known := RoundsLabor([]WorkPawn{builder, researcher, downed}).Value()
	if !known || !reflect.DeepEqual(labor, map[WorkType]int{WorkConstruction: 2, WorkResearch: 1}) {
		t.Fatal(labor, known)
	}
	unknownWork := WorkPawn{ID: "d", Available: domain.Known(true), Applies: domain.Known(true)}
	if _, known := RoundsLabor([]WorkPawn{builder, unknownWork}).Value(); known {
		t.Fatal("unknown work settings became labor evidence")
	}
	emptyWork := WorkPawn{ID: "e", Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]WorkPriority{})}
	if _, known := RoundsLabor([]WorkPawn{builder, emptyWork}).Value(); known {
		t.Fatal("empty work list became labor evidence")
	}
	if ConcernLabor(EnsureComfort)[0] != WorkConstruction || ConcernLabor(EnsureResearch)[0] != WorkResearch || ConcernLabor(ActiveCombat) != nil {
		t.Fatal("unexpected goal labor profiles")
	}
	if ConcernLabor(MaintainHerd)[0] != WorkHandling {
		t.Fatal("unexpected animal labor profiles")
	}
}

// One builder cannot serve two construction goals; a researcher's slot is
// still granted to research, and the deferral names the missing work type.
func TestDevelopmentLaborBottleneck(t *testing.T) {
	r := developmentFixture()
	r.Labor = domain.Known(map[WorkType]int{WorkConstruction: 1, WorkResearch: 1})
	r.Concerns = []DevelopmentConcern{
		{ID: "comfort", Priority: 4, Deficit: domain.Known(0.9), Labor: ConcernLabor(EnsureComfort)},
		{ID: "expansion", Priority: 4, Deficit: domain.Known(0.6), Labor: ConcernLabor(MaintainHousing)},
		{ID: "research", Priority: 4, Deficit: domain.Known(0.3), Labor: ConcernLabor(EnsureResearch)},
		{ID: "resource", Priority: 4, Deficit: domain.Known(0.2), Labor: ConcernLabor(MaintainResource)},
	}
	s := rank(t, r)
	requireSelected(t, s, "comfort", "expansion", "research", "resource")
	rows := map[ConcernID]DevelopmentRow{}
	for _, row := range s.Rows {
		rows[row.Concern] = row
	}
	if err := ValidateDevelopmentState(s); err != nil {
		t.Fatal(err)
	}
	// A committed player construction project occupies the only builder.
	r.Labor = domain.Known(map[WorkType]int{WorkConstruction: 1, WorkResearch: 1, WorkMining: 1})
	r.Commitments = []Commitment{{Concern: "player-room", Priority: 3, Progress: developmentProgress(t), Labor: LaborProfile{WorkConstruction}}}
	s = rank(t, r)
	requireSelected(t, s, "comfort", "expansion", "research", "resource")
	// Unknown labor keeps only the coarse worker bound; an empty profile is
	// never labor-gated.
	r.Commitments = nil
	r.Labor = domain.Unknown[map[WorkType]int]()
	requireSelected(t, rank(t, r), "comfort", "expansion", "research", "resource")
	r.Labor = domain.Known(map[WorkType]int{})
	r.Concerns = append(r.Concerns, DevelopmentConcern{ID: "monitor", Priority: 4, Deficit: domain.Known(0.1)})
	s = rank(t, r)
	requireSelected(t, s, "comfort", "expansion", "monitor", "research", "resource")
	r.Labor = domain.Known(map[WorkType]int{WorkResearch: 1})
	requireSelected(t, rank(t, r), "comfort", "expansion", "research", "monitor", "resource")
	r.Labor = domain.Known(map[WorkType]int{"": 1})
	if _, err := RankDevelopment(r); err == nil {
		t.Fatal("invalid labor census accepted")
	}
}

func TestEquipmentDevelopmentCanUseBuilderBeforeTailoringExists(t *testing.T) {
	r := developmentFixture()
	r.Labor = domain.Known(map[WorkType]int{WorkConstruction: 1})
	r.Concerns = []DevelopmentConcern{{ID: MaintainEquipment, Priority: 3, Deficit: domain.Known(1.0), Labor: ConcernLabor(MaintainEquipment)}}
	requireSelected(t, rank(t, r), MaintainEquipment)
}
