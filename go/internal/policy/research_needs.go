package policy

import "sort"

// ResearchRequirementKind names what kind of native definition a research
// need is blocking on, mirroring research.py's 'ThingDef:'/'RecipeDef:'
// string prefixes.
type ResearchRequirementKind string

const (
	ResearchRequirementThing  ResearchRequirementKind = "ThingDef"
	ResearchRequirementRecipe ResearchRequirementKind = "RecipeDef"
)

// ResearchNeed is one deduplicated (goal, blocked native definition) pair,
// mirroring one entry of research.py's needs() result set.
type ResearchNeed struct {
	Goal        GoalID
	Requirement ResearchRequirementKind
	Name        string
}

// ResearchNeedSource is one active goal's own observed research
// requirements: the native ThingDefs its plan needs that are currently
// unavailable, and the native RecipeDefs its production deficits report as
// research-blocked. research.py's needs() derives this by scanning a single
// plan.colony_goals registry and each goal's evidence/spec steps generically;
// Go has no such registry — each goal family owns its own typed review, so
// callers assemble one ResearchNeedSource per active, non-cancelled,
// non-LLM_ADVISOR-sourced goal from that goal's own admitted evidence before
// calling ResearchNeeds. PriorityClass mirrors research.py's goal priority
// (lower sorts first; unranked goals should use the caller's own default,
// matching research.py's fallback of 4 for a goal not yet in the registry).
type ResearchNeedSource struct {
	Goal              GoalID
	PriorityClass     int
	UnavailableThings []string
	BlockedRecipes    []string
}

// ResearchNeeds aggregates and deterministically orders the distinct
// (goal, requirement) research needs across every supplied source, mirroring
// research.py's needs(): deduplicated by (goal, requirement), then sorted by
// the owning goal's priority class and finally by the goal/requirement
// identity itself so retries observe a stable queue.
func ResearchNeeds(sources []ResearchNeedSource) []ResearchNeed {
	priority := map[GoalID]int{}
	seen := map[ResearchNeed]bool{}
	var result []ResearchNeed
	add := func(goal GoalID, kind ResearchRequirementKind, name string) {
		need := ResearchNeed{Goal: goal, Requirement: kind, Name: name}
		if !seen[need] {
			seen[need] = true
			result = append(result, need)
		}
	}
	for _, source := range sources {
		priority[source.Goal] = source.PriorityClass
		for _, name := range source.UnavailableThings {
			add(source.Goal, ResearchRequirementThing, name)
		}
		for _, name := range source.BlockedRecipes {
			add(source.Goal, ResearchRequirementRecipe, name)
		}
	}
	sort.Slice(result, func(i, j int) bool {
		a, b := result[i], result[j]
		if priority[a.Goal] != priority[b.Goal] {
			return priority[a.Goal] < priority[b.Goal]
		}
		if a.Goal != b.Goal {
			return a.Goal < b.Goal
		}
		if a.Requirement != b.Requirement {
			return a.Requirement < b.Requirement
		}
		return a.Name < b.Name
	})
	return result
}
