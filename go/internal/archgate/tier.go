package archgate

import (
	"go/ast"
	"go/types"
	"strings"
)

// tierPassThrough are the two places that rebuild an action whose tier was
// already stated: NewPlan canonicalizes a plan's actions and scanAction reads
// a stored row (a row without a tier does not load). Everything else states
// the tier itself.
var tierPassThrough = map[string]bool{
	"internal/domain/plan.go|NewPlan":          true,
	"internal/store/action_rows.go|scanAction": true,
}

// tierMappings are the pure policy functions that return a tier from the
// ladder's tables (policy/construction_tier.go).
var tierMappings = map[string]bool{"RoomTier": true, "PlannerTier": true, "AdoptedTier": true}

// BuildingActionTiers is the construction tier completeness check (#2525,
// docs/developers/contracts/construction-tiers.md): every non-test call of
// domain.NewBuildingAction states its tier as a ladder constant
// (domain.TierSurvive ... TierSecure) or through a policy mapping function
// (RoomTier, PlannerTier, AdoptedTier). It returns "file|func|reason" for each
// caller that omits the tier or passes anything else. There is no baseline.
func BuildingActionTiers(root string) []string {
	bad := map[string]bool{}
	for _, f := range load(root, []string{"internal", "cmd"}, true) {
		for _, d := range f.node.Decls {
			name := funcName(d)
			ast.Inspect(d, func(n ast.Node) bool {
				call, ok := n.(*ast.CallExpr)
				if !ok || !callsBuildingAction(call.Fun, f.pkg) {
					return true
				}
				key := f.rel + "|" + name
				switch {
				case len(call.Args) != 3:
					bad[key+"|no tier argument"] = true
				case tierPassThrough[key]:
				case !statesTier(call.Args[2], f.pkg):
					bad[key+"|tier is not a ladder constant or policy mapping: "+types.ExprString(call.Args[2])] = true
				}
				return true
			})
		}
	}
	return sorted(bad)
}

func callsBuildingAction(fun ast.Expr, pkg string) bool {
	switch f := fun.(type) {
	case *ast.SelectorExpr:
		x, ok := f.X.(*ast.Ident)
		return ok && x.Name == "domain" && f.Sel.Name == "NewBuildingAction"
	case *ast.Ident:
		return pkg == "domain" && f.Name == "NewBuildingAction"
	}
	return false
}

func statesTier(arg ast.Expr, pkg string) bool {
	switch a := arg.(type) {
	case *ast.SelectorExpr:
		x, ok := a.X.(*ast.Ident)
		return ok && x.Name == "domain" && strings.HasPrefix(a.Sel.Name, "Tier")
	case *ast.Ident:
		return pkg == "domain" && strings.HasPrefix(a.Name, "Tier")
	case *ast.CallExpr:
		sel, ok := a.Fun.(*ast.SelectorExpr)
		if !ok {
			return false
		}
		x, ok := sel.X.(*ast.Ident)
		return ok && x.Name == "policy" && tierMappings[sel.Sel.Name]
	}
	return false
}
