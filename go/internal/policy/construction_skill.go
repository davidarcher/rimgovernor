package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"sort"
)

type ConstructionSkillChoice struct {
	Site    ConstructionSite
	Minimum int
}

// ConstructionSkillChoices adopts each unconfigured quality site once. Current
// jobs, timetable, priorities and sleep do not reduce construction capability.
func ConstructionSkillChoices(census domain.Fact[CurrentConstruction], pawns domain.Fact[[]WorkPawn]) []ConstructionSkillChoice {
	sites, known := census.Value()
	workers, wk := pawns.Value()
	if !known || !sites.Colony || !wk {
		return nil
	}
	best, found := 0, false
	for _, pawn := range workers {
		applies, ak := pawn.Applies.Value()
		available, vk := pawn.ConstructionAble.Value()
		if ak && !applies || vk && !available {
			continue
		}
		skills, sk := pawn.Skills.Value()
		work, pk := pawn.Work.Value()
		if !ak || !vk || !sk || !pk {
			return nil
		}
		profile := BuildProfile(pawn)
		if profile.Forbidden(WorkConstruction) || profile.Incapable[WorkConstruction] {
			continue
		}
		able := false
		for _, w := range work {
			if w.Work == WorkConstruction && !w.Disabled {
				able = true
			}
		}
		if !able {
			continue
		}
		for _, skill := range skills {
			if skill.Name == "Construction" && !skill.Disabled {
				best, found = max(best, skill.Level), true
			}
		}
	}
	if !found {
		return nil
	}
	var out []ConstructionSkillChoice
	for _, site := range sites.Sites {
		if site.ID == "" || site.QualitySensitive != domain.Known(true) {
			continue
		}
		if _, set := site.MinimumFinishingSkill.Value(); set {
			continue
		}
		native, nk := site.NativeFinishingSkill.Value()
		if !nk || best < native {
			continue
		}
		out = append(out, ConstructionSkillChoice{site, best})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Site.ID < out[j].Site.ID })
	return out
}

// ConstructionHelperView augments action progress with native site facts.
// A placed blueprint is not a finished building, but a filled frame is work
// vanilla can offer now. Unknown/material-blocked sites never invent readiness.
func ConstructionHelperView(previous *ReadyWorkReport, census domain.Fact[CurrentConstruction], current domain.GenerationSnapshot) *ReadyWorkReport {
	observed, known := census.Value()
	if !known || !observed.Colony {
		return previous
	}
	report := ReadyWorkReport{Colony: current.Colony, Map: current.Map, Load: current.Load}
	if previous != nil && previous.Current(current) {
		report = *previous
		report.Candidates = append([]ReadyWork(nil), previous.Candidates...)
	}
	for _, site := range observed.Sites {
		if !HelperConstructionDefinitions[site.Building.Definition()] {
			continue
		}
		claim := CellClaim(site.Building.Cell())
		row := ReadyWork{Stage: "building:" + site.Building.Definition(), Work: LaborProfile{WorkConstruction}, State: ReadyRunnable, Parallelism: 1, Claims: []ReadyClaim{claim}, Reason: "frame_materials_complete"}
		if site.Stage != "frame" || site.ResourcesComplete != domain.Known(true) {
			row.State, row.Parallelism, row.Reason = ReadyAwaiting, 0, "construction_materials_pending"
		}
		found := false
		for i, c := range report.Candidates {
			if c.Stage != row.Stage {
				continue
			}
			for _, cc := range c.Claims {
				if cc == claim {
					report.Candidates[i] = row
					found = true
					break
				}
			}
		}
		if !found {
			report.Candidates = append(report.Candidates, row)
		}
	}
	return &report
}

func protectedQualityDefinitions(census domain.Fact[CurrentConstruction]) map[string]bool {
	out := map[string]bool{}
	observed, known := census.Value()
	if !known || !observed.Colony {
		return out
	}
	blocked := map[string]bool{}
	for _, site := range observed.Sites {
		name := site.Building.Definition()
		_, set := site.MinimumFinishingSkill.Value()
		if site.QualitySensitive == domain.Known(true) && set {
			out[name] = true
		} else {
			blocked[name] = true
		}
	}
	for name := range blocked {
		delete(out, name)
	}
	return out
}
