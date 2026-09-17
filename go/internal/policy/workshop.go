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
	// WorkshopBuild: stage Definition, a non-powered bench buildable now, in a
	// Workshop-hosting room.
	WorkshopBuild WorkshopMethod = "build"
	// WorkshopUnavailable: every hosting bench needs research, power or a
	// skilled builder this slice does not stage.
	WorkshopUnavailable WorkshopMethod = "unavailable"
)

// BenchDefinition is the planning-census view of one bench definition.
type BenchDefinition struct {
	Name              string
	Available         domain.Fact[bool]
	NeedsPower        domain.Fact[bool]
	ConstructionSkill domain.Fact[int32]
}

type WorkshopRequest struct {
	Resource    Resource
	Benches     domain.Fact[[]GearBench]
	Hosts       []RecipeHost
	Definitions []BenchDefinition
}

type WorkshopChoice struct {
	Method     WorkshopMethod
	Definition string
	Recipe     string
}

// WorkshopBenchCandidates lists, in stable order, every bench definition
// hosting a research-available recipe that produces the resource. It is the
// definition set a planner requests from the planning census before choosing.
func WorkshopBenchCandidates(resource Resource, hosts []RecipeHost) []string {
	seen := map[string]bool{}
	var out []string
	for _, host := range hosts {
		if !host.Available || !containsResource(host.Products, resource) {
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
// buildable without research, power or construction skill), or cannot be
// served yet. It issues no orders and reserves nothing.
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
	unknown := false
	for _, name := range WorkshopBenchCandidates(r.Resource, r.Hosts) {
		d, exists := byName[name]
		available, ak := d.Available.Value()
		powered, pk := d.NeedsPower.Value()
		skill, sk := d.ConstructionSkill.Value()
		if !exists || !ak || !pk || !sk {
			unknown = true
			continue
		}
		if available && !powered && skill == 0 {
			return WorkshopChoice{Method: WorkshopBuild, Definition: name, Recipe: workshopRecipe(r.Resource, r.Hosts, name)}, nil
		}
	}
	if unknown {
		return WorkshopChoice{Method: WorkshopUnknown}, nil
	}
	return WorkshopChoice{Method: WorkshopUnavailable}, nil
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
