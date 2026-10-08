package policy

import (
	"slices"
	"sort"
)

func MonumentAdmission(offer JoinerOffer, f RoundsFacts) QuestSkipReason {
	profile, known := offer.Profile.Value()
	if !known {
		return "class_unknown"
	}
	switch profile.Family {
	case QuestFamilyBuildMonument, QuestFamilyDecreeMonument, QuestFamilyRoyalHeir:
	default:
		return ""
	}
	var marker QuestMonument
	found := false
	timed := false
	for _, objective := range offer.Objectives {
		if m, known := objective.Monument.Value(); known {
			if found {
				return "monument_unknown"
			}
			marker = m
			found = true
			_, d := objective.DeadlineTicks.Value()
			_, t := objective.DurationTicks.Value()
			timed = d || t
		}
	}
	if !found {
		return "monument_unknown"
	}
	if clear, known := marker.ClearSite.Value(); !known {
		return "monument_site_unknown"
	} else if !clear || len(marker.InstallCells) == 0 {
		return "monument_site"
	}
	if !timed {
		return "deadline_unknown"
	}
	deficits, known := f.ConstructionDeficit.Value()
	if !known {
		return "materials_unknown"
	}
	available := map[Resource]int64{}
	for _, a := range marker.AvailableResources {
		available[a.Resource] += a.Count
	}
	for _, a := range marker.SuppliedResources {
		available[a.Resource] += a.Count
	}
	for resource, count := range deficits {
		available[resource] -= count
	}
	// Reserve unmet material for earlier nonautomatic quests before this offer.
	open, known := f.QuestOffers.Value()
	if !known {
		return "open_demands_unknown"
	}
	for _, q := range open {
		if q.State != "Ongoing" || q.Quest == offer.Quest {
			continue
		}
		p, pk := q.Profile.Value()
		if !pk {
			return "open_demands_unknown"
		}
		if p.Cost == QuestCostFree || p.NeverAct || p.Disposition == QuestObserve {
			continue
		}
		for _, o := range q.Objectives {
			if m, mk := o.Monument.Value(); mk {
				if !fundMonument(m, available) {
					return "monument_materials"
				}
			}
		}
	}
	if !fundMonument(marker, available) {
		return "monument_materials"
	}
	if profile.Demands&QuestDemandSecurity != 0 {
		threat, tk := offer.ThreatPoints.Value()
		defense, dk := f.DefenseCapacity.Value()
		if !tk || !dk {
			return "defense_unknown"
		}
		if defense < threat {
			return "defense_capacity"
		}
	}
	return ""
}

func fundMonument(marker QuestMonument, available map[Resource]int64) bool {
	for _, piece := range marker.Pieces {
		if built, known := piece.Built.Value(); known && built {
			continue
		}
		options := slices.Clone(piece.BuildOptions)
		sort.Slice(options, func(i, j int) bool { return options[i].Stuff < options[j].Stuff })
		funded := false
		for _, option := range options {
			costs := map[Resource]int64{}
			for _, c := range option.Costs {
				costs[c.Resource] += c.Count
			}
			enough := true
			for r, n := range costs {
				if n < 0 || available[r] < n {
					enough = false
					break
				}
			}
			if !enough {
				continue
			}
			for r, n := range costs {
				available[r] -= n
			}
			funded = true
			break
		}
		if !funded {
			return false
		}
	}
	return true
}
