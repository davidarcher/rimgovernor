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
		if section.Observed == nil || len(section.Observed.Items) > 4096 {
			return contract("invalid event loot census")
		}
		seen := map[string]bool{}
		for _, row := range section.Observed.Items {
			if row == nil || row.Item == nil || row.Forbidden == nil {
				return contract("incomplete event loot item")
			}
			item := row.Item
			if validID(item.GetId()) != nil || validID(item.GetDefName()) != nil || seen[item.GetId()] || item.MapId == nil || item.GetMapId() != v.Context.Identity.GetMapId() || !colonyCell(item.Position, v.MapSize) {
				return contract("invalid event loot item")
			}
			seen[item.GetId()] = true
		}
		return nil
	default:
		return contract("missing event loot outcome")
	}
}
