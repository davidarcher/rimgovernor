package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestDevelopmentWeightsDefaultAndValidation(t *testing.T) {
	r := developmentFixture()
	base := rank(t, r)
	r.Weights = DefaultDevelopmentWeights()
	if explicit := rank(t, r); explicit.Rows[0].Score != base.Rows[0].Score || explicit.Rows[0].Concern != "storage" || base.Rows[0].Score != 100 {
		t.Fatal(base.Rows, explicit.Rows)
	}
	r.Weights.Deficit = 0
	// Storage committed its selection, so it keeps age and hysteresis.
	base.Rows[0].Committed = true
	r.Tick, r.Previous = 2600, base
	if s := rank(t, r); s.Rows[0].Concern != "storage" || s.Rows[0].Score != 22.5 {
		t.Fatal("age and hysteresis alone", s.Rows)
	}
	for _, bad := range []DevelopmentWeights{{Deficit: 100}, {Deficit: -1, AgeTicks: 1}, {AgeTicks: 1, Risk: 1e7}} {
		r.Weights = bad
		if _, err := RankDevelopment(r); err == nil {
			t.Fatal("invalid weights accepted", bad)
		}
	}
}

// Contested scarce labor lowers a goal's order so an uncontested goal with a
// smaller deficit can take the slot first; the penalty is order-independent.
func TestDevelopmentBottleneckOrdering(t *testing.T) {
	r := developmentFixture()
	r.Labor = domain.Known(map[WorkType]int{WorkConstruction: 1, WorkResearch: 1})
	r.Concerns = []DevelopmentConcern{
		{ID: "comfort", Priority: 4, Deficit: domain.Known(0.6), Labor: ConcernLabor(EnsureComfort)},
		{ID: "expansion", Priority: 4, Deficit: domain.Known(0.5), Labor: ConcernLabor(MaintainHousing)},
		{ID: "research", Priority: 4, Deficit: domain.Known(0.4), Labor: ConcernLabor(EnsureResearch)},
	}
	s := rank(t, r)
	if s.Rows[0].Concern != "comfort" || s.Rows[0].Score != 45 || s.Rows[1].Concern != "research" || s.Rows[1].Score != 40 || s.Rows[2].Concern != "expansion" || s.Rows[2].Score != 35 {
		t.Fatal(s.Rows)
	}
	// Contested labor only lowers the order; every goal is admitted.
	requireSelected(t, s, "comfort", "research", "expansion")
	r.Weights = DefaultDevelopmentWeights()
	r.Weights.Bottleneck = 0
	s = rank(t, r)
	if s.Rows[0].Concern != "comfort" || s.Rows[1].Concern != "expansion" {
		t.Fatal(s.Rows)
	}
	requireSelected(t, s, "comfort", "expansion", "research")
	// A committed player construction project leaves nothing free: ratio 0.
	r.Weights = DevelopmentWeights{}
	r.Commitments = []Commitment{{Concern: "player-room", Priority: 3, Progress: developmentProgress(t), Labor: LaborProfile{WorkConstruction}}}
	s = rank(t, r)
	if s.Rows[0].Concern != "research" || s.Rows[1].Concern != "comfort" || s.Rows[1].Score != 30 {
		t.Fatal(s.Rows)
	}
}

func TestDevelopmentRiskPenalisesAndDefers(t *testing.T) {
	r := developmentFixture()
	r.Concerns = []DevelopmentConcern{
		{ID: "safe", Priority: 4, Deficit: domain.Known(0.5), Risk: domain.Known(0.0)},
		{ID: "cold", Priority: 4, Deficit: domain.Known(0.6), Risk: domain.Known(0.5)},
		{ID: "fallout", Priority: 4, Deficit: domain.Known(1.0), Risk: domain.Known(1.0)},
		{ID: "unmeasured", Priority: 4, Deficit: domain.Known(0.3)},
	}
	s := rank(t, r)
	rows := map[ConcernID]DevelopmentRow{}
	for _, row := range s.Rows {
		rows[row.Concern] = row
	}
	if rows["cold"].Score != 40 || rows["safe"].Score != 50 || rows["unmeasured"].Score != 30 {
		t.Fatal(s.Rows)
	}
	if rows["fallout"].Reason != DevelopmentRisk || rows["fallout"].Selected || rows["fallout"].Score != 60 {
		t.Fatal(rows["fallout"])
	}
	requireSelected(t, s, "safe", "cold", "unmeasured")
	if err := ValidateDevelopmentState(s); err != nil {
		t.Fatal(err)
	}
	r.Concerns[0].Risk = domain.Known(1.5)
	if _, err := RankDevelopment(r); err == nil {
		t.Fatal("invalid risk accepted")
	}
	// A risk penalty never drives a score negative.
	r.Concerns[0].Risk, r.Weights = domain.Known(0.9), DevelopmentWeights{Deficit: 1, AgeTicks: 2500, Risk: 100}
	if s := rank(t, r); s.Rows[len(s.Rows)-1].Score != 0 {
		t.Fatal(s.Rows)
	}
}

func TestRoundsDevelopmentRiskFromObservedHazards(t *testing.T) {
	f := stableRounds()
	if RoundsDevelopmentRisk(MaintainHousing, f, RoundsLatches{}) != domain.Known(0.0) || RoundsDevelopmentRisk(EnsureResearch, f, RoundsLatches{Cold: true}) != domain.Known(0.0) {
		t.Fatal("no hazard must be zero risk")
	}
	if RoundsDevelopmentRisk(EnsureComfort, f, RoundsLatches{Hot: true}) != domain.Known(0.5) || RoundsDevelopmentRisk(MaintainResource, f, RoundsLatches{Cold: true}) != domain.Known(0.5) {
		t.Fatal("temperature latch must halve outdoor priority")
	}
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "1", Definition: "ToxicFallout"}})
	if RoundsDevelopmentRisk(MaintainHousing, f, RoundsLatches{}) != domain.Known(1.0) || RoundsDevelopmentRisk(EnsureResearch, f, RoundsLatches{}) != domain.Known(0.0) {
		t.Fatal("outdoor hazard must defer outdoor work only")
	}
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "1", Definition: "Eclipse"}})
	if RoundsDevelopmentRisk(MaintainHousing, f, RoundsLatches{}) != domain.Known(0.0) {
		t.Fatal("non-hazard condition")
	}
}
