package bridge

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// QuestProfile classifies one catalog root without inventing a default family.
// Odyssey keeps its layer-derived classification; other roots use the explicit
// game-data table. Auto-accepted roots are never proactively acted on.
func (catalog *DefinitionCatalog) QuestProfile(scriptDef string) (policy.QuestProfile, error) {
	row := DefRow[*d.QuestScriptDef](catalog, scriptDef)
	if row == nil {
		return policy.QuestProfile{}, contract("catalog has no QuestScriptDef row %s", scriptDef)
	}
	profile := policy.QuestFamilyForRoot(scriptDef)
	if profile.Family == policy.QuestFamilyUnknown {
		return policy.QuestProfile{}, contract("quest script %s has no known family", scriptDef)
	}
	if strings.EqualFold(row.GetModPackageId(), odysseyPackageID) {
		class, err := catalog.QuestClass(scriptDef)
		if err != nil {
			return policy.QuestProfile{}, err
		}
		if profile.Family != policy.QuestFamilyUtility && class.Scope == policy.QuestScopeShipOnly {
			profile.Family = policy.QuestFamilyOdysseyShipOnly
			profile.NeverAct = true
			profile.Disposition = policy.QuestRefuse
			profile.SkipReason = "ship_only"
		}
	}
	if profile.Family == policy.QuestFamilyUnknown {
		return policy.QuestProfile{}, contract("quest script %s has no known family", scriptDef)
	}
	profile.NeverAct = profile.NeverAct || row.GetAutoAccept()
	if row.GetAutoAccept() && profile.Disposition == policy.QuestDecide {
		profile.Disposition = policy.QuestObserve
	}
	return profile, nil
}
