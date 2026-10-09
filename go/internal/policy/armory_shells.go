package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Shells per built mortar: a base stock, doubled at the fabrication tier.
// EMP is stocked only when mech threats are plausible, read as a threat
// tier of fabrication (raid points at armoryFabricationPoints and up, the
// band where mechanoid raids and clusters become routine).
const (
	shellHEPerMortar         = 10
	shellIncendiaryPerMortar = 5
	shellEMPPerMortar        = 5
)

// MortarShellTargets is the shell stock the armory keeps for the built
// mortars under assessment a: none without a mortar or an observed threat,
// and none of a kind the load's shells have no def for.
func MortarShellTargets(mortars int, a ArmoryAssessment, shells MortarShells) []Amount {
	if mortars <= 0 || a.Threat == ArmoryTierUnknown {
		return nil
	}
	scale := int64(mortars)
	if a.Tier >= ArmoryTierFabrication {
		scale *= 2
	}
	var targets []Amount
	add := func(kind ShellKind, perMortar int64) {
		if def := shells.Def(kind); def != "" {
			targets = append(targets, Amount{Resource: Resource(def), Count: perMortar * scale})
		}
	}
	add(ShellHE, shellHEPerMortar)
	add(ShellIncendiary, shellIncendiaryPerMortar)
	if a.Threat >= ArmoryTierFabrication {
		add(ShellEMP, shellEMPPerMortar)
	}
	return targets
}

// ShellsShort reports whether any target's stock is below half its count,
// the point a native stock-target bill resumes at: above it a standing bill
// has nothing to add. Unknown stock with targets is unknown; no targets is
// known false.
func ShellsShort(targets []Amount, stock domain.Fact[map[Resource]int64]) domain.Fact[bool] {
	if len(targets) == 0 {
		return domain.Known(false)
	}
	have, known := stock.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	for _, t := range targets {
		if have[t.Resource]*2 < t.Count {
			return domain.Known(true)
		}
	}
	return domain.Known(false)
}
