package policy

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Ready work describes stages that can consume worker time. Unmet needs and
// unresolved effects alone do not occupy workers.
//
// ProjectReadyWork is a pure projection stored as Rounds.ReadyWork diagnostics;
// admission does not consume it. Explicit adapters identify stages, work,
// claims and parallelism. An action kind without one reports ReadyAwaiting
// with reason eligibility_unknown and no work: its eligibility is not observed.

// ReadyState is what a candidate can do this review.
type ReadyState string

const (
	// ReadyNoMethod: the goal has no plan or proposal to offer.
	ReadyNoMethod ReadyState = "no_method"
	// ReadyBlocked: a known prerequisite or eligibility fact fails.
	ReadyBlocked ReadyState = "blocked"
	// ReadyAwaiting: a fact the stage needs is unknown; never ready.
	ReadyAwaiting ReadyState = "awaiting_observation"
	// ReadyOpenEffect: admitted work whose effect is unresolved but which
	// needs no worker until its next stage's prerequisites hold (a bill
	// waiting on ingredients, a crop still growing).
	ReadyOpenEffect ReadyState = "open_effect"
	// ReadyRunnable: the stage can consume worker time now.
	ReadyRunnable ReadyState = "runnable"
)

// ReadyClaim is a resource or cell a candidate would use. Candidates with
// the same stage, work and claims are one piece of work.
type ReadyClaim struct {
	Kind string // "cell", "thing", "bench"
	Key  string
}

func CellClaim(c domain.Cell) ReadyClaim { return ReadyClaim{"cell", fmt.Sprintf("%d,%d", c.X, c.Z)} }

// ReadyWorkID is order-independent: world, stage, work and claims (or the
// plan action when there are none). A world change changes every ID.
type ReadyWorkID string

type ReadyWork struct {
	ID ReadyWorkID
	// Concerns served; more than one when shared work was deduplicated.
	Concerns []ConcernID
	Method   domain.MethodID `json:",omitempty"`
	Plan     domain.PlanID   `json:",omitempty"`
	Action   domain.ActionID `json:",omitempty"`
	// Stage is the concrete method stage ("bill:Make_Kibble", "grow:Hay"),
	// never the goal's broad profile.
	Stage string
	// Work is the native work type the stage consumes; empty when none is known.
	Work   LaborProfile `json:",omitempty"`
	State  ReadyState
	Reason string `json:",omitempty"`
	// Requires names unmet prerequisites (plan actions or earlier stages).
	Requires []string     `json:",omitempty"`
	Claims   []ReadyClaim `json:",omitempty"`
	// Alternative groups interchangeable methods: at most one per group is
	// counted as demand. Sequential steps use Requires, never this.
	Alternative string `json:",omitempty"`
	// Parallelism is how many workers the stage usefully takes now; zero
	// unless runnable.
	Parallelism int `json:",omitempty"`
}

// ReadyBounds cap one projection pass.
type ReadyBounds struct {
	Candidates int // recorded candidates
	PerConcern int // lookahead per goal
	Discovery  int // inputs examined (plan actions + proposals)
}

func DefaultReadyBounds() ReadyBounds {
	return ReadyBounds{Candidates: 64, PerConcern: 8, Discovery: 1024}
}

// Deferral reasons, deterministic when a bound cuts the pass.
const (
	ReadyDeferredDiscovery  = "discovery_bound"
	ReadyDeferredPerConcern = "per_standard_bound"
	ReadyDeferredCandidate  = "candidate_bound"
)

type ReadyDeferral struct {
	Concern ConcernID
	Reason  string
	Count   int
}

type ReadyWorkReport struct {
	Colony     domain.ColonyID
	Map        domain.MapID
	Load       domain.LoadID
	Tick       domain.Tick
	Candidates []ReadyWork     `json:",omitempty"`
	Deferred   []ReadyDeferral `json:",omitempty"`
	// Continuation is the first input the discovery bound skipped.
	Continuation string `json:",omitempty"`
}

// Current reports whether the report describes the world s names; a
// report from another colony, map or load describes nothing.
func (r ReadyWorkReport) Current(s domain.GenerationSnapshot) bool {
	return r.Colony == s.Colony && r.Map == s.Map && r.Load == s.Load
}

// Demand is the runnable worker demand per work type, counting one
// alternative per group (the largest) so alternatives are never both
// reserved.
func (r ReadyWorkReport) Demand() map[WorkType]int {
	demand := map[WorkType]int{}
	groups := map[string]ReadyWork{}
	for _, c := range r.Candidates {
		if c.State != ReadyRunnable || c.Parallelism == 0 || len(c.Work) == 0 {
			continue
		}
		if c.Alternative == "" {
			demand[c.Work[0]] += c.Parallelism
			continue
		}
		if prev, ok := groups[c.Alternative]; !ok || c.Parallelism > prev.Parallelism {
			groups[c.Alternative] = c
		}
	}
	for _, c := range groups {
		demand[c.Work[0]] += c.Parallelism
	}
	return demand
}

// ReadyPlan is one existing plan with its progress.
type ReadyPlan struct {
	Concern  ConcernID
	Method   domain.MethodID
	Spec     domain.PlanSpec
	Progress []domain.Progress
	// Inputs is the observed next-stage prerequisite of an admitted action
	// whose work comes in stages: a crop ready to sow or harvest. Missing
	// means unknown.
	Inputs map[domain.ActionID]domain.Fact[bool]
}

// ReadyProposal is a not-yet-admitted stage from side-effect-free
// discovery (the adapters below).
type ReadyProposal struct {
	Concern     ConcernID
	Method      domain.MethodID
	Stage       string
	Work        WorkType
	Claims      []ReadyClaim
	Requires    []string
	Alternative string
	// Eligible is the stage's current native eligibility; unknown awaits.
	Eligible    domain.Fact[bool]
	Reason      string
	Parallelism int
}

type ReadyRequest struct {
	Snapshot  domain.GenerationSnapshot
	Tick      domain.Tick
	Plans     []ReadyPlan
	Proposals []ReadyProposal
	// Unserved goals have neither; each reads no_method.
	Unserved []ConcernID
	Bounds   ReadyBounds
	// Construction is the building census: an applied building intent stays
	// open, and its dependents wait, until a built row carries its key.
	Construction domain.Fact[CurrentConstruction]
	// Recipes place a bill's recipe at a work type; a bill with a recipe it
	// does not know reports eligibility_unknown.
	Recipes RecipeFacts
}

// ProjectReadyWork projects plans and proposals into bounded candidates.
// Every open action of a plan is examined, not only the first, so an
// independent runnable action beside a blocked one is visible.
func ProjectReadyWork(r ReadyRequest) ReadyWorkReport {
	b := r.Bounds
	if b == (ReadyBounds{}) {
		b = DefaultReadyBounds()
	}
	report := ReadyWorkReport{Colony: r.Snapshot.Colony, Map: r.Snapshot.Map, Load: r.Snapshot.Load, Tick: r.Tick}
	deferred := map[[2]string]int{}
	perConcern := map[ConcernID]int{}
	byID := map[ReadyWorkID]int{}
	var out []ReadyWork
	examined := 0
	add := func(c ReadyWork) {
		c.Concerns = sortedConcerns(c.Concerns)
		c.Claims = sortedClaims(c.Claims)
		c.ID = readyID(r.Snapshot, c)
		if i, ok := byID[c.ID]; ok {
			out[i].Concerns = sortedConcerns(append(out[i].Concerns, c.Concerns...))
			return
		}
		byID[c.ID] = len(out)
		out = append(out, c)
	}
	budget := func(key string) bool {
		if examined >= b.Discovery {
			if report.Continuation == "" {
				report.Continuation = key
			}
			return false
		}
		examined++
		return true
	}

	plans := append([]ReadyPlan(nil), r.Plans...)
	sort.Slice(plans, func(i, j int) bool { return plans[i].Spec.ID() < plans[j].Spec.ID() })
	for _, p := range plans {
		byAction := map[domain.ActionID]domain.Progress{}
		for _, v := range p.Progress {
			byAction[v.View().Action] = v
		}
		for _, a := range p.Spec.Actions() {
			if !budget(string(p.Spec.ID()) + "/" + string(a.ID())) {
				deferred[[2]string{string(p.Concern), ReadyDeferredDiscovery}]++
				continue
			}
			c, ok := readyAction(p, a, byAction, r.Construction, r.Recipes)
			if !ok {
				continue
			}
			add(c)
		}
	}
	proposals := append([]ReadyProposal(nil), r.Proposals...)
	sort.SliceStable(proposals, func(i, j int) bool {
		a, b := proposals[i], proposals[j]
		if a.Concern != b.Concern {
			return a.Concern < b.Concern
		}
		if a.Method != b.Method {
			return a.Method < b.Method
		}
		if a.Stage != b.Stage {
			return a.Stage < b.Stage
		}
		return fmt.Sprint(sortedClaims(a.Claims)) < fmt.Sprint(sortedClaims(b.Claims))
	})
	for _, p := range proposals {
		if !budget(string(p.Concern) + "/" + string(p.Method) + "/" + p.Stage) {
			deferred[[2]string{string(p.Concern), ReadyDeferredDiscovery}]++
			continue
		}
		add(readyProposal(p))
	}
	for _, g := range r.Unserved {
		add(ReadyWork{Concerns: []ConcernID{g}, Stage: "none", State: ReadyNoMethod, Reason: "no plan or proposal"})
	}

	sort.SliceStable(out, func(i, j int) bool {
		if ri, rj := readyRank(out[i].State), readyRank(out[j].State); ri != rj {
			return ri < rj
		}
		return out[i].ID < out[j].ID
	})
	// Per-goal lookahead after sorting, so the kept candidates do not
	// depend on input order.
	kept := out[:0]
	for _, c := range out {
		g := c.Concerns[0]
		if perConcern[g] >= b.PerConcern {
			deferred[[2]string{string(g), ReadyDeferredPerConcern}]++
			continue
		}
		perConcern[g]++
		kept = append(kept, c)
	}
	out = kept
	if len(out) > b.Candidates {
		for _, c := range out[b.Candidates:] {
			deferred[[2]string{string(c.Concerns[0]), ReadyDeferredCandidate}]++
		}
		out = out[:b.Candidates]
	}
	report.Candidates = out
	for k, n := range deferred {
		report.Deferred = append(report.Deferred, ReadyDeferral{Concern: ConcernID(k[0]), Reason: k[1], Count: n})
	}
	sort.Slice(report.Deferred, func(i, j int) bool {
		a, b := report.Deferred[i], report.Deferred[j]
		if a.Concern != b.Concern {
			return a.Concern < b.Concern
		}
		return a.Reason < b.Reason
	})
	return report
}

func readyRank(s ReadyState) int {
	switch s {
	case ReadyRunnable:
		return 0
	case ReadyAwaiting:
		return 1
	case ReadyBlocked:
		return 2
	case ReadyOpenEffect:
		return 3
	}
	return 4
}

// readyStage describes an action kind's concrete stage.
type readyStage struct {
	stage  string
	work   WorkType // "" = no pawn work (a settings write)
	claims []ReadyClaim
	// staged: once dispatched, the next stage waits on Inputs (a crop)
	// instead of occupying a worker.
	staged bool
}

func readyActionStage(a domain.Action, recipes RecipeFacts) (readyStage, bool) {
	if a.ConstructionTarget() != "" {
		return readyStage{stage: "construction_setting"}, true
	}
	if v, ok := a.Building(); ok {
		return readyStage{stage: "building:" + v.Definition(), work: WorkConstruction, claims: []ReadyClaim{CellClaim(v.Cell())}}, true
	}
	if v, ok := a.Haul(); ok {
		return readyStage{stage: "haul:" + v.Definition(), work: WorkHauling, claims: []ReadyClaim{{"thing", v.Thing()}}}, true
	}
	if v, ok := a.CutPlant(); ok {
		return readyStage{stage: "cut_plant:" + v.Definition(), work: WorkPlantCutting, claims: []ReadyClaim{{"thing", v.Plant()}}}, true
	}
	if v, ok := a.AreaPlantCut(); ok {
		cells := v.Cells()
		claims := make([]ReadyClaim, 0, len(cells))
		for _, cell := range cells {
			claims = append(claims, CellClaim(cell))
		}
		return readyStage{stage: "area_plant_cut", work: WorkPlantCutting, claims: claims}, true
	}
	if v, ok := a.ProductionBill(); ok {
		// A recipe no catalog row places at a bench has no known work: the
		// action reports eligibility_unknown.
		if work, known := recipes.BillWorkOf(v.Recipe()); known {
			return readyStage{stage: "bill:" + v.Recipe(), work: work, claims: []ReadyClaim{{"bench", v.Bench()}}}, true
		}
		return readyStage{}, false
	}
	if _, ok := a.GrowerCrop(); ok {
		return readyStage{stage: "grow", work: WorkGrowing, staged: true}, true
	}
	switch a.Kind() {
	case domain.ZoneCreateAction, domain.SupplyAllowAction, domain.SupplyForbidAction:
		return readyStage{stage: string(a.Kind())}, true
	}
	return readyStage{}, false
}

func readyAction(p ReadyPlan, a domain.Action, byAction map[domain.ActionID]domain.Progress, census domain.Fact[CurrentConstruction], recipes RecipeFacts) (ReadyWork, bool) {
	prog, has := byAction[a.ID()]
	v := prog.View()
	blueprint := has && AppliedBuildingOpen(prog, census)
	if has && !blueprint && (v.Stage == domain.Completed || v.Stage == domain.Unsuccessful || v.Stage == domain.Cancelled) && !v.Unresolved {
		return ReadyWork{}, false
	}
	c := ReadyWork{Concerns: []ConcernID{p.Concern}, Method: p.Method, Plan: p.Spec.ID(), Action: a.ID()}
	st, migrated := readyActionStage(a, recipes)
	if !migrated {
		st = readyStage{stage: string(a.Kind())}
	}
	c.Stage, c.Claims = st.stage, st.claims
	if st.work != "" {
		c.Work = LaborProfile{st.work}
	}
	runnable := func(reason string) ReadyWork {
		c.State = ReadyRunnable
		if reason != "" && c.Reason == "" {
			c.Reason = reason
		}
		if len(c.Work) > 0 {
			c.Parallelism = 1
		}
		return c
	}
	set := func(s ReadyState, reason string) ReadyWork {
		c.State = s
		if c.Reason == "" || s != ReadyRunnable {
			c.Reason = reason
		}
		return c
	}
	if !has || v.Stage == domain.Pending || v.Stage == domain.Prepared {
		for _, d := range p.Spec.Dependencies() {
			if d.Action != a.ID() {
				continue
			}
			req, ok := byAction[d.Requires]
			rv := req.View()
			effect, known := rv.Effect.Value()
			if !ok || rv.Stage != domain.Completed || rv.Unresolved || !known || effect != domain.EffectCompleted || AppliedBuildingOpen(req, census) {
				c.Requires = append(c.Requires, string(d.Requires))
			}
		}
		if len(c.Requires) > 0 {
			sort.Strings(c.Requires)
			return set(ReadyBlocked, string(domain.HeldDependencyBlocked)), true
		}
		if hold, ok := v.FreshHold(); has && ok {
			var reasons []string
			for _, r := range hold.Reasons() {
				reasons = append(reasons, string(r))
			}
			return set(ReadyBlocked, strings.Join(reasons, ",")), true
		}
		if !migrated {
			// No stage adapter: nothing observed says a worker can take it, and the
			// goal's labor profile is not a guess to stand in.
			return set(ReadyAwaiting, "eligibility_unknown"), true
		}
		return runnable("admission"), true
	}
	// An applied building whose blueprint or frame still stands.
	if blueprint {
		return set(ReadyAwaiting, "blueprint_open"), true
	}
	// Dispatched or awaiting: the action is admitted and its effect open.
	effect, known := v.Effect.Value()
	if !known || effect == domain.EffectUnknown {
		return set(ReadyAwaiting, "effect_unknown"), true
	}
	if !migrated {
		return set(ReadyAwaiting, "eligibility_unknown"), true
	}
	if st.work == "" {
		return set(ReadyOpenEffect, "settings_write"), true
	}
	if st.staged {
		ready, known := p.Inputs[a.ID()].Value()
		switch {
		case !known:
			return set(ReadyAwaiting, "stage_inputs_unknown"), true
		case !ready:
			return set(ReadyOpenEffect, "stage_inputs_missing"), true
		}
		return runnable("stage_inputs_ready"), true
	}
	return runnable("designated"), true
}

func readyProposal(p ReadyProposal) ReadyWork {
	c := ReadyWork{Concerns: []ConcernID{p.Concern}, Method: p.Method, Stage: p.Stage, Claims: append([]ReadyClaim(nil), p.Claims...), Alternative: p.Alternative, Reason: p.Reason}
	if p.Work != "" {
		c.Work = LaborProfile{p.Work}
	}
	c.Requires = append([]string(nil), p.Requires...)
	sort.Strings(c.Requires)
	eligible, known := p.Eligible.Value()
	switch {
	case len(c.Requires) > 0:
		c.State, c.Reason = ReadyBlocked, string(domain.HeldDependencyBlocked)
	case !known:
		c.State, c.Reason = ReadyAwaiting, "eligibility_unknown"
	case !eligible:
		c.State = ReadyBlocked
		if c.Reason == "" {
			c.Reason = string(domain.HeldNativeIneligible)
		}
	default:
		c.State = ReadyRunnable
		if len(c.Work) > 0 {
			c.Parallelism = max(p.Parallelism, 1)
		}
	}
	return c
}

func sortedConcerns(goals []ConcernID) []ConcernID {
	seen := map[ConcernID]bool{}
	var out []ConcernID
	for _, g := range goals {
		if !seen[g] {
			seen[g] = true
			out = append(out, g)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}

func sortedClaims(claims []ReadyClaim) []ReadyClaim {
	out := append([]ReadyClaim(nil), claims...)
	sort.Slice(out, func(i, j int) bool {
		if out[i].Kind != out[j].Kind {
			return out[i].Kind < out[j].Kind
		}
		return out[i].Key < out[j].Key
	})
	return out
}

func readyID(s domain.GenerationSnapshot, c ReadyWork) ReadyWorkID {
	h := sha256.New()
	fmt.Fprintf(h, "%v|%v|%v|%s|%v|%v|", s.Colony, s.Map, s.Load, c.Stage, c.Work, c.State == ReadyNoMethod)
	if len(c.Claims) == 0 {
		// Without claims the work is only identified by its origin.
		fmt.Fprintf(h, "%v|%v|%v|%v|", c.Concerns[0], c.Method, c.Plan, c.Action)
	}
	for _, k := range c.Claims {
		fmt.Fprintf(h, "%s=%s;", k.Kind, k.Key)
	}
	return ReadyWorkID(hex.EncodeToString(h.Sum(nil))[:16])
}

// Startup proposal adapters: side-effect-free discovery for the families
// this slice migrates. They read planner outputs already computed from
// cached observations and add no map reads.
