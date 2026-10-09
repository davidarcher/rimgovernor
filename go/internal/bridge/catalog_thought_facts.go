package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// ThoughtFacts projects the named ThoughtDef row and the policy audit table
// into the fact the mood ledger reads. A def the catalog lacks is false; a
// def the audit table lacks is projected with the unclassified dependency
// and no owner. Null stages (the game keeps unused ones null) are skipped.
func (catalog *DefinitionCatalog) ThoughtFacts(name string) (policy.ThoughtFacts, bool) {
	row := DefRow[*d.ThoughtDef](catalog, name)
	if row == nil {
		return policy.ThoughtFacts{}, false
	}
	facts := policy.ThoughtFacts{
		Def:                name,
		NullifyingTraits:   row.GetNullifyingTraits(),
		NullifyingPrecepts: row.GetNullifyingPrecepts(),
		RequiredTraits:     row.GetRequiredTraits(),
		MinExpectation:     row.GetMinExpectation(),
		Dependency:         policy.ThoughtUnclassified,
	}
	first := true
	for _, stage := range row.GetStages() {
		if stage.GetValue() == nil {
			continue
		}
		offset := float32Number(stage.GetValue().GetBaseMoodEffect())
		if first || offset < facts.WorstOffset {
			facts.WorstOffset, first = offset, false
		}
	}
	if audit, ok := policy.ThoughtAuditRow(name); ok {
		facts.Dependency, facts.Owner = audit.Dependency, audit.Owner
	}
	return facts, true
}

// AllThoughtFacts is ThoughtFacts for every ThoughtDef row, by defName.
func (catalog *DefinitionCatalog) AllThoughtFacts() map[string]policy.ThoughtFacts {
	out := map[string]policy.ThoughtFacts{}
	for name := range catalogDefs[*d.ThoughtDef](catalog) {
		if facts, ok := catalog.ThoughtFacts(name); ok {
			out[name] = facts
		}
	}
	return out
}
