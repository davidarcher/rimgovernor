package policy

// ResearchRequirementKind names what kind of native definition a research
// need is blocking on.
type ResearchRequirementKind string

const (
	ResearchRequirementThing  ResearchRequirementKind = "ThingDef"
	ResearchRequirementRecipe ResearchRequirementKind = "RecipeDef"
)

// ResearchNeed is one deduplicated (goal, blocked native definition) pair,
// one entry of the research-needs result set.
type ResearchNeed struct {
	Goal        GoalID
	Requirement ResearchRequirementKind
	Name        string
}

// ResearchNeedSource is one active goal's own observed research
// requirements: the native ThingDefs its plan needs that are currently
// unavailable, and the native RecipeDefs its production deficits report as
// research-blocked. There is no single goal registry to scan generically --
// each goal family owns its own typed review, so
// callers assemble one ResearchNeedSource per active, non-cancelled,
// non-LLM_ADVISOR-sourced goal from that goal's own admitted evidence before
// calling ResearchNeeds. PriorityClass is the goal's priority (lower sorts
// first; unranked goals should use the caller's own default).
type ResearchNeedSource struct {
	Goal              GoalID
	PriorityClass     int
	UnavailableThings []string
	BlockedRecipes    []string
}
