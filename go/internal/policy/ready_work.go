package policy

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Ready work (#645) is the typed input the worker allocator reads: concrete
// work that can run next, kept apart from the two facts it is easily
// confused with. An unmet need is a goal (DevelopmentGoal); an admitted
// action whose effect is unresolved is progress (a standing bill, a sown
// field) and occupies no worker by itself; ready work is the stage that
// would consume a worker's time now.
//
// Ownership: this file is the one contract. ProjectReadyWork is pure and
// value-only: it makes no native writes and reserves nothing, so discovery
// never needs the optional slot a candidate competes for. The store records
// the projection beside the development rows as shadow diagnostics
// (RoutineReview.ReadyWork); admission does not read it yet. Extension: a
// family migrates by giving readyActionStage a case for its action kind (or
// a proposal adapter below) that names the actual stage, work type, claims
// and parallelism; every other kind stays on the conservative adapter.

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

// ReadyAdapter names how a candidate's work was derived.
type ReadyAdapter string

const (
	ReadyMigrated ReadyAdapter = "migrated"
	// ReadyConservative: the action kind is not migrated (ReadyConservativeKinds
	// is every kind but ReadyMigratedKinds). Its work is the goal's whole
	// labor profile, one worker, and a dispatched action is assumed to be
	// occupying it: usage is never read as zero.
	ReadyConservative ReadyAdapter = "conservative"
)

// ReadyMigratedKinds are the action kinds whose stage, work and claims are
// described exactly in this slice (startup shelter/building, supply hauling,
// animal feed, wood).
var ReadyMigratedKinds = []domain.ActionKind{domain.BuildingAction, domain.HaulAction, domain.CutPlantAction, domain.ProductionBillAction, domain.GrowerCropAction, domain.ZoneCreateAction, domain.SupplyAllowAction, domain.SupplyForbidAction}

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
	// Goals served; more than one when shared work was deduplicated.
	Goals  []GoalID
	Method domain.MethodID `json:",omitempty"`
	Plan   domain.PlanID   `json:",omitempty"`
	Action domain.ActionID `json:",omitempty"`
	// Stage is the concrete method stage ("bill:Make_Kibble", "grow:Hay"),
	// never the goal's broad profile.
	Stage string
	// Work is the native work type the stage consumes; a conservative
	// candidate lists the goal's whole profile (any one of them).
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
	Adapter     ReadyAdapter
}

// ReadyBounds cap one projection pass.
type ReadyBounds struct {
	Candidates int // recorded candidates
	PerGoal    int // lookahead per goal
	Discovery  int // inputs examined (plan actions + proposals)
}

func DefaultReadyBounds() ReadyBounds {
	return ReadyBounds{Candidates: 64, PerGoal: 8, Discovery: 1024}
}

// Deferral reasons, deterministic when a bound cuts the pass.
const (
	ReadyDeferredDiscovery = "discovery_bound"
	ReadyDeferredPerGoal   = "per_goal_bound"
	ReadyDeferredCandidate = "candidate_bound"
)

type ReadyDeferral struct {
	Goal   GoalID
	Reason string
	Count  int
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
	// Conservative lists the unmigrated action kinds this pass met.
	Conservative []domain.ActionKind `json:",omitempty"`
}

// Current reports whether the report describes the world s names; a
// report from another colony, map or load describes nothing.
func (r ReadyWorkReport) Current(s domain.GenerationSnapshot) bool {
	return r.Colony == s.Colony && r.Map == s.Map && r.Load == s.Load
}

// Demand is the runnable worker demand per work type, counting one
// alternative per group (the largest) so alternatives are never both
// reserved. Conservative candidates count against their first work type.
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
	Goal     GoalID
	Method   domain.MethodID
	Spec     domain.PlanSpec
	Progress []domain.Progress
	// Inputs is the observed next-stage prerequisite of an admitted action
	// whose work comes in stages: a bill's ingredients, a crop ready to
	// sow or harvest. Missing means unknown.
	Inputs map[domain.ActionID]domain.Fact[bool]
}

// ReadyProposal is a not-yet-admitted stage from side-effect-free
// discovery (the adapters below).
type ReadyProposal struct {
	Goal        GoalID
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
	Unserved []GoalID
	Bounds   ReadyBounds
}

const maxReadyParallelism = 4

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
	perGoal := map[GoalID]int{}
	byID := map[ReadyWorkID]int{}
	var out []ReadyWork
	examined := 0
	conservative := map[domain.ActionKind]bool{}
	add := func(c ReadyWork) {
		c.Goals = sortedGoals(c.Goals)
		c.Claims = sortedClaims(c.Claims)
		c.ID = readyID(r.Snapshot, c)
		if i, ok := byID[c.ID]; ok {
			out[i].Goals = sortedGoals(append(out[i].Goals, c.Goals...))
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
				deferred[[2]string{string(p.Goal), ReadyDeferredDiscovery}]++
				continue
			}
			c, ok := readyAction(p, a, byAction)
			if !ok {
				continue
			}
			if c.Adapter == ReadyConservative {
				conservative[a.Kind()] = true
			}
			add(c)
		}
	}
	proposals := append([]ReadyProposal(nil), r.Proposals...)
	sort.SliceStable(proposals, func(i, j int) bool {
		a, b := proposals[i], proposals[j]
		if a.Goal != b.Goal {
			return a.Goal < b.Goal
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
		if !budget(string(p.Goal) + "/" + string(p.Method) + "/" + p.Stage) {
			deferred[[2]string{string(p.Goal), ReadyDeferredDiscovery}]++
			continue
		}
		add(readyProposal(p))
	}
	for _, g := range r.Unserved {
		add(ReadyWork{Goals: []GoalID{g}, Stage: "none", State: ReadyNoMethod, Reason: "no plan or proposal", Adapter: ReadyMigrated})
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
		g := c.Goals[0]
		if perGoal[g] >= b.PerGoal {
			deferred[[2]string{string(g), ReadyDeferredPerGoal}]++
			continue
		}
		perGoal[g]++
		kept = append(kept, c)
	}
	out = kept
	if len(out) > b.Candidates {
		for _, c := range out[b.Candidates:] {
			deferred[[2]string{string(c.Goals[0]), ReadyDeferredCandidate}]++
		}
		out = out[:b.Candidates]
	}
	report.Candidates = out
	for k, n := range deferred {
		report.Deferred = append(report.Deferred, ReadyDeferral{Goal: GoalID(k[0]), Reason: k[1], Count: n})
	}
	sort.Slice(report.Deferred, func(i, j int) bool {
		a, b := report.Deferred[i], report.Deferred[j]
		if a.Goal != b.Goal {
			return a.Goal < b.Goal
		}
		return a.Reason < b.Reason
	})
	for k := range conservative {
		report.Conservative = append(report.Conservative, k)
	}
	sort.Slice(report.Conservative, func(i, j int) bool { return report.Conservative[i] < report.Conservative[j] })
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

// readyStage is a migrated kind's concrete stage.
type readyStage struct {
	stage  string
	work   WorkType // "" = no pawn work (a settings write)
	claims []ReadyClaim
	// staged: once dispatched, the next stage waits on Inputs (a bill,
	// a crop) instead of occupying a worker.
	staged bool
}

func readyActionStage(a domain.Action) (readyStage, bool) {
	if v, ok := a.Building(); ok {
		return readyStage{stage: "building:" + v.Definition(), work: WorkConstruction, claims: []ReadyClaim{CellClaim(v.Cell())}}, true
	}
	if v, ok := a.Haul(); ok {
		return readyStage{stage: "haul:" + v.Definition(), work: WorkHauling, claims: []ReadyClaim{{"thing", v.Thing()}}}, true
	}
	if v, ok := a.CutPlant(); ok {
		return readyStage{stage: "cut_plant:" + v.Definition(), work: WorkPlantCutting, claims: []ReadyClaim{{"thing", v.Plant()}}}, true
	}
	if v, ok := a.ProductionBill(); ok {
		return readyStage{stage: "bill:" + v.Recipe(), work: billWork(v.Recipe()), claims: []ReadyClaim{{"bench", v.Bench()}}, staged: true}, true
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

// billWork names the work type a recipe's bill puts a pawn to. Only the
// startup feed/meal recipes are migrated; any other recipe is Crafting,
// the bench family the rest of the controller assumes.
func billWork(recipe string) WorkType {
	switch {
	case strings.HasPrefix(recipe, "Cook") || recipe == "Make_Kibble" || recipe == "Make_Pemmican" || strings.HasPrefix(recipe, "Butcher"):
		return WorkCooking
	}
	return WorkCrafting
}

func readyAction(p ReadyPlan, a domain.Action, byAction map[domain.ActionID]domain.Progress) (ReadyWork, bool) {
	prog, has := byAction[a.ID()]
	v := prog.View()
	if has && (v.Stage == domain.Completed || v.Stage == domain.Unsuccessful || v.Stage == domain.Cancelled) && !v.Unresolved {
		return ReadyWork{}, false
	}
	c := ReadyWork{Goals: []GoalID{p.Goal}, Method: p.Method, Plan: p.Spec.ID(), Action: a.ID(), Adapter: ReadyMigrated}
	st, migrated := readyActionStage(a)
	if !migrated {
		st = readyStage{stage: string(a.Kind())}
		c.Adapter = ReadyConservative
		c.Reason = "unmigrated_family"
	}
	c.Stage, c.Claims = st.stage, st.claims
	if st.work != "" {
		c.Work = LaborProfile{st.work}
	} else if !migrated {
		c.Work = append(LaborProfile(nil), GoalLabor(p.Goal)...)
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
			if !ok || rv.Stage != domain.Completed || rv.Unresolved || !known || effect != domain.EffectCompleted {
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
		return runnable("admission"), true
	}
	// Dispatched or awaiting: the action is admitted and its effect open.
	effect, known := v.Effect.Value()
	if !known || effect == domain.EffectUnknown {
		return set(ReadyAwaiting, "effect_unknown"), true
	}
	if !migrated {
		// Conservative: an open unmigrated action is assumed to occupy a worker.
		return runnable(""), true
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
	c := ReadyWork{Goals: []GoalID{p.Goal}, Method: p.Method, Stage: p.Stage, Claims: append([]ReadyClaim(nil), p.Claims...), Alternative: p.Alternative, Adapter: ReadyMigrated, Reason: p.Reason}
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
			c.Parallelism = min(max(p.Parallelism, 1), maxReadyParallelism)
		}
	}
	return c
}

func sortedGoals(goals []GoalID) []GoalID {
	seen := map[GoalID]bool{}
	var out []GoalID
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
		fmt.Fprintf(h, "%v|%v|%v|%v|", c.Goals[0], c.Method, c.Plan, c.Action)
	}
	for _, k := range c.Claims {
		fmt.Fprintf(h, "%s=%s;", k.Kind, k.Key)
	}
	return ReadyWorkID(hex.EncodeToString(h.Sum(nil))[:16])
}

// Startup proposal adapters: side-effect-free discovery for the families
// this slice migrates. They read planner outputs already computed from
// cached observations and add no map reads.

// ShelterBunkProposals: each bed of a bunk layout is independent
// construction; beds build in parallel.
func ShelterBunkProposals(goal GoalID, method domain.MethodID, bed string, bunks ShelterBunks, eligible domain.Fact[bool]) []ReadyProposal {
	var out []ReadyProposal
	for _, c := range bunks.Beds {
		out = append(out, ReadyProposal{Goal: goal, Method: method, Stage: "building:" + bed, Work: WorkConstruction, Claims: []ReadyClaim{CellClaim(c)}, Eligible: eligible, Parallelism: 1})
	}
	return out
}

// WoodProposals: each designatable tree is one cut.
func WoodProposals(goal GoalID, method domain.MethodID, definition string, trees []string, eligible domain.Fact[bool]) []ReadyProposal {
	var out []ReadyProposal
	for _, t := range trees {
		out = append(out, ReadyProposal{Goal: goal, Method: method, Stage: "cut_plant:" + definition, Work: WorkPlantCutting, Claims: []ReadyClaim{{"thing", t}}, Eligible: eligible, Parallelism: 1})
	}
	return out
}

// SupplyHaulProposals: each loose stack is one haul; two goals naming the
// same stack project one candidate.
func SupplyHaulProposals(goal GoalID, method domain.MethodID, definition string, things []string, eligible domain.Fact[bool]) []ReadyProposal {
	var out []ReadyProposal
	for _, t := range things {
		out = append(out, ReadyProposal{Goal: goal, Method: method, Stage: "haul:" + definition, Work: WorkHauling, Claims: []ReadyClaim{{"thing", t}}, Eligible: eligible, Parallelism: 1})
	}
	return out
}

// AnimalFeedProposals: MaintainAnimalFeed's methods as alternatives of one
// group. A stock-sourced method is a haul; kibble is a bill on any of the
// shared benches (each bench an alternative) that can run only when its
// ingredients are observed; a hay field is growing on its cells.
func AnimalFeedProposals(goal GoalID, m AnimalFeedMethod, ingredients domain.Fact[bool], hay []domain.Cell) []ReadyProposal {
	group := string(goal) + "/feed"
	var out []ReadyProposal
	if m.Resource == AnimalFeedFallbackResource {
		for _, b := range m.Benches {
			out = append(out, ReadyProposal{Goal: goal, Method: "kibble", Stage: "bill:Make_Kibble", Work: WorkCooking, Claims: []ReadyClaim{{"bench", b}}, Alternative: group, Eligible: ingredients, Parallelism: 1})
		}
	} else if m.Resource != "" {
		out = append(out, ReadyProposal{Goal: goal, Method: domain.MethodID("stock-" + string(m.Resource)), Stage: "haul:" + string(m.Resource), Work: WorkHauling, Alternative: group, Eligible: domain.Known(m.Delivered), Reason: reasonIf(!m.Delivered, string(domain.HeldStorageMissing)), Parallelism: 1})
	}
	if len(hay) > 0 {
		var claims []ReadyClaim
		for _, c := range hay {
			claims = append(claims, CellClaim(c))
		}
		out = append(out, ReadyProposal{Goal: goal, Method: "hay", Stage: "grow:Hay", Work: WorkGrowing, Claims: claims, Alternative: group, Eligible: domain.Known(true), Parallelism: 1})
	}
	return out
}

func reasonIf(cond bool, reason string) string {
	if cond {
		return reason
	}
	return ""
}
