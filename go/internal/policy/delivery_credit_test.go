package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const creditDay = domain.Tick(domain.TicksPerDay)

// creditRun feeds one group a constant expected rate and a cumulative counter.
type creditRun struct {
	d       DeliveryCredit
	epoch   string
	lost    uint64
	rate    float64
	lead    float64
	cum     float64
	armed   bool
	changes []CreditChange
}

func newCreditRun(rate, lead float64) *creditRun {
	return &creditRun{epoch: "a", rate: rate, lead: lead}
}

// step advances to day with `deliver` more nutrition counted, opening the
// group on the first step unless asked not to.
func (r *creditRun) step(day float64, deliver float64) CreditResult {
	r.cum += deliver
	in := CreditInput{Tick: domain.Tick(day * float64(creditDay)), LoadToken: r.epoch, Known: true, Lost: r.lost, Delivered: map[string]float64{"g": r.cum}}
	res := r.d.Observe(in, []CreditChannel{{Source: "g", Expected: r.rate, LeadDays: r.lead}})
	if r.armed {
		r.d.groups["g"].armOnce(in.Tick)
	}
	r.changes = append(r.changes, r.d.Drain()...)
	return res["g"]
}

func (h *creditHistory) armOnce(now domain.Tick) {
	if !h.armed {
		h.armed, h.opened, h.lead = true, now, h.leadNow
	}
}

func TestCreditFactorIsObservedOverExpectedAcrossTheWindow(t *testing.T) {
	r := newCreditRun(10, 0)
	r.armed = true
	for day := 0.0; day <= 3; day++ {
		r.step(day, 5) // half the expected 10 per day
	}
	// The base sample is day 0 (5 counted): 15 more over 3.5 days expecting 35.
	if got := r.step(3.5, 0); !near(got.Factor, 15.0/35.0) {
		t.Fatalf("factor = %v", got.Factor)
	}
}

func TestCreditWarmUpHoldsUntilLeadPlusOneWindow(t *testing.T) {
	r := newCreditRun(10, 2)
	r.armed = true
	for day := 0.0; day < 5; day++ { // lead 2 + window 3 = 5 days
		if got := r.step(day, 0); got.Factor != 1 || got.State != CandidateDesignated {
			t.Fatalf("day %v: factor %v state %v before the warm-up ends", day, got.Factor, got.State)
		}
	}
	if got := r.step(5, 0); got.Factor != 0 {
		t.Fatalf("factor after the warm-up with no deliveries = %v", got.Factor)
	}
}

func TestCreditUnopenedGroupIsNeverPenalised(t *testing.T) {
	r := newCreditRun(10, 0)
	for day := 0.0; day < 20; day++ {
		if got := r.step(day, 0); got.Factor != 1 {
			t.Fatalf("day %v: an unopened group's factor = %v", day, got.Factor)
		}
	}
}

func TestCreditOpenedByPlanArmsAtThatTick(t *testing.T) {
	var d DeliveryCredit
	z1 := FoodCandidate(CandidateCrop, "z1", domain.Known(10.0))
	z1.Source, z1.State, z1.LeadDays = "crop:z1", domain.Known(CandidateDesignated), domain.Known(0.0)
	ch := []SupplyCandidate{z1}
	in := func(day int) CreditInput {
		return CreditInput{Tick: domain.Tick(day) * creditDay, LoadToken: "a", Known: true, Delivered: map[string]float64{}}
	}
	d.Apply(ch, in(0))
	d.Apply(ch, in(5)) // unopened: held at 1
	d.Opened(FoodPlan{Portfolio: []FoodPlanEntry{{Channel: ch[0], Decision: FoodPlanOpen}}}, ch, 5*creditDay)
	out := d.Apply(ch, in(8)) // opened at day 5: the window (grow 0 -> 3 days) ends at day 8
	if rate, _ := out[0].Nutrition().PerDay.Value(); rate != 0 {
		t.Fatalf("an opened group that delivered nothing for a window keeps credit %v", rate)
	}
}

func TestCreditClampAndRecovery(t *testing.T) {
	r := newCreditRun(1, 0)
	r.armed = true
	r.step(0, 0)
	if got := r.step(4, 100); got.Factor != 1 {
		t.Fatalf("over-delivery is clamped to 1, got %v", got.Factor)
	}
	r.step(5, 0)
	r.step(6, 0)
	r.step(7, 0)
	if got := r.step(8, 0); got.Factor != 0 {
		t.Fatalf("a window with no delivery takes the factor to 0, got %v", got.Factor)
	}
	r.step(9, 3)
	if got := r.step(10, 0); got.Factor <= 0 {
		t.Fatalf("deliveries in the window raise the factor again, got %v", got.Factor)
	}
}

func TestCreditStateIsSticky(t *testing.T) {
	r := newCreditRun(1, 0)
	if got := r.step(0, 0); got.State != CandidateDesignated {
		t.Fatalf("state = %v", got.State)
	}
	if got := r.step(1, 2); got.State != CandidateDelivering {
		t.Fatalf("deliveries make the group delivering, got %v", got.State)
	}
	if got := r.step(9, 0); got.State != CandidateDelivering {
		t.Fatalf("a group stays delivering while nothing arrives, got %v", got.State)
	}
}

func TestCreditRebaselineHoldsTheFactor(t *testing.T) {
	r := newCreditRun(1, 0)
	r.armed = true
	r.step(0, 0)
	r.step(3, 0)
	if got := r.step(4, 0); got.Factor != 0 {
		t.Fatalf("setup: factor %v", got.Factor)
	}
	r.epoch, r.cum = "b", 0 // a load: counters restart
	r.changes = nil
	if got := r.step(5, 0); got.Factor != 0 || got.State != CandidateDesignated {
		t.Fatalf("the load holds the last factor and state, got %+v", got)
	}
	if len(r.changes) != 1 || r.changes[0].Reason != CreditRebaseline {
		t.Fatalf("changes = %+v", r.changes)
	}
	// The new epoch's first samples cannot be diffed against the old counters.
	if got := r.step(6, 3); got.Factor != 0 {
		t.Fatalf("one sample is not a window, got %v", got.Factor)
	}
}

func TestCreditLostRowsHoldAGroupWithoutARow(t *testing.T) {
	var d DeliveryCredit
	in := func(day int, lost uint64, delivered map[string]float64) CreditInput {
		return CreditInput{Tick: domain.Tick(day) * creditDay, LoadToken: "a", Known: true, Lost: lost, Delivered: delivered}
	}
	g := []CreditChannel{{Source: "g", Expected: 1}}
	d.Observe(in(0, 0, nil), g)
	d.groups["g"].armOnce(0)
	d.Observe(in(4, 0, nil), g)
	if d.results["g"].Factor != 0 {
		t.Fatalf("setup: %v", d.results["g"])
	}
	d.groups["g"].factor = 1
	d.Drain()
	// Lost rows: no counter for g may be zero or dropped, so the factor holds.
	if got := d.Observe(in(8, 5, nil), g)["g"]; got.Factor != 1 {
		t.Fatalf("lost rows hold the factor, got %v", got.Factor)
	}
	changes := d.Drain()
	if len(changes) != 0 && changes[0].Reason != CreditLost {
		t.Fatalf("changes = %+v", changes)
	}
}

func TestCreditUnavailableLedgerHolds(t *testing.T) {
	r := newCreditRun(1, 0)
	r.armed = true
	r.step(0, 0)
	got := r.d.Observe(CreditInput{Tick: 9 * creditDay}, []CreditChannel{{Source: "g", Expected: 1}})["g"]
	if got.Factor != 1 {
		t.Fatalf("an unknown ledger holds the factor, got %v", got.Factor)
	}
}

func TestCreditSameTickIsFoldedOnce(t *testing.T) {
	r := newCreditRun(1, 0)
	r.armed = true
	r.step(0, 0)
	r.step(4, 0)
	before := len(r.d.groups["g"].samples)
	r.step(4, 0)
	if len(r.d.groups["g"].samples) != before {
		t.Fatal("a second fold of the same tick added a sample")
	}
}

func TestCreditMoveEmitsOneRowPerTenthAndStateChange(t *testing.T) {
	r := newCreditRun(10, 0)
	r.armed = true
	r.step(0, 0)
	r.step(1, 10) // delivering: a state change row
	r.step(2, 10)
	r.step(3, 10)
	r.step(4, 10)
	if len(r.changes) != 1 || r.changes[0].State != string(CandidateDelivering) || r.changes[0].Reason != CreditWindow {
		t.Fatalf("changes = %+v", r.changes)
	}
	r.step(5, 10)
	if len(r.changes) != 1 {
		t.Fatalf("a steady factor wrote a row: %+v", r.changes[1:])
	}
	r.step(6, 0)
	r.step(7, 0)
	if len(r.changes) < 2 || r.changes[1].Factor >= 1-CreditMoveThreshold {
		t.Fatalf("a drop of %v or more writes a row: %+v", CreditMoveThreshold, r.changes)
	}
}

func TestCreditApplyAttributesByCounterGroupAndRisk(t *testing.T) {
	var d DeliveryCredit
	forage := func(id string) SupplyCandidate {
		c := FoodCandidate(CandidateForage, id, domain.Known(4.0))
		c.Source, c.LeadDays, c.State = "forage:Berry", domain.Known(0.0), domain.Known(CandidateDesignated)
		return c
	}
	risky := FoodCandidate(CandidateCrop, "z", domain.Known(10.0))
	risky.Source, risky.LeadDays, risky.State, risky.Risk = "crop:z", domain.Known(0.0), domain.Known(CandidateDesignated), []CandidateRisk{{CandidateFallout, 0.5}}
	closed := FoodCandidate(CandidateHunt, "deer", domain.Known(9.0))
	closed.State = domain.Known(CandidateClosed)
	channels := []SupplyCandidate{forage("a"), forage("b"), risky, closed}
	in := CreditInput{Tick: 0, LoadToken: "e", Known: true, Delivered: map[string]float64{"forage:Berry": 1}}
	out := d.Apply(channels, in)
	for _, i := range []int{0, 1} {
		if out[i].Source != "forage:Berry" || out[i].State != domain.Known(CandidateDelivering) {
			t.Fatalf("a forage channel takes its def's state from the ledger: %+v", out[i])
		}
	}
	if out[2].State == domain.Known(CandidateDelivering) || out[3].State != channels[3].State {
		t.Fatalf("a crop with no delivery stays designated and a closed hunt is untouched: %+v %+v", out[2], out[3])
	}
	for i := range channels {
		if channels[i].State == domain.Known(CandidateDelivering) {
			t.Fatal("Apply wrote into its input")
		}
	}
	if len(d.groups) != 2 {
		t.Fatalf("groups = %d, want the forage def and the crop zone", len(d.groups))
	}
	// Expected is risk-adjusted: the crop expects 5, the forage def 8 (two plants).
	d.groups["forage:Berry"].armOnce(0)
	d.groups["crop:z"].armOnce(0)
	d.Apply(channels, CreditInput{Tick: 3 * creditDay, LoadToken: "e", Known: true, Delivered: map[string]float64{"forage:Berry": 25, "crop:z": 5}})
	got := d.results
	if !near(got["forage:Berry"].Factor, 1.0) || !near(got["crop:z"].Factor, 5.0/15.0) {
		t.Fatalf("results = %+v", got)
	}
}

func TestCreditApplyScalesTheRateAndNilIsANoOp(t *testing.T) {
	water := FoodCandidate(CandidateFishing, "water-0-0", domain.Known(8.0))
	water.Source, water.LeadDays, water.State = "fish:0,0", domain.Known(0.0), domain.Known(CandidateDesignated)
	ch := []SupplyCandidate{water}
	var nilCredit *DeliveryCredit
	if out := nilCredit.Apply(ch, CreditInput{}); &out[0] != &ch[0] {
		t.Fatal("a nil credit must return the channels as given")
	}
	var d DeliveryCredit
	d.Apply(ch, CreditInput{Tick: 0, LoadToken: "e", Known: true})
	out := d.Apply(ch, CreditInput{Tick: 4 * creditDay, LoadToken: "e", Known: true})
	if rate, _ := out[0].Nutrition().PerDay.Value(); rate != 8 {
		t.Fatalf("a group no plan opened keeps its rate, got %v", rate)
	}
}
