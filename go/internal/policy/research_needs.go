package policy

import "sort"

// KnowledgeSlot is one KnowledgeCategoryDef's research slot (Anomaly):
// the project the research manager funds from that category's knowledge, ""
// when the slot is empty. Knowledge that arrives for a category with no
// project is lost unless the category overflows into another, so an empty
// slot with a project to fund is a spending need.
//
// Categories are independent: a project is funded only by its own category's
// slot. Advanced knowledge overflows into Basic when its project finishes or
// is unset (ResearchManager.ApplyKnowledge, KnowledgeCategoryDef
// overflowCategory), never the other way, so Basic knowledge cannot fund an
// Advanced project and each slot is filled for its own category alone.
type KnowledgeSlot struct {
	Category string
	Current  ResearchProjectID
}

// KnowledgePick is the knowledge project to put in the first empty knowledge
// slot (by category name) that has a project to fund, or "" when none does.
// A project qualifies when the native census listed it with no lock reason
// (prerequisites finished, not hidden by the entity codex, no other native
// requirement unmet) and it is unfinished; the cheapest by apparent cost
// funds first, ties by name, so the slot finishes and unlocks the next
// project soonest. Projects with another category than the slot's never
// qualify, whatever the stock of the other category.
func KnowledgePick(projects map[string]ResearchProjectFacts, finished []string, slots []KnowledgeSlot) string {
	done := make(map[string]bool, len(finished))
	for _, name := range finished {
		done[name] = true
	}
	ordered := append([]KnowledgeSlot(nil), slots...)
	sort.Slice(ordered, func(i, j int) bool { return ordered[i].Category < ordered[j].Category })
	for _, slot := range ordered {
		if slot.Current != "" {
			continue
		}
		pick, cost := "", 0.0
		for name, row := range projects {
			if row.KnowledgeCategory != slot.Category || done[name] || !row.Census || len(row.LockReasons) != 0 {
				continue
			}
			if hidden, known := row.Hidden.Value(); !known || hidden {
				continue
			}
			if pick == "" || row.Cost < cost || row.Cost == cost && name < pick {
				pick, cost = name, row.Cost
			}
		}
		if pick != "" {
			return pick
		}
	}
	return ""
}
