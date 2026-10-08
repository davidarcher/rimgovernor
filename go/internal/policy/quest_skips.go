package policy

import "sort"

// QuestSkips explains refusals with the same family decision used for admission.
func QuestSkips(f RoundsFacts) []QuestSkip {
	rows, known := f.QuestOffers.Value()
	if !known {
		return nil
	}
	var out []QuestSkip
	for _, offer := range rows {
		if claimAnswerable(offer, f.TitleClaimQuests) {
			continue
		}
		if _, reason := questDecision(offer, f); reason != "" {
			detail := ""
			if reason == "class_unknown" {
				detail = offer.ClassError
			}
			if reason == "ship_only" {
				if class, known := offer.Class.Value(); known {
					detail = class.SpaceLayer
				}
			}
			out = append(out, QuestSkip{Quest: offer.Quest, ScriptDef: offer.ScriptDef, Reason: reason, Detail: detail})
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Quest < out[j].Quest })
	return out
}
