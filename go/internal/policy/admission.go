// Package policy admits explicit building work from complete native observations.
// Admission is not dispatch authorization; Hands must refresh native safety checks.
package policy

import (
	"errors"
	"fmt"
	"maps"
	"math"
	"sort"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type Resource string
type Amount struct {
	Resource Resource
	Count    int64
}
type Stock struct {
	Resource  Resource
	Available domain.Fact[int64]
}
type Bounds struct{ Width, Height int32 }

// Purpose is the spending class a building candidate is admitted under.
// Routine work spends stock it can see; Defense work may spend under a
// DefenseOnly rule; Shelter work is a shell whose frames RimWorld places
// regardless of stock and holds natively for materials, so InsufficientStock
// is not a refusal for it (#602): only a Stop/DefenseOnly spending rule or an
// operator reserve on the resource keeps its walls unadmitted. The purpose
// is recorded with the admission and applied again at dispatch.
type Purpose string

const (
	Routine Purpose = "routine"
	Defense Purpose = "defense"
	Shelter Purpose = "shelter"
)

// ValidPurpose reports whether p names a spending class.
func ValidPurpose(p Purpose) bool {
	return p == Routine || p == Defense || p == Shelter
}

type Spending string

const (
	Allow       Spending = "allow"
	Stop        Spending = "stop"
	DefenseOnly Spending = "defense_only"
)

type ResourceRule struct {
	Resource Resource
	Reserve  int64
	Spending Spending
}
type Dependency struct {
	Action    domain.ActionID
	Completed domain.Fact[bool]
	Snapshot  domain.GenerationSnapshot
	Tick      domain.Tick
}

// Preview is bound to the exact action, including resolved anchor/rotation/stuff.
// SafeToPlace includes normal-game legality and absence of destructive side effects;
// CanPlace alone does not establish that existing objects will be preserved.
type Preview struct {
	Action                               domain.Action
	Snapshot                             domain.GenerationSnapshot
	Tick                                 domain.Tick
	CanPlace, SafeToPlace, MadeFromStuff domain.Fact[bool]
	WatchCellsAccessible                 domain.Fact[bool]
	// WindBlockedCells is, for a wind turbine, how many cells of its native
	// catch zone are obstructed (roofed, off the map or holding a
	// wind-blocking thing); unknown for every other definition.
	WindBlockedCells domain.Fact[int32]
	Footprint        domain.Fact[[]domain.Cell]
	Costs            domain.Fact[[]Amount]
	// Blockers lists what native reports the placement would disturb, so a
	// method that replaces a building on purpose (a door cut into a wall,
	// issue #6 slice 5) can tell that deliberate replacement from an
	// accidental one. Admission itself still reads SafeToPlace.
	Blockers []PlacementBlocker
}

// PlacementBlocker is one thing the previewed placement would disturb.
type PlacementBlocker struct {
	Category                           string
	Wiped, Blueprint, Frame, Cancelled bool
}
type Candidate struct {
	Action       domain.Action
	Progress     domain.Progress
	Priority     int32
	Purpose      Purpose
	Dependencies []Dependency
	Preview      Preview
}
type StockObservation struct {
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Values   []Stock
	// NativeConstruction means Available already subtracts the complete native
	// census of remaining blueprint/frame costs, including other controllers.
	NativeConstruction bool
}

// Reservation retains the original complete costs even when fresh completion
// evidence releases its budget. Cancelled uncertain effects keep their footprint;
// completed work yields geometry to fresh native placement observations.
type Reservation struct {
	Action    domain.Action
	Progress  domain.Progress
	Snapshot  domain.GenerationSnapshot
	Costs     []Amount
	Footprint []domain.Cell
}

// Request candidates belong to Current's single plan. Held reservations may be
// from other plans in the same colony/map/load. Evidence must be at CurrentTick.
type Request struct {
	Current     domain.GenerationSnapshot
	CurrentTick domain.Tick
	Bounds      domain.Fact[Bounds]
	Candidates  []Candidate
	Stock       StockObservation
	Held        []Reservation
	Rules       []ResourceRule
}

// Input owns deep copies and exposes no mutable observations.
type Input struct{ request Request }
type Reason string

const (
	NotReady           Reason = "not_ready"
	AlreadyReserved    Reason = "already_reserved"
	UnknownFacts       Reason = "unknown_facts"
	StaleFacts         Reason = "stale_facts"
	UnsafePlacement    Reason = "unsafe_placement"
	UnsafeThreat       Reason = "unsafe_threat"
	CriticalMedical    Reason = "critical_medical"
	MaterialRequired   Reason = "explicit_material_required"
	DependencyBlocked  Reason = "dependency_incomplete"
	GeometryBlocked    Reason = "geometry_conflict"
	SpendingBlocked    Reason = "spending_policy"
	InsufficientStock  Reason = "insufficient_stock"
	InvalidHeld        Reason = "held_reservation_unverifiable"
	ArithmeticOverflow Reason = "arithmetic_overflow"
	// NoDevelopmentSlot refuses a routine goal method whose goal the routine
	// review has not selected for development (store.ErrNotAdmitted).
	NoDevelopmentSlot Reason = "no_development_slot"
)

type Refusal struct {
	Action   domain.ActionID
	Reason   Reason
	Resource Resource
}
type Decision struct {
	Admitted []Reservation
	Held     []Reservation
	Refused  []Refusal
}

func NewInput(r Request) (Input, error) {
	if err := r.Current.Validate(); err != nil {
		return Input{}, err
	}
	if r.CurrentTick < 0 {
		return Input{}, errors.New("negative current tick")
	}
	if len(r.Candidates) > 256 || len(r.Held) > 256 || len(r.Stock.Values) > 256 || len(r.Rules) > 256 {
		return Input{}, errors.New("admission collection exceeds 256 rows")
	}
	if b, known := r.Bounds.Value(); known && (b.Width <= 0 || b.Height <= 0) {
		return Input{}, errors.New("invalid observed map bounds")
	}
	r.Candidates = append([]Candidate(nil), r.Candidates...)
	seen := map[domain.ActionID]bool{}
	for i, c := range r.Candidates {
		if err := validAction(c.Action, c.Progress); err != nil {
			return Input{}, err
		}
		if seen[c.Action.ID()] {
			return Input{}, errors.New("duplicate candidate identity")
		}
		seen[c.Action.ID()] = true
		if c.Progress.View().Plan != r.Current.Plan || c.Progress.View().Revision != r.Current.Revision {
			return Input{}, errors.New("candidate belongs to a different plan revision")
		}
		if !ValidPurpose(c.Purpose) {
			return Input{}, errors.New("invalid building purpose")
		}
		if len(c.Dependencies) > 256 {
			return Input{}, errors.New("too many dependencies")
		}
		deps := map[domain.ActionID]bool{}
		for _, dep := range c.Dependencies {
			if strings.TrimSpace(string(dep.Action)) == "" || dep.Action == c.Action.ID() || deps[dep.Action] {
				return Input{}, errors.New("invalid or duplicate dependency identity")
			}
			deps[dep.Action] = true
		}
		c.Dependencies = append([]Dependency(nil), c.Dependencies...)
		if costs, known := c.Preview.Costs.Value(); known {
			if err := validAmounts(costs); err != nil {
				return Input{}, err
			}
			c.Preview.Costs = domain.Known(copyAmounts(costs))
		}
		if cells, known := c.Preview.Footprint.Value(); known {
			if len(cells) > 4096 {
				return Input{}, errors.New("footprint exceeds native cell bound")
			}
			c.Preview.Footprint = domain.Known(append([]domain.Cell(nil), cells...))
		}
		r.Candidates[i] = c
	}
	r.Held = append([]Reservation(nil), r.Held...)
	heldIDs := map[domain.ActionID]bool{}
	for i, h := range r.Held {
		if err := validAction(h.Action, h.Progress); err != nil {
			return Input{}, err
		}
		if heldIDs[h.Action.ID()] {
			return Input{}, errors.New("duplicate held identity")
		}
		heldIDs[h.Action.ID()] = true
		if err := h.Snapshot.Validate(); err != nil {
			return Input{}, err
		}
		v := h.Progress.View()
		if h.Snapshot.Plan != v.Plan || h.Snapshot.Revision != v.Revision ||
			(v.Stage == domain.Prepared || v.Attempt > 0) && !sameWorld(h.Snapshot, v.Snapshot) {
			return Input{}, errors.New("held reservation scope differs from its progress")
		}
		for _, candidate := range r.Candidates {
			if candidate.Action.ID() == h.Action.ID() && candidate.Action != h.Action {
				return Input{}, errors.New("held action identity reused for different intent")
			}
		}
		if err := validAmounts(h.Costs); err != nil {
			return Input{}, err
		}
		if len(h.Footprint) > 4096 {
			return Input{}, errors.New("held footprint too large")
		}
		r.Held[i] = copyReservation(h)
	}
	r.Stock.Values = append([]Stock(nil), r.Stock.Values...)
	resources := map[Resource]bool{}
	for _, stock := range r.Stock.Values {
		if !validResource(stock.Resource) || resources[stock.Resource] {
			return Input{}, errors.New("invalid or duplicate stock resource")
		}
		resources[stock.Resource] = true
		if n, known := stock.Available.Value(); known && n < 0 {
			return Input{}, errors.New("negative stock")
		}
	}
	r.Rules = append([]ResourceRule(nil), r.Rules...)
	if err := ValidateResourceRules(r.Rules); err != nil {
		return Input{}, err
	}
	return Input{r}, nil
}

func ValidateResourceRules(rules []ResourceRule) error {
	if len(rules) > 256 {
		return errors.New("resource policy exceeds 256 rules")
	}
	resources := map[Resource]bool{}
	for _, rule := range rules {
		if !validResource(rule.Resource) || resources[rule.Resource] || rule.Reserve < 0 {
			return errors.New("invalid or duplicate resource rule")
		}
		resources[rule.Resource] = true
		if rule.Spending != Allow && rule.Spending != Stop && rule.Spending != DefenseOnly {
			return errors.New("invalid spending mode")
		}
	}
	return nil
}
func validAction(a domain.Action, p domain.Progress) error {
	b, ok := a.Building()
	if zone, zoneOK := a.ZoneCreate(); zoneOK {
		if _, err := domain.NewZoneCreateAction(a.ID(), zone); err != nil {
			return err
		}
		if p.View().Action != a.ID() || p.View().Stage == "" {
			return errors.New("unbound zone progress")
		}
		return nil
	}
	if !ok {
		return errors.New("unsupported action family")
	}
	if _, err := domain.NewBuildingAction(a.ID(), b); err != nil {
		return err
	}
	if p.View().Action != a.ID() || p.View().Stage == "" {
		return errors.New("unbound action progress")
	}
	return nil
}
func validResource(r Resource) bool {
	return utf8.ValidString(string(r)) && strings.TrimSpace(string(r)) != "" && !strings.ContainsRune(string(r), 0) && len(r) <= 256
}
func validAmounts(costs []Amount) error {
	if len(costs) > 256 {
		return errors.New("too many resource costs")
	}
	seen := map[Resource]bool{}
	for _, a := range costs {
		if !validResource(a.Resource) || a.Count < 0 || seen[a.Resource] {
			return fmt.Errorf("invalid or duplicate cost %q", a.Resource)
		}
		seen[a.Resource] = true
	}
	return nil
}
func copyAmounts(costs []Amount) []Amount {
	result := append([]Amount(nil), costs...)
	sort.Slice(result, func(i, j int) bool { return result[i].Resource < result[j].Resource })
	return result
}
func copyReservation(h Reservation) Reservation {
	h.Costs = copyAmounts(h.Costs)
	h.Footprint = append([]domain.Cell(nil), h.Footprint...)
	sort.Slice(h.Footprint, func(i, j int) bool {
		if h.Footprint[i].X != h.Footprint[j].X {
			return h.Footprint[i].X < h.Footprint[j].X
		}
		return h.Footprint[i].Z < h.Footprint[j].Z
	})
	return h
}
func sameWorld(a, b domain.GenerationSnapshot) bool {
	return a.Colony == b.Colony && a.Map == b.Map && a.Load == b.Load
}
func released(h Reservation) bool {
	v := h.Progress.View()
	effect, known := v.Effect.Value()
	return v.Stage == domain.Cancelled && !v.Unresolved && (v.Attempt == 0 || known && effect == domain.EffectAbsent)
}
func footprint(cells []domain.Cell, a domain.Action, bounds Bounds) bool {
	if len(cells) == 0 {
		return false
	}
	building, _ := a.Building()
	anchorCell := building.Cell()
	zone, isZone := a.ZoneCreate()
	if isZone {
		anchorCell = zone.Cells()[0]
		if len(cells) != len(zone.Cells()) {
			return false
		}
	}
	anchor := false
	seen := map[domain.Cell]bool{}
	for _, c := range cells {
		if c.X < 0 || c.Z < 0 || c.X >= bounds.Width || c.Z >= bounds.Height || seen[c] {
			return false
		}
		seen[c] = true
		anchor = anchor || c == anchorCell
	}
	if isZone {
		for _, cell := range zone.Cells() {
			if !seen[cell] {
				return false
			}
		}
	}
	return anchor
}
func add(a, b int64) (int64, bool) {
	if b > math.MaxInt64-a {
		return 0, false
	}
	return a + b, true
}

func Admit(input Input) Decision {
	r := input.request
	result := Decision{}
	// Reject an unconstructed Input instead of interpreting zero facts as authority.
	if r.Current.Validate() != nil {
		return result
	}
	candidates := append([]Candidate(nil), r.Candidates...)
	sort.Slice(candidates, func(i, j int) bool {
		if candidates[i].Priority != candidates[j].Priority {
			return candidates[i].Priority > candidates[j].Priority
		}
		return candidates[i].Action.ID() < candidates[j].Action.ID()
	})
	bounds, boundsKnown := r.Bounds.Value()
	stockFresh := r.Stock.Snapshot.Matches(r.Current) && r.Stock.Tick.FreshFor(r.CurrentTick)
	stock := map[Resource]domain.Fact[int64]{}
	for _, s := range r.Stock.Values {
		stock[s.Resource] = s.Available
	}
	rules := map[Resource]ResourceRule{}
	for _, rule := range r.Rules {
		rules[rule.Resource] = rule
	}
	used := map[Resource]int64{}
	occupied := map[domain.Cell]int{}
	heldIDs := map[domain.ActionID]bool{}
	heldProblem := Reason("")
	heldInvalid, heldOverflow := false, false
	for _, h := range r.Held {
		if released(h) {
			continue
		}
		result.Held = append(result.Held, copyReservation(h))
		heldIDs[h.Action.ID()] = true
		if !sameWorld(h.Snapshot, r.Current) || !boundsKnown || !footprint(h.Footprint, h.Action, bounds) {
			heldInvalid = true
			continue
		}
		v := h.Progress.View()
		effect, observed := v.Effect.Value()
		// Settled outcomes preserve history. Fresh native facts replace old
		// reservations, accounting for actual remaining stock and placement.
		terminalStockFresh := observed && (effect == domain.EffectCompleted || effect == domain.EffectUnsuccessful) && !v.Unresolved && stockFresh && r.Stock.Tick >= v.Tick && sameWorld(v.Snapshot, r.Current)
		if terminalStockFresh {
			continue
		}
		for _, c := range h.Footprint {
			occupied[c]++
		}
		// A complete correlated inspection proves the placement write settled.
		// Fresh net stock now owns its remaining cost; keep geometry and uncertain
		// completion pinned without subtracting the original cost a second time.
		constructionTick, constructionKnown := v.ConstructionObserved.Value()
		if r.Stock.NativeConstruction && stockFresh && constructionKnown && observed && effect == domain.EffectPending && constructionTick < r.Stock.Tick && v.Tick <= r.Stock.Tick && sameWorld(v.Snapshot, r.Current) {
			continue
		}
		for _, cost := range h.Costs {
			sum, ok := add(used[cost.Resource], cost.Count)
			if !ok {
				heldOverflow = true
			} else {
				used[cost.Resource] = sum
			}
		}
	}
	if heldOverflow {
		heldProblem = ArithmeticOverflow
	}
	if heldInvalid {
		heldProblem = InvalidHeld
	}
	sort.Slice(result.Held, func(i, j int) bool { return result.Held[i].Action.ID() < result.Held[j].Action.ID() })
	for _, c := range candidates {
		budget, space, identities := used, occupied, heldIDs
		replace := -1
		if heldProblem == "" {
			for i, h := range result.Held {
				cv, hv := c.Progress.View(), h.Progress.View()
				// A Prepared attempt 0 has no write outstanding (dispatch is
				// journaled before the native call), so its own earlier hold
				// -- current or left behind by a generation that moved (#101)
				// -- is superseded by this fresh admission.
				if c.Action == h.Action && cv.Stage == domain.Prepared &&
					cv.Attempt == 0 && !cv.Unresolved && (hv.Stage == domain.Pending || hv.Stage == domain.Prepared) &&
					hv.Attempt == 0 && !hv.Unresolved {
					// Replace the hold only if fresh revalidation succeeds. Failed
					// admission must not expose its resources or geometry to rivals.
					replace = i
					budget = maps.Clone(used)
					space = maps.Clone(occupied)
					identities = maps.Clone(heldIDs)
					for _, cost := range h.Costs {
						budget[cost.Resource] -= cost.Count
					}
					for _, cell := range h.Footprint {
						space[cell]--
					}
					delete(identities, h.Action.ID())
					break
				}
			}
		}
		reason, resource := assess(c, r, bounds, boundsKnown, stockFresh, stock, rules, budget, space, identities, heldProblem)
		if reason != "" {
			result.Refused = append(result.Refused, Refusal{c.Action.ID(), reason, resource})
			continue
		}
		used, occupied, heldIDs = budget, space, identities
		if replace >= 0 {
			result.Held = append(result.Held[:replace], result.Held[replace+1:]...)
		}
		costs, _ := c.Preview.Costs.Value()
		cells, _ := c.Preview.Footprint.Value()
		for _, cost := range costs {
			used[cost.Resource] += cost.Count
		}
		for _, cell := range cells {
			occupied[cell]++
		}
		result.Admitted = append(result.Admitted, copyReservation(Reservation{c.Action, c.Progress, r.Current, costs, cells}))
	}
	return result
}
func assess(c Candidate, r Request, bounds Bounds, boundsKnown, stockFresh bool, stock map[Resource]domain.Fact[int64], rules map[Resource]ResourceRule, used map[Resource]int64, occupied map[domain.Cell]int, heldIDs map[domain.ActionID]bool, heldProblem Reason) (Reason, Resource) {
	v := c.Progress.View()
	// A Prepared action prepared under an older snapshot is not ready to
	// dispatch as it stands, but an attempt 0 has no write outstanding and is
	// re-prepared under the current one (#101); only a later attempt's stale
	// preparation waits.
	if (v.Stage != domain.Pending && v.Stage != domain.Prepared) || v.Unresolved || v.Tick > r.CurrentTick ||
		v.Stage == domain.Prepared && v.Attempt > 0 && !v.Snapshot.Matches(r.Current) {
		return NotReady, ""
	}
	if heldIDs[c.Action.ID()] {
		return AlreadyReserved, ""
	}
	if heldProblem != "" {
		return heldProblem, ""
	}
	p := c.Preview
	if !p.Snapshot.Matches(r.Current) || !p.Tick.FreshFor(r.CurrentTick) || !stockFresh {
		return StaleFacts, ""
	}
	if p.Action != c.Action {
		return StaleFacts, ""
	}
	for _, dep := range c.Dependencies {
		complete, known := dep.Completed.Value()
		if !known || !complete || !dep.Snapshot.Matches(r.Current) || !dep.Tick.FreshFor(r.CurrentTick) {
			return DependencyBlocked, ""
		}
	}
	canPlace, canKnown := p.CanPlace.Value()
	safe, safeKnown := p.SafeToPlace.Value()
	made, madeKnown := p.MadeFromStuff.Value()
	if !canKnown || !safeKnown || !madeKnown {
		return UnknownFacts, ""
	}
	if !canPlace || !safe {
		return UnsafePlacement, ""
	}
	b, _ := c.Action.Building()
	if made && b.Stuff() == "" {
		return MaterialRequired, ""
	}
	cells, cellsKnown := p.Footprint.Value()
	costs, costsKnown := p.Costs.Value()
	if c.Action.Kind() == domain.ZoneCreateAction && (made || len(costs) > 0) {
		return UnknownFacts, ""
	}
	if !boundsKnown || !cellsKnown || !costsKnown {
		return UnknownFacts, ""
	}
	if !footprint(cells, c.Action, bounds) {
		return GeometryBlocked, ""
	}
	for _, cell := range cells {
		if occupied[cell] > 0 {
			return GeometryBlocked, ""
		}
	}
	for _, cost := range costs {
		if cost.Count == 0 {
			continue
		}
		rule := rules[cost.Resource]
		if rule.Spending == Stop || rule.Spending == DefenseOnly && c.Purpose != Defense {
			return SpendingBlocked, cost.Resource
		}
		// A shell's frames wait natively for materials; its stock is a
		// spending budget only where the operator reserved some of it.
		if c.Purpose == Shelter && rule.Reserve == 0 {
			continue
		}
		available, known := stock[cost.Resource].Value()
		if !known {
			return UnknownFacts, cost.Resource
		}
		needed, ok := add(used[cost.Resource], cost.Count)
		if !ok {
			return ArithmeticOverflow, cost.Resource
		}
		needed, ok = add(needed, rule.Reserve)
		if !ok {
			return ArithmeticOverflow, cost.Resource
		}
		if needed > available {
			return InsufficientStock, cost.Resource
		}
	}
	return "", ""
}
