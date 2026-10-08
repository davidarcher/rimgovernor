package policy

import (
	"cmp"
	"encoding/json"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"slices"
)

// DesignScore orders concrete restrictions before worst known mood cost,
// then mandatory obligations. Benefits break ties; no mixed-unit weights.
type DesignScore struct {
	Restrictions int
	MoodCost     float64
	Obligations  int
	MoodBenefit  float64
}

const ImproveIdeoligion ConcernID = "ImproveIdeoligion"

func inspectIdeoligion(c *roundsRun) error { c.owed(ImproveIdeoligion, 3, c.f.ReformOwed); return nil }

func (s DesignScore) Compare(t DesignScore) int {
	if v := cmp.Compare(s.Restrictions, t.Restrictions); v != 0 {
		return v
	}
	if v := cmp.Compare(s.MoodCost, t.MoodCost); v != 0 {
		return v
	}
	if v := cmp.Compare(s.Obligations, t.Obligations); v != 0 {
		return v
	}
	return cmp.Compare(t.MoodBenefit, s.MoodBenefit)
}
func (s DesignScore) add(t DesignScore) DesignScore {
	return DesignScore{s.Restrictions + t.Restrictions, s.MoodCost + t.MoodCost, s.Obligations + t.Obligations, s.MoodBenefit + t.MoodBenefit}
}

type DesignMeme struct {
	Name                                        string
	Structure, Allowed, InitialFluid, Supported bool
	Exclusions                                  []string
	RequireOne                                  [][]string
	Obligations                                 int
}
type DesignPrecept struct {
	Name, Issue                                 string
	Default, Allowed, Supported                 bool
	RequiredMemes, ConflictingMemes, Exclusions []string
	Score                                       DesignScore
	WorkTypes, Thoughts                         []string
	PotentialMoodBenefit                        float64
}
type DesignOptions struct {
	Memes         []DesignMeme
	Precepts      []DesignPrecept
	RequiredMemes []string
	Needs         domain.Fact[DesignNeeds]
}
type DesignNeeds struct {
	WorkTypes []string
	Thoughts  domain.Fact[[]MoodThought]
	Believers int
}
type DesignChoice struct {
	Design     domain.IdeoligionDesign
	Score      DesignScore
	Candidates int
	Comparison *DesignComparison
}

type DesignDecision string

const (
	DesignImproves   DesignDecision = "improves"
	DesignUnchanged  DesignDecision = "no_improvement"
	DesignIneligible DesignDecision = "ineligible"
	DesignUnsafe     DesignDecision = "unsafe"
)

type DesignComparison struct {
	Decision                DesignDecision
	Current, Candidate      DesignScore
	WorkRelief              bool
	MoodRelief, NewMoodCost float64
}

// IdeoligionNeeds uses assigned work and mood with complete believer coverage.
func IdeoligionNeeds(ideo IdeoligionFacts, workers domain.Fact[[]WorkPawn], moods domain.Fact[[]MoodPawn]) domain.Fact[DesignNeeds] {
	rows, known := workers.Value()
	if !known || ideo.Believers <= 0 {
		return domain.Unknown[DesignNeeds]()
	}
	needs := DesignNeeds{Believers: ideo.Believers}
	moodRows, moodKnown := moods.Value()
	var thoughts []MoodThought
	believers := 0
	for _, pawn := range rows {
		inputs, known := pawn.PolicyInputs.Value()
		if !known {
			return domain.Unknown[DesignNeeds]()
		}
		if inputs.Ideo != ideo.IdeoID {
			continue
		}
		believers++
		work, known := pawn.Work.Value()
		if !known {
			return domain.Unknown[DesignNeeds]()
		}
		for _, assignment := range work {
			if assignment.Priority > 0 && !assignment.Disabled && !slices.Contains(needs.WorkTypes, string(assignment.Work)) {
				needs.WorkTypes = append(needs.WorkTypes, string(assignment.Work))
			}
		}
		i := slices.IndexFunc(moodRows, func(row MoodPawn) bool { return row.ID == pawn.ID })
		if i < 0 {
			moodKnown = false
			continue
		}
		observed, known := moodRows[i].Thoughts.Value()
		if !known {
			moodKnown = false
			continue
		}
		thoughts = append(thoughts, observed...)
	}
	if believers != ideo.Believers {
		return domain.Unknown[DesignNeeds]()
	}
	slices.Sort(needs.WorkTypes)
	if moodKnown {
		needs.Thoughts = domain.Known(thoughts)
	}
	return domain.Known(needs)
}

func designOverlap(a, b []string) bool {
	for _, v := range a {
		if slices.Contains(b, v) {
			return true
		}
	}
	return false
}

func ScoreIdeoligion(options DesignOptions, design domain.IdeoligionDesign) domain.Fact[DesignScore] {
	if _, err := domain.CanonicalIdeoligionDesign(design); err != nil {
		return domain.Unknown[DesignScore]()
	}
	var score DesignScore
	var exclusions []string
	structures, normal := 0, 0
	for _, name := range design.Memes {
		i := slices.IndexFunc(options.Memes, func(m DesignMeme) bool { return m.Name == name })
		if i < 0 || !options.Memes[i].Supported || !options.Memes[i].Allowed {
			return domain.Unknown[DesignScore]()
		}
		m := options.Memes[i]
		if m.Structure {
			structures++
		} else {
			normal++
		}
		if designOverlap(exclusions, m.Exclusions) {
			return domain.Unknown[DesignScore]()
		}
		exclusions = append(exclusions, m.Exclusions...)
		score.Obligations += m.Obligations
		for _, group := range m.RequireOne {
			if !designOverlap(group, design.Precepts) {
				return domain.Unknown[DesignScore]()
			}
		}
	}
	if structures != 1 || normal < 1 || normal > 4 {
		return domain.Unknown[DesignScore]()
	}
	for _, name := range options.RequiredMemes {
		if !slices.Contains(design.Memes, name) {
			return domain.Unknown[DesignScore]()
		}
	}
	exclusions = nil
	seen := map[string]bool{}
	for _, name := range design.Precepts {
		i := slices.IndexFunc(options.Precepts, func(p DesignPrecept) bool { return p.Name == name })
		if i < 0 {
			return domain.Unknown[DesignScore]()
		}
		p := options.Precepts[i]
		if !p.Allowed || !p.Supported || seen[p.Issue] || designOverlap(exclusions, p.Exclusions) || designOverlap(design.Memes, p.ConflictingMemes) || (len(p.RequiredMemes) > 0 && !designOverlap(design.Memes, p.RequiredMemes)) {
			return domain.Unknown[DesignScore]()
		}
		seen[p.Issue] = true
		exclusions = append(exclusions, p.Exclusions...)
		score = score.add(p.Score)
	}
	for _, p := range options.Precepts {
		if p.Default && !seen[p.Issue] {
			return domain.Unknown[DesignScore]()
		}
	}
	return domain.Known(score)
}

// ChooseStartingIdeoligion searches initial fluid designs (one normal meme).
// The game-sourced initial fluid range is one; native rechecks every request.
// A count guard bounds pathological catalogs without returning a partial best.
func ChooseStartingIdeoligion(options DesignOptions) domain.Fact[DesignChoice] {
	memes := slices.Clone(options.Memes)
	slices.SortFunc(memes, func(a, b DesignMeme) int { return cmp.Compare(a.Name, b.Name) })
	var best *DesignChoice
	visits := 0
	exhausted := false
	for _, structure := range memes {
		if !structure.Structure || !structure.Allowed || !structure.Supported {
			continue
		}
		for _, normal := range memes {
			if normal.Structure || !normal.Allowed || !normal.Supported || !normal.InitialFluid {
				continue
			}
			selected := []string{structure.Name, normal.Name}
			if designOverlap(structure.Exclusions, normal.Exclusions) {
				continue
			}
			issues := map[string][]DesignPrecept{}
			mandatory := map[string]bool{}
			for _, p := range options.Precepts {
				if p.Default {
					mandatory[p.Issue] = true
				}
				if p.Allowed && p.Supported && !designOverlap(selected, p.ConflictingMemes) && (len(p.RequiredMemes) == 0 || designOverlap(selected, p.RequiredMemes)) {
					issues[p.Issue] = append(issues[p.Issue], p)
				}
			}
			for _, m := range []DesignMeme{structure, normal} {
				for _, group := range m.RequireOne {
					for _, name := range group {
						for _, p := range options.Precepts {
							if p.Name == name {
								mandatory[p.Issue] = true
							}
						}
					}
				}
			}
			keys := make([]string, 0, len(mandatory))
			for key := range mandatory {
				keys = append(keys, key)
			}
			slices.Sort(keys)
			for _, key := range keys {
				slices.SortFunc(issues[key], func(a, b DesignPrecept) int {
					if c := a.Score.Compare(b.Score); c != 0 {
						return c
					}
					return cmp.Compare(a.Name, b.Name)
				})
			}
			lower := make([]DesignScore, len(keys)+1)
			valid := true
			for i := len(keys) - 1; i >= 0; i-- {
				if len(issues[keys[i]]) == 0 {
					valid = false
					break
				}
				lower[i] = lower[i+1].add(issues[keys[i]][0].Score)
			}
			if !valid {
				continue
			}
			var walk func(int, []string, []string, DesignScore)
			walk = func(i int, chosen, tags []string, score DesignScore) {
				visits++
				if visits > 100000 {
					exhausted = true
					return
				}
				if best != nil && score.add(lower[i]).Compare(best.Score) >= 0 {
					return
				}
				if i == len(keys) {
					design, err := domain.CanonicalIdeoligionDesign(domain.IdeoligionDesign{Memes: selected, Precepts: chosen, Fluid: true})
					if err != nil {
						return
					}
					evaluated, known := ScoreIdeoligion(options, design).Value()
					if !known {
						return
					}
					encoded, _ := json.Marshal(design)
					better := best == nil
					if best != nil {
						old, _ := json.Marshal(best.Design)
						c := evaluated.Compare(best.Score)
						better = c < 0 || (c == 0 && string(encoded) < string(old))
					}
					if better {
						best = &DesignChoice{Design: design, Score: evaluated}
					}
					return
				}
				for _, p := range issues[keys[i]] {
					if !designOverlap(tags, p.Exclusions) {
						walk(i+1, append(slices.Clone(chosen), p.Name), append(slices.Clone(tags), p.Exclusions...), score.add(p.Score))
						if exhausted {
							return
						}
					}
				}
			}
			walk(0, nil, nil, DesignScore{Obligations: structure.Obligations + normal.Obligations})
			if exhausted {
				return domain.Unknown[DesignChoice]()
			}
		}
	}
	if best == nil {
		return domain.Unknown[DesignChoice]()
	}
	best.Candidates = visits
	return domain.Known(*best)
}

// Reform keeps memes and special instances when their transition cost cannot
// be valued. Plain-precept changes may improve known restrictions/mood while
// preserving obligations and every unvalued effect.
func ChooseIdeoligionReform(options DesignOptions, current domain.IdeoligionDesign, development domain.Fact[IdeoDevelopment], calm domain.Fact[bool]) domain.Fact[DesignChoice] {
	state, known := development.Value()
	safe, safetyKnown := calm.Value()
	if !known || !safetyKnown {
		return domain.Unknown[DesignChoice]()
	}
	if !safe || !state.Fluid || !state.CanReform {
		decision := DesignIneligible
		if !safe {
			decision = DesignUnsafe
		}
		return domain.Known(DesignChoice{Comparison: &DesignComparison{Decision: decision}})
	}
	needs, known := options.Needs.Value()
	if !known || needs.Believers <= 0 {
		return domain.Unknown[DesignChoice]()
	}
	baseline, known := ScoreIdeoligion(options, current).Value()
	if !known {
		return domain.Unknown[DesignChoice]()
	}
	var best *DesignChoice
	for i, old := range current.Precepts {
		for _, p := range options.Precepts {
			if p.Name == old {
				continue
			}
			oldIndex := slices.IndexFunc(options.Precepts, func(p DesignPrecept) bool { return p.Name == old })
			if oldIndex < 0 || p.Issue != options.Precepts[oldIndex].Issue {
				continue
			}
			oldPrecept := options.Precepts[oldIndex]
			workImproves := designOverlap(oldPrecept.WorkTypes, needs.WorkTypes) && !designOverlap(p.WorkTypes, needs.WorkTypes)
			penalty, benefit := 0.0, 0.0
			thoughts, thoughtsKnown := needs.Thoughts.Value()
			for _, thought := range thoughts {
				if slices.Contains(oldPrecept.Thoughts, thought.Def) && !slices.Contains(p.Thoughts, thought.Def) {
					if thought.Offset < 0 {
						penalty -= thought.Offset
					} else {
						benefit += thought.Offset
					}
				}
			}
			if benefit > 0 || (!thoughtsKnown && oldPrecept.Score.MoodBenefit+oldPrecept.PotentialMoodBenefit > 0) || p.PotentialMoodBenefit < oldPrecept.PotentialMoodBenefit {
				continue
			}
			moodImproves := thoughtsKnown && penalty > p.Score.MoodCost*float64(needs.Believers)
			if !workImproves && !moodImproves {
				continue
			}
			// Unknown transition effects are marked unsupported by the catalog adapter.
			next := domain.IdeoligionDesign{Memes: slices.Clone(current.Memes), Precepts: slices.Clone(current.Precepts), Fluid: true}
			next.Precepts[i] = p.Name
			score, known := ScoreIdeoligion(options, next).Value()
			if !known || score.Compare(baseline) >= 0 || score.MoodBenefit < baseline.MoodBenefit || score.MoodCost > baseline.MoodCost || score.Obligations > baseline.Obligations || score.Restrictions > baseline.Restrictions {
				continue
			}
			canonical, err := domain.CanonicalIdeoligionDesign(next)
			if err != nil {
				continue
			}
			encoded, _ := json.Marshal(canonical)
			oldBest := ""
			if best != nil {
				b, _ := json.Marshal(best.Design)
				oldBest = string(b)
			}
			if best == nil || score.Compare(best.Score) < 0 || (score.Compare(best.Score) == 0 && string(encoded) < oldBest) {
				best = &DesignChoice{Design: canonical, Score: score, Comparison: &DesignComparison{Decision: DesignImproves, Current: baseline, Candidate: score, WorkRelief: workImproves, MoodRelief: penalty, NewMoodCost: p.Score.MoodCost * float64(needs.Believers)}}
			}
		}
	}
	if best == nil {
		return domain.Known(DesignChoice{Comparison: &DesignComparison{Decision: DesignUnchanged, Current: baseline, Candidate: baseline}})
	}
	return domain.Known(*best)
}
