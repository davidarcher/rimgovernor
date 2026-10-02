package bridge

import o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

func validateEventLoot(v *o.ColonyFactsSnapshot) error {
	if v.EventLoot == nil {
		return nil
	}
	switch section := v.EventLoot.Outcome.(type) {
	case *o.LootSection_Unavailable:
		return validateUnavailable(section.Unavailable)
	case *o.LootSection_Observed:
		if section.Observed == nil {
			return contract("invalid event loot census")
		}
		seen := map[string]bool{}
		for _, row := range section.Observed.Items {
			if row == nil || row.Item == nil || row.Forbidden == nil {
				return contract("incomplete event loot item")
			}
			item := row.Item
			if !validRef(item) || seen[item.GetId()] {
				return contract("invalid event loot item")
			}
			seen[item.GetId()] = true
		}
		return nil
	default:
		return contract("missing event loot outcome")
	}
}
