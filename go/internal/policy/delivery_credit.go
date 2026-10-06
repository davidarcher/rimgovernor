package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// LedgerWindowDays is the trailing window a channel's observed deliveries are
// compared with its expected ones over. A channel that delivers in bursts is
// judged over at least one of its cycles plus LedgerReplantDays (CreditChannel.CycleDays).
const (
	LedgerWindowDays  = 3.0
	LedgerReplantDays = 1.0
	// CreditMoveThreshold is the factor change that earns a food_credit row.
	CreditMoveThreshold = 0.1
)

// Reasons of a CreditChange.
const (
	CreditWindow     = "window"     // the trailing window moved the factor or the state
	CreditRebaseline = "rebaseline" // the ledger's load token changed; the factor is held
	CreditLost       = "lost"       // the ledger dropped rows; the factor is held
)

// CreditChannel is one ledger counter group as the channels the census
// committed describe it: the rate they expect, risk-adjusted.
type CreditChannel struct {
	Source string
	// Expected is the summed risk-adjusted nutrition per day.
	Expected float64
	// LeadDays is the shortest lead of the group's channels; CycleDays is the
	// days between a bursty channel's deliveries (a crop's grow days).
	LeadDays, CycleDays float64
}

// CreditInput is one Round's read of the delivery ledger. Delivered is the
// cumulative nutrition per counter group within the load token Epoch; Lost is
// the ledger's count of rows beyond its key bound. An unavailable ledger is
// Known false and holds every factor.
type CreditInput struct {
	Tick      domain.Tick
	Epoch     string
	Known     bool
	Delivered map[string]float64
	Lost      uint64
}

// CreditResult is a group's standing: Factor in [0,1] scales its expected rate
// and State is Designated until the ledger shows deliveries, then Delivering.
type CreditResult struct {
	Factor float64
	State  CandidateState
}

// CreditChange is a food_credit flight row: a factor that moved by
// CreditMoveThreshold or more, a state change, or a held factor explained.
type CreditChange struct {
	Source, Reason, State      string
	Expected, Observed, Factor float64
	WindowDays                 float64
}

type creditSample struct {
	tick      domain.Tick
	delivered float64
}

type creditHistory struct {
	// armed is set once the plan opened the group or it was seen delivering;
	// opened and lead (days) are when, and the lead then.
	armed     bool
	opened    domain.Tick
	lead      float64
	leadNow   float64
	samples   []creditSample
	factor    float64
	reported  float64
	delivered bool
	evaluated bool
	expected  float64
	observed  float64
	window    float64
}

// DeliveryCredit credits each ledger counter group by what it delivers: the
// factor is observed over expected nutrition across a trailing window, in
// [0,1], starting at 1. It lives in memory only; a restart starts every group
// again at factor 1 and Designated, which the ledger corrects within a window.
type DeliveryCredit struct {
	epoch   string
	lost    uint64
	last    domain.Tick
	seen    bool
	groups  map[string]*creditHistory
	results map[string]CreditResult
	changes []CreditChange
}

func creditWindowDays(c CreditChannel) float64 {
	return math.Max(LedgerWindowDays, c.CycleDays+LedgerReplantDays)
}

// Observe folds one Round's ledger read into the factors and returns them by
// counter group. Groups absent from channels are forgotten. A tick already
// folded returns the standing results unchanged.
func (d *DeliveryCredit) Observe(in CreditInput, channels []CreditChannel) map[string]CreditResult {
	if d.seen && in.Tick <= d.last {
		return d.results
	}
	d.seen, d.last = true, in.Tick
	if d.groups == nil {
		d.groups = map[string]*creditHistory{}
	}
	rebaselined, lostRaised := false, false
	if in.Known {
		rebaselined = d.epoch != "" && in.Epoch != d.epoch
		lostRaised = !rebaselined && in.Lost > d.lost
		d.epoch, d.lost = in.Epoch, in.Lost
	}
	live := map[string]bool{}
	d.results = make(map[string]CreditResult, len(channels))
	sorted := append([]CreditChannel(nil), channels...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].Source < sorted[j].Source })
	for _, c := range sorted {
		live[c.Source] = true
		h := d.groups[c.Source]
		if h == nil {
			h = &creditHistory{factor: 1, reported: 1}
			d.groups[c.Source] = h
		}
		window := creditWindowDays(c)
		h.leadNow = math.Max(0, c.LeadDays)
		wasDelivering := h.delivered
		reason := CreditWindow
		cum, present := in.Delivered[c.Source]
		switch {
		case !in.Known:
		case rebaselined:
			h.samples, reason = nil, CreditRebaseline
		case !present && in.Lost > 0:
			reason = CreditLost
			if lostRaised {
				h.samples = nil
			}
		default:
			if cum > 0 {
				h.delivered = true
				d.arm(h, in.Tick)
			}
			h.samples = append(h.samples, creditSample{tick: in.Tick, delivered: cum})
			d.evaluate(h, c, in.Tick, window, cum)
		}
		state := CandidateDesignated
		if h.delivered {
			state = CandidateDelivering
		}
		d.results[c.Source] = CreditResult{Factor: h.factor, State: state}
		held := reason != CreditWindow && h.evaluated && (rebaselined || lostRaised)
		moved := math.Abs(h.factor-h.reported) >= CreditMoveThreshold
		if held || moved || h.delivered != wasDelivering {
			h.reported = h.factor
			d.changes = append(d.changes, CreditChange{Source: c.Source, Reason: reason, State: string(state),
				Expected: h.expected, Observed: h.observed, Factor: h.factor, WindowDays: window})
		}
	}
	for source := range d.groups {
		if !live[source] {
			delete(d.groups, source)
		}
	}
	return d.results
}

func (d *DeliveryCredit) arm(h *creditHistory, now domain.Tick) {
	if !h.armed {
		h.armed, h.opened, h.lead = true, now, h.leadNow
	}
}

// Opened tells the credit which counter groups a plan just opened: a group is
// judged only from its lead plus one window after the plan opened it, so a
// candidate nobody asked for is never penalised for not delivering.
func (d *DeliveryCredit) Opened(plan FoodPlan, channels []FoodChannel, now domain.Tick) {
	if d == nil {
		return
	}
	source := map[string]string{}
	for _, c := range channels {
		if c.Source != "" {
			source[string(c.Kind)+"/"+c.ID] = c.Source
		}
	}
	for _, e := range plan.Portfolio {
		if e.Decision != FoodPlanOpen {
			continue
		}
		if h := d.groups[source[string(e.Channel.Kind)+"/"+e.Channel.ID]]; h != nil {
			d.arm(h, now)
		}
	}
}

// evaluate sets the group's factor from the newest sample at least one window
// old that is not older than the end of the group's lead; with none (the
// warm-up), no expected delivery or a group no plan opened the factor is held.
func (d *DeliveryCredit) evaluate(h *creditHistory, c CreditChannel, now domain.Tick, window, cum float64) {
	if !h.armed {
		return
	}
	cutoff := now - domain.Tick(window*float64(domain.TicksPerDay))
	start := h.opened + domain.Tick(h.lead*float64(domain.TicksPerDay))
	base := -1
	for i, s := range h.samples {
		if s.tick <= cutoff && s.tick >= start {
			base = i
		}
	}
	if base < 0 {
		return
	}
	h.samples = h.samples[base:]
	expected := c.Expected * float64(now-h.samples[0].tick) / float64(domain.TicksPerDay)
	if expected <= 0 {
		return
	}
	observed := math.Max(0, cum-h.samples[0].delivered)
	h.expected, h.observed, h.window = expected, observed, window
	h.factor = math.Min(1, observed/expected)
	h.evaluated = true
}

// Drain returns the changes since the last call, in source order.
func (d *DeliveryCredit) Drain() []CreditChange {
	out := d.changes
	d.changes = nil
	return out
}

// Apply credits the channels: a committed channel with a counter group takes
// the group's state and its nutrition rate is scaled by the group's factor.
// A nil DeliveryCredit applies nothing. Channels without a Source, and Closed
// ones, are returned as they are.
func (d *DeliveryCredit) Apply(channels []FoodChannel, in CreditInput) []FoodChannel {
	if d == nil {
		return channels
	}
	byGroup := map[string]*CreditChannel{}
	for _, c := range channels {
		if c.Source == "" || !committed(c) {
			continue
		}
		rate, known := c.NutritionPerDay.Value()
		if !known || math.IsNaN(rate) || math.IsInf(rate, 0) {
			continue
		}
		g := byGroup[c.Source]
		if g == nil {
			g = &CreditChannel{Source: c.Source, LeadDays: math.Inf(1)}
			byGroup[c.Source] = g
		}
		risk := 0.0
		for _, r := range c.Risk {
			risk += r.Weight
		}
		g.Expected += rate * math.Max(0, 1-risk)
		if lead, ok := c.LeadDays.Value(); ok && lead < g.LeadDays {
			g.LeadDays = math.Max(0, lead)
		}
		if cycle := cycleDays(c); cycle > g.CycleDays {
			g.CycleDays = cycle
		}
	}
	groups := make([]CreditChannel, 0, len(byGroup))
	for _, g := range byGroup {
		if math.IsInf(g.LeadDays, 1) {
			g.LeadDays = 0
		}
		groups = append(groups, *g)
	}
	results := d.Observe(in, groups)
	out := make([]FoodChannel, len(channels))
	copy(out, channels)
	for i, c := range out {
		r, ok := results[c.Source]
		if !ok || c.Source == "" || !committed(c) {
			continue
		}
		c.Open, c.Designated = domain.Known(r.State == CandidateDelivering), domain.Known(r.State == CandidateDesignated)
		if rate, known := c.NutritionPerDay.Value(); known && !math.IsNaN(rate) && !math.IsInf(rate, 0) {
			c.NutritionPerDay = domain.Known(rate * r.Factor)
		}
		out[i] = c
	}
	return out
}

func committed(c FoodChannel) bool {
	s, known := c.State().Value()
	return known && (s == CandidateDesignated || s == CandidateDelivering)
}

// cycleDays is the days between a crop channel's deliveries, from its Terms.
func cycleDays(c FoodChannel) float64 {
	for _, t := range c.Terms {
		if t.Name == "grow_days" {
			return t.Value
		}
	}
	return 0
}
