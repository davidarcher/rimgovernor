package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// WorkshopMethod is what a MaintainResource deficit needs from the building
// ladder before a bill can be dispatched for it.
type WorkshopMethod string

const (
	// WorkshopUnknown: a native fact the choice depends on is unobserved.
	WorkshopUnknown WorkshopMethod = ""
	// WorkshopExisting: a usable bench already hosts an available recipe for
	// the resource; the bill path owns the deficit from here.
	WorkshopExisting WorkshopMethod = "existing"
	// WorkshopBuild: stage Definition, a bench buildable now, in a
	// Workshop-hosting room. NeedsPower marks a powered bench, which the
	// power family connects once it stands as an unpowered consumer.
	WorkshopBuild WorkshopMethod = "build"
	// WorkshopResearch: the first candidate bench and recipe are gated only
	// by unfinished research; Research lists the projects to finish.
	WorkshopResearch WorkshopMethod = "research"
	// WorkshopUnavailable: every hosting bench needs more construction skill
	// than any builder has, or power no generator definition can supply,
	// which this ladder does not stage.
	WorkshopUnavailable WorkshopMethod = "unavailable"
)

// BenchDefinition is the planning-census view of one bench definition.
type BenchDefinition struct {
	Name              string
	Available         domain.Fact[bool]
	NeedsPower        domain.Fact[bool]
	ConstructionSkill domain.Fact[int32]
	// Research names the definition's own ResearchProjectDefs; with
	// Available false they are what stands between the colony and the bench.
	Research []string
}

type WorkshopRequest struct {
	Resource    Resource
	Benches     domain.Fact[[]GearBench]
	Hosts       []RecipeHost
	Definitions []BenchDefinition
	// Power is whether a generator definition is available to build, so a
	// powered bench can be staged and connected; unknown or false keeps the
	// choice to unpowered benches.
	Power domain.Fact[bool]
	// BuilderSkill is the best Construction level among colonists who can
	// build (BuilderSkill); a bench with a construction skill prerequisite is
	// buildable only when a builder meets it, and the plan's own work
	// requirement then assigns that builder. Unknown keeps the choice to
	// benches without a prerequisite.
	BuilderSkill domain.Fact[int32]
}

// BuilderSkill is the highest Construction skill level among the pawns that
// are available, apply to work assignment and have the skill enabled;
// unknown when the roster or any such pawn's skills are unobserved.
func BuilderSkill(pawns domain.Fact[[]WorkPawn]) domain.Fact[int32] {
	rows, known := pawns.Value()
	if !known {
		return domain.Unknown[int32]()
	}
	best := int32(0)
	for _, pawn := range rows {
		available, ak := pawn.Available.Value()
		applies, pk := pawn.Applies.Value()
		if !ak || !pk {
			return domain.Unknown[int32]()
		}
		if !available || !applies {
			continue
		}
		skills, sk := pawn.Skills.Value()
		if !sk {
			return domain.Unknown[int32]()
		}
		for _, skill := range skills {
			if skill.Name == "Construction" && !skill.Disabled {
				best = max(best, int32(skill.Level))
			}
		}
	}
	return domain.Known(best)
}

type WorkshopChoice struct {
	Method     WorkshopMethod
	Definition string
	Recipe     string
	NeedsPower bool
	// Research is the sorted project set WorkshopResearch waits on.
	Research []string
}

// WorkshopBenchCandidates lists, in stable order, every bench definition
// hosting a research-available recipe that produces the resource. It is the
// definition set a planner requests from the planning census before choosing.
func WorkshopBenchCandidates(resource Resource, hosts []RecipeHost) []string {
	return workshopCandidates(resource, hosts, false)
}

// WorkshopResearchCandidates lists every bench definition hosting any recipe
// for the resource, research-gated ones included, so the census can report
// which projects each is waiting on.
func WorkshopResearchCandidates(resource Resource, hosts []RecipeHost) []string {
	return workshopCandidates(resource, hosts, true)
}

func workshopCandidates(resource Resource, hosts []RecipeHost, gated bool) []string {
	seen := map[string]bool{}
	var out []string
	for _, host := range hosts {
		if !gated && !host.Available || !containsResource(host.Products, resource) {
			continue
		}
		for _, bench := range host.Benches {
			if !seen[bench] {
				seen[bench] = true
				out = append(out, bench)
			}
		}
	}
	sort.Strings(out)
	return out
}

// SelectWorkshopBench decides whether a resource deficit already has a bench
// (reuse first), needs one staged (the first candidate the census reports
// buildable by a builder the colony has, unpowered before powered), is
// waiting on research (the first candidate gated by nothing else), or
// cannot be served yet. It issues no orders and reserves nothing.
func SelectWorkshopBench(r WorkshopRequest) (WorkshopChoice, error) {
	if !validResource(r.Resource) {
		return WorkshopChoice{}, errors.New("invalid workshop resource")
	}
	if len(r.Hosts) > 256 || len(r.Definitions) > 256 {
		return WorkshopChoice{}, errors.New("workshop request exceeds bound")
	}
	benches, known := r.Benches.Value()
	if !known {
		return WorkshopChoice{Method: WorkshopUnknown}, nil
	}
	for _, b := range benches {
		recipes, known := b.Recipes.Value()
		if !known {
			return WorkshopChoice{Method: WorkshopUnknown}, nil
		}
		for _, recipe := range recipes {
			if !containsResource(recipe.Products, r.Resource) {
				continue
			}
			available, ak := recipe.Available.Value()
			on, ok := recipe.AvailableOn.Value()
			if !ak || !ok {
				return WorkshopChoice{Method: WorkshopUnknown}, nil
			}
			if available && on {
				return WorkshopChoice{Method: WorkshopExisting, Definition: "", Recipe: recipe.Definition}, nil
			}
		}
	}
	byName := map[string]BenchDefinition{}
	for _, d := range r.Definitions {
		byName[d.Name] = d
	}
	power, powerKnown := r.Power.Value()
	builder, builderKnown := r.BuilderSkill.Value()
	buildable := func(skill int32) bool { return skill == 0 || builderKnown && skill <= builder }
	unknown := false
	// Buildable now: unpowered first, then powered when a generator can be
	// built for it.
	for _, allowPower := range []bool{false, true} {
		if allowPower && !(powerKnown && power) {
			continue
		}
		for _, name := range WorkshopBenchCandidates(r.Resource, r.Hosts) {
			d, exists := byName[name]
			available, ak := d.Available.Value()
			powered, pk := d.NeedsPower.Value()
			skill, sk := d.ConstructionSkill.Value()
			if !exists || !ak || !pk || !sk {
				unknown = true
				continue
			}
			if available && powered == allowPower && buildable(skill) {
				return WorkshopChoice{Method: WorkshopBuild, Definition: name, Recipe: workshopRecipe(r.Resource, r.Hosts, name), NeedsPower: powered}, nil
			}
		}
	}
	// Research-gated: the first bench and recipe pair that only research
	// stands between; its projects are the ladder's next rung.
	for _, host := range r.Hosts {
		if !containsResource(host.Products, r.Resource) {
			continue
		}
		for _, name := range host.Benches {
			d, exists := byName[name]
			available, ak := d.Available.Value()
			powered, pk := d.NeedsPower.Value()
			skill, sk := d.ConstructionSkill.Value()
			if !exists || !ak || !pk || !sk {
				unknown = true
				continue
			}
			if !buildable(skill) || powered && !(powerKnown && power) {
				continue
			}
			// A gate the census cannot name (a recipe or bench unavailable
			// with no project listed) is not research this ladder can do.
			var projects []string
			if !host.Available {
				if len(host.Research) == 0 {
					continue
				}
				projects = append(projects, host.Research...)
			}
			if !available {
				if len(d.Research) == 0 {
					continue
				}
				projects = append(projects, d.Research...)
			}
			if len(projects) == 0 {
				continue
			}
			sort.Strings(projects)
			projects = dedupeStrings(projects)
			return WorkshopChoice{Method: WorkshopResearch, Definition: name, Recipe: host.Definition, NeedsPower: powered, Research: projects}, nil
		}
	}
	if unknown {
		return WorkshopChoice{Method: WorkshopUnknown}, nil
	}
	return WorkshopChoice{Method: WorkshopUnavailable}, nil
}

func dedupeStrings(sorted []string) []string {
	out := sorted[:0]
	for i, s := range sorted {
		if i == 0 || s != sorted[i-1] {
			out = append(out, s)
		}
	}
	return out
}

func workshopRecipe(resource Resource, hosts []RecipeHost, bench string) string {
	for _, host := range hosts {
		if !host.Available || !containsResource(host.Products, resource) {
			continue
		}
		for _, b := range host.Benches {
			if b == bench {
				return host.Definition
			}
		}
	}
	return ""
}
