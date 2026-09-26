package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Dependency donation (#651): a goal whose admitted work is waiting on a
// measured shortfall of a resource lends its scheduling order to the goal
// that acquires that resource, for as long as the shortfall stays open.
// Donation is effective ordering only: the prerequisite keeps its declared
// priority, so it never becomes startup or emergency work and never holds
// the clock. It changes which ranked row takes the next free slot and
// worker; the project limit, the labor fit, the stage hold and the goal's
// own planner (safety, spending rules, designations) still apply.
//
// Edges come from typed admission evidence (a shelter method's stock
// observation against its previewed costs), never from reason strings.
// ResolveDonations is pure: the caller supplies only edges of the current
// world whose dependent goal epoch is still active, with the costs of the
// dependent actions still open and the current usable stock.

// ResourcePrerequisite is the goal that acquires resource: MaintainWood
// for wood (until it folds into MaintainResource, #728), MaintainResource
// for every other definition.
func ResourcePrerequisite(resource Resource) (GoalID, bool) {
	if resource == "WoodLog" {
		return MaintainWood, true
	}
	if validResource(resource) {
		return MaintainResource, true
	}
	return "", false
}

// DependencyCost is one open dependent action's cost in the resource.
type DependencyCost struct {
	Action domain.ActionID
	Count  int64
}

// DevelopmentDependency is one typed edge: Dependent's method needs
// Resource (or, with no resource, Prerequisite's work outright) and
// Prerequisite supplies it.
type DevelopmentDependency struct {
	Dependent    GoalID
	Goal         domain.GoalID // the dependent's goal identity
	Epoch        uint64
	Method       domain.MethodID
	Prerequisite GoalID
	Resource     Resource `json:",omitempty"`
	// Costs are the dependent's still-open actions' costs in Resource.
	Costs []DependencyCost `json:",omitempty"`
	// Available is the usable stock now (after other claims); unknown
	// neither satisfies nor creates the edge.
	Available domain.Fact[int64]
	// Observed is the tick the edge's evidence was taken.
	Observed domain.Tick
}

// DevelopmentDonation is what a prerequisite row inherited.
type DevelopmentDonation struct {
	// Priority is the effective ordering: the most urgent dependent's
	// declared priority. The goal's own priority is unchanged.
	Priority int
	// Chain is the dependency chain from the originating goal to this one.
	Chain    []GoalID
	Resource Resource `json:",omitempty"`
	// Shortfall is the bounded demand: open dependent costs, each action
	// once, minus usable stock.
	Shortfall int64 `json:",omitempty"`
	// Conflict names an explicit operator ceiling that kept the donated
	// row from a slot ("project_limit"); the ceiling holds.
	Conflict string `json:",omitempty"`
}

// DependencyBlocker is an edge that donates nothing, and why.
type DependencyBlocker struct {
	Dependent    GoalID
	Prerequisite GoalID
	Reason       string
}

const (
	DependencyCycle            = "dependency_cycle"
	DependencyDepth            = "chain_depth"
	DependencyInactive         = "prerequisite_inactive"
	DependencyStockUnknown     = "stock_unknown"
	DependencyNotUrgent        = "not_more_urgent"
	MaxDependencyChain         = 4
	MaxDevelopmentDependencies = 256
)

// ResolveDonations computes each prerequisite's inherited ordering from the
// live edges. A resource edge donates only while its deduplicated shortfall
// is positive; an edge to a goal that is not an active ranked row with a
// method (absent, cancelled, blocked, method unavailable) donates nothing
// and says so, so no worker waits on a dependency nobody can execute.
// Chains pass the most urgent origin along, MaxDependencyChain goals
// long counting the origin; a
// cycle donates nothing along it.
func ResolveDonations(goals []DevelopmentGoal, deps []DevelopmentDependency) (map[GoalID]DevelopmentDonation, []DependencyBlocker) {
	byID := map[GoalID]DevelopmentGoal{}
	for _, g := range goals {
		byID[g.ID] = g
	}
	var blockers []DependencyBlocker
	block := func(d DevelopmentDependency, reason string) {
		blockers = append(blockers, DependencyBlocker{Dependent: d.Dependent, Prerequisite: d.Prerequisite, Reason: reason})
	}
	// Shared demand: per (prerequisite, resource), each action's cost once
	// and the freshest stock reading.
	type demand struct {
		costs     map[domain.ActionID]int64
		available int64
		observed  domain.Tick
		known     bool
	}
	demands := map[[2]string]*demand{}
	// live edges: dependent -> prerequisite
	edges := map[GoalID]map[GoalID]bool{}
	for i, d := range deps {
		if i >= MaxDevelopmentDependencies {
			break
		}
		if d.Dependent == d.Prerequisite {
			block(d, DependencyCycle)
			continue
		}
		if d.Resource != "" {
			available, known := d.Available.Value()
			if !known {
				block(d, DependencyStockUnknown)
				continue
			}
			key := [2]string{string(d.Prerequisite), string(d.Resource)}
			m := demands[key]
			if m == nil {
				m = &demand{costs: map[domain.ActionID]int64{}}
				demands[key] = m
			}
			for _, c := range d.Costs {
				if c.Count > 0 {
					m.costs[c.Action] = max(m.costs[c.Action], c.Count)
				}
			}
			if !m.known || d.Observed >= m.observed {
				m.available, m.observed, m.known = max(0, available), d.Observed, true
			}
		}
		if edges[d.Dependent] == nil {
			edges[d.Dependent] = map[GoalID]bool{}
		}
		edges[d.Dependent][d.Prerequisite] = true
	}
	shortfall := map[[2]string]int64{}
	for key, m := range demands {
		var need int64
		for _, c := range m.costs {
			need += c
		}
		shortfall[key] = max(0, need-m.available)
	}
	// An edge is open when its resource shortfall is positive (or it names
	// no resource).
	open := func(d DevelopmentDependency) bool {
		return d.Resource == "" || shortfall[[2]string{string(d.Prerequisite), string(d.Resource)}] > 0
	}
	// reaches: whether from can reach to over live edges (bounded search).
	reaches := func(from, to GoalID) bool {
		seen := map[GoalID]bool{from: true}
		queue := []GoalID{from}
		for len(queue) > 0 && len(seen) <= MaxDevelopmentDependencies {
			at := queue[0]
			queue = queue[1:]
			for next := range edges[at] {
				if next == to {
					return true
				}
				if !seen[next] {
					seen[next] = true
					queue = append(queue, next)
				}
			}
		}
		return false
	}
	executable := func(g GoalID) bool {
		v, ok := byID[g]
		return ok && !v.Cancelled && !v.Blocked && !v.MethodUnavailable
	}

	donations := map[GoalID]DevelopmentDonation{}
	blocked := map[[2]GoalID]bool{}
	// Walk from every dependent that is not itself a prerequisite of a
	// live edge's chain origin: each origin donates its own priority down
	// its chain.
	var walk func(origin DevelopmentGoal, at GoalID, chain []GoalID, seen map[GoalID]bool)
	walk = func(origin DevelopmentGoal, at GoalID, chain []GoalID, seen map[GoalID]bool) {
		for _, d := range deps {
			if d.Dependent != at || d.Dependent == d.Prerequisite || !open(d) {
				continue
			}
			if _, known := d.Available.Value(); d.Resource != "" && !known {
				continue
			}
			pair := [2]GoalID{d.Dependent, d.Prerequisite}
			switch {
			case seen[d.Prerequisite] || reaches(d.Prerequisite, d.Dependent):
				if !blocked[pair] {
					blocked[pair] = true
					block(d, DependencyCycle)
				}
				continue
			case len(chain) >= MaxDependencyChain:
				if !blocked[pair] {
					blocked[pair] = true
					block(d, DependencyDepth)
				}
				continue
			case !executable(d.Prerequisite):
				if !blocked[pair] {
					blocked[pair] = true
					block(d, DependencyInactive)
				}
				continue
			}
			pre := byID[d.Prerequisite]
			next := append(append([]GoalID(nil), chain...), d.Prerequisite)
			if origin.Priority >= pre.Priority {
				if !blocked[pair] && len(chain) == 1 {
					blocked[pair] = true
					block(d, DependencyNotUrgent)
				}
			} else if cur, ok := donations[pre.ID]; !ok || origin.Priority < cur.Priority {
				don := DevelopmentDonation{Priority: origin.Priority, Chain: next, Resource: d.Resource}
				if d.Resource != "" {
					don.Shortfall = shortfall[[2]string{string(d.Prerequisite), string(d.Resource)}]
				}
				donations[pre.ID] = don
			}
			seen2 := map[GoalID]bool{}
			for k := range seen {
				seen2[k] = true
			}
			seen2[d.Prerequisite] = true
			walk(origin, d.Prerequisite, next, seen2)
		}
	}
	origins := make([]GoalID, 0, len(edges))
	for g := range edges {
		origins = append(origins, g)
	}
	sort.Slice(origins, func(i, j int) bool { return origins[i] < origins[j] })
	for _, g := range origins {
		origin, ok := byID[g]
		if !ok || origin.Cancelled {
			continue
		}
		walk(origin, g, []GoalID{g}, map[GoalID]bool{g: true})
	}
	sort.Slice(blockers, func(i, j int) bool {
		a, b := blockers[i], blockers[j]
		if a.Dependent != b.Dependent {
			return a.Dependent < b.Dependent
		}
		if a.Prerequisite != b.Prerequisite {
			return a.Prerequisite < b.Prerequisite
		}
		return a.Reason < b.Reason
	})
	return donations, blockers
}

// WoodShortfall is the open WoodLog demand of the live edges naming
// MaintainWood (#711).
func WoodShortfall(deps []DevelopmentDependency) int64 {
	d := dependencyDemands(deps, MaintainWood)["WoodLog"]
	return max(0, d.need-d.available)
}

// DependencyResourceNeeds are the stock floors MaintainResource's live
// shortfall edges ask for (#728): per resource, the open dependent costs,
// each action once, wherever the freshest known stock falls short of them.
// ResourceGoalTargets merges them into the configured targets, so a goal
// admitted short of any resource raises its acquisition.
func DependencyResourceNeeds(deps []DevelopmentDependency) map[Resource]int64 {
	var out map[Resource]int64
	for resource, d := range dependencyDemands(deps, MaintainResource) {
		if d.need > d.available {
			if out == nil {
				out = map[Resource]int64{}
			}
			out[resource] = d.need
		}
	}
	return out
}

type dependencyDemand struct{ need, available int64 }

// dependencyDemands is ResolveDonations' shared demand for one
// prerequisite: per resource, each open action's cost once against the
// freshest known stock. Edges with unknown stock add nothing.
func dependencyDemands(deps []DevelopmentDependency, prerequisite GoalID) map[Resource]dependencyDemand {
	type acc struct {
		costs     map[domain.ActionID]int64
		available int64
		observed  domain.Tick
		known     bool
	}
	accs := map[Resource]*acc{}
	for _, d := range deps {
		stock, k := d.Available.Value()
		if d.Prerequisite != prerequisite || d.Resource == "" || !k {
			continue
		}
		a := accs[d.Resource]
		if a == nil {
			a = &acc{costs: map[domain.ActionID]int64{}}
			accs[d.Resource] = a
		}
		for _, c := range d.Costs {
			if c.Count > 0 {
				a.costs[c.Action] = max(a.costs[c.Action], c.Count)
			}
		}
		if !a.known || d.Observed >= a.observed {
			a.available, a.observed, a.known = max(0, stock), d.Observed, true
		}
	}
	out := make(map[Resource]dependencyDemand, len(accs))
	for r, a := range accs {
		var need int64
		for _, c := range a.costs {
			need += c
		}
		out[r] = dependencyDemand{need, a.available}
	}
	return out
}
