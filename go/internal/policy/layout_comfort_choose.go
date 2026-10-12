package policy

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ChooseComfort advises a comfort tier for the sleep node and each worked
// bench. The bed is chosen first against the planner's own, then each worked
// node in graph order against the choices so far: a chair is worth what it adds
// to the day the bed and the earlier chairs leave, so a bed that already holds
// the level makes a work chair worth little. Each choice maximises mood benefit
// less cost in watts and is the current behaviour when none beats it; nothing is
// vetoed. An unknown number leaves every choice unpriced.
func ChooseComfort(in ComfortInput) []ComfortChoice {
	need := in.Model.ComfortNeed()
	price := EnvPrice{Mood: in.Mood, WorkDayW: in.WorkDayW, Pawns: in.Pawns, ExpectedMood: in.ExpectedMood}
	weights := DefaultSiteTypeWeights()
	buildW := func(o ComfortOption) float64 { return BuildW(weights, o.Build, in.Steel) }
	known := need.Curve.Valid() && in.Pawns > 0 && finitePositive(in.Steel) && finitePositive(in.WorkDayW) && in.ExpectedMood >= 0 && in.ExpectedMood <= 1

	var sleep *StationNode
	var work []StationNode
	for i, n := range in.Graph.Nodes {
		switch {
		case n.Purpose == PurposeSleep && n.Floor == 0 && sleep == nil:
			sleep = &in.Graph.Nodes[i]
		case n.Purpose == PurposeWork && n.DailyWork > 0 && n.Count > 0:
			work = append(work, n)
		}
	}
	var out []ComfortChoice
	bedTarget, bedKnown := 0.0, false
	if sleep != nil {
		base, found := bedOption(in.Model.Beds, in.Baseline)
		choice := ComfortChoice{Node: sleep.ID, Option: base}
		if known && found {
			dayOf := func(o ComfortOption) []EnvSegment { return envDay(o.Comfort+in.Model.BedOffset, nil) }
			choice = chooseOption(need, price, choice.Node, dayOf(base), base, undominatedWith(in.Model.Beds, base), float64(sleep.Count), buildW, dayOf)
			bedTarget, bedKnown = choice.Option.Comfort+in.Model.BedOffset, true
		}
		out = append(out, choice)
	}
	var spots []EnvSpot
	for _, n := range work {
		choice := ComfortChoice{Node: n.ID}
		if known && bedKnown {
			hours := n.DailyWork / domain.TicksPerHour / float64(in.Pawns)
			earlier := slices.Clone(spots)
			dayOf := func(o ComfortOption) []EnvSegment {
				return envDay(bedTarget, append(slices.Clone(earlier), EnvSpot{Hours: hours, Target: o.Comfort}))
			}
			choice = chooseOption(need, price, n.ID, envDay(bedTarget, earlier), ComfortOption{}, undominated(in.Model.Chairs), float64(n.Count), buildW, dayOf)
			if choice.Option.Def != "" {
				spots = append(spots, EnvSpot{Hours: hours, Target: choice.Option.Comfort})
			}
		}
		out = append(out, choice)
	}
	return out
}

// workChairsOf is the work chairs chosen for the group's worked benches, by
// bench definition: a priced choice of a chair over the baseline of none. A node
// left unpriced, or with no chair chosen, adds none, which leaves the bench as
// it was. Nil when no node has one.
func workChairsOf(g RoomGroup, comfort []ComfortChoice) map[string]string {
	var out map[string]string
	for _, s := range g.Stations {
		if s.Purpose != PurposeWork {
			continue
		}
		for _, c := range comfort {
			if c.Node == s.Node && c.Priced && c.Option.Def != "" {
				if out == nil {
					out = map[string]string{}
				}
				out[s.Def] = c.Option.Def
			}
		}
	}
	return out
}

// undominatedWith is the undominated options with base kept: the baseline stays
// a candidate however the rest rank.
func undominatedWith(options []ComfortOption, base ComfortOption) []ComfortOption {
	return undominated(append(slices.Clone(options), base))
}

// bedOption is the baseline bed: the def's option with the most comfort per
// cost (the planner's own rule), the most comfort when free, by stuff when equal.
func bedOption(beds []ComfortOption, def string) (ComfortOption, bool) {
	var best ComfortOption
	found := false
	for _, o := range beds {
		if o.Def != def {
			continue
		}
		if !found || comfortRatio(o) > comfortRatio(best) ||
			comfortRatio(o) == comfortRatio(best) && (o.Comfort > best.Comfort || o.Comfort == best.Comfort && o.Stuff < best.Stuff) {
			best, found = o, true
		}
	}
	return best, found
}

func comfortRatio(o ComfortOption) float64 {
	if o.Build <= 0 {
		return math.Inf(1)
	}
	return o.Comfort / o.Build
}

// chooseOption prices each option's day against the base day (the baseline
// piece's) and returns the one whose mood benefit less its extra cost over the
// baseline is largest and positive, count pieces to a node; the baseline stands
// otherwise. A loss of mood is a negative benefit and a cheaper piece a saving,
// so a cheaper bed can win. An option that cannot be priced leaves the choice
// unpriced.
func chooseOption(need SeekerNeed, price EnvPrice, node string, baseDay []EnvSegment, baseline ComfortOption, options []ComfortOption, count float64, buildW func(ComfortOption) float64, dayOf func(ComfortOption) []EnvSegment) ComfortChoice {
	best, bestNet := ComfortChoice{Node: node, Option: baseline, Priced: true}, 0.0
	baseCost := buildW(baseline) * count
	for _, o := range options {
		if o == baseline {
			continue
		}
		change, ok := need.Price(baseDay, dayOf(o), price)
		if !ok {
			return ComfortChoice{Node: node, Option: baseline}
		}
		cost := buildW(o)*count - baseCost
		if net := change.BenefitW - cost; net > bestNet {
			best, bestNet = ComfortChoice{Node: node, Option: o, Priced: true, Gain: change.Gain, BenefitW: change.BenefitW, CostW: cost}, net
		}
	}
	return best
}

// adviseComfort prices the comfort tiers over the station graph and the colony's
// facts: the colonists, a work-day in watts, steel's value and the colonists'
// mean mood. Nil while the comfort model or any fact is unknown, which leaves
// the furnisher's choice as it is.
func adviseComfort(f StationFacts, graph StationGraph, steel float64) []ComfortChoice {
	colonists, ok := f.Colonists.Value()
	mood, mk := f.ExpectedMood.Value()
	if !ok || !mk || colonists <= 0 || !f.Catalog.Mood.Break.Valid() {
		return nil
	}
	return ChooseComfort(ComfortInput{
		Model: f.Catalog.Comfort, Mood: f.Catalog.Mood, Graph: graph, Baseline: f.Furniture.PrimaryBed(),
		Pawns: int(colonists), WorkDayW: stationWorkDayW(f), ExpectedMood: mood, Steel: steel,
	})
}
