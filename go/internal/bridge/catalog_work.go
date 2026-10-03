package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// WorkTypeSkill is the skill a work type is scored by: the first of its
// WorkTypeDef's relevant skills, "" for unskilled work. A work type the
// catalog lacks is a contract error.
func (catalog *DefinitionCatalog) WorkTypeSkill(work string) (string, error) {
	row := DefRow[*d.WorkTypeDef](catalog, work)
	if row == nil {
		return "", contract("catalog has no work type %s", work)
	}
	if skills := row.GetRelevantSkills(); len(skills) > 0 {
		return skills[0], nil
	}
	return "", nil
}

// ResolveWorkRow sets the work row's Skill and Order from its WorkTypeDef
// (relevant skill and natural priority).
func (catalog *DefinitionCatalog) ResolveWorkRow(row *policy.WorkPriority) error {
	skill, err := catalog.WorkTypeSkill(string(row.Work))
	if err != nil {
		return err
	}
	row.Skill = skill
	row.Order = int(DefRow[*d.WorkTypeDef](catalog, string(row.Work)).GetNaturalPriority())
	return nil
}
