package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

var policyDatabases = map[domain.PolicyDatabase]op.PolicyDatabase{
	domain.OutfitPolicies:  op.PolicyDatabase_POLICY_DATABASE_OUTFIT,
	domain.DrugPolicies:    op.PolicyDatabase_POLICY_DATABASE_DRUG,
	domain.FoodPolicies:    op.PolicyDatabase_POLICY_DATABASE_FOOD,
	domain.ReadingPolicies: op.PolicyDatabase_POLICY_DATABASE_READING,
	domain.AllowedAreas:    op.PolicyDatabase_POLICY_DATABASE_ALLOWED_AREA,
}

// policyPruneAction is the PolicyPruneIntent of one database:
// native reassigns holders to their own per-pawn policy, then deletes
// (NativePolicyPrune.cs).
func policyPruneAction(action domain.Action) (*op.Action, error) {
	v, ok := action.PolicyPrune()
	if !ok {
		return nil, contract("not a policy prune action")
	}
	if _, err := domain.NewPolicyPruneAction(action.ID(), v); err != nil {
		return nil, contract("%v", err)
	}
	return &op.Action{Intent: &op.Action_PolicyPrune{PolicyPrune: &op.PolicyPruneIntent{Database: policyDatabases[v.Database()].Enum(), DeleteIds: v.IDs()}}}, nil
}
