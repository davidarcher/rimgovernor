package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// FireSafetyPawnFacts mirrors SecureSuppliesHaulerFacts' eligibility shape
// applied to the Firefighting work type: the
// candidate filter for the firefight action (dead/downed/drafted/mental
// state, needs-tend/bleeding, and an enabled non-zero-
// priority work setting).
type FireSafetyPawnFacts struct {
	Pawn                               domain.PawnID
	Dead, Downed, Drafted, MentalState domain.Fact[bool]
	PlayerForced, NeedsTend, Bleeding  domain.Fact[bool]
	FirefightingEnabled                domain.Fact[bool]
}

// FireSafetyOutcome names the three-way firefight branch: recovered (no
// active fire), waiting on the
// native, non-orderable firefighting WorkGiver because an eligible worker
// exists and the fire is bounded, or blocked because no such worker or
// bound exists and the emergency hold must be retained.
type FireSafetyOutcome string

const (
	FireSafetyRecovered        FireSafetyOutcome = "recovered"
	FireSafetyWaitingForNative FireSafetyOutcome = "waiting_for_native_fire"
	FireSafetyBlocked          FireSafetyOutcome = "blocked"
	FireSafetyUnknown          FireSafetyOutcome = "unknown"
)

// EvaluateFireSafety decides the firefight branch from the evidence this
// project's wire schema actually exposes: UpkeepFire only carries id/home/size
// (see policy.UpkeepFire, observation/colony_upkeep.go), so no per-target safe-worker
// list exists to thread through. This deliberately narrows to a colony-wide
// eligible-firefighter existence check paired with ReviewUpkeep's own Unsafe
// verdict (more than three home fires, or any fire with unmeasured or >1
// size) rather than fabricate a per-fire safety claim the native layer has
// not itself confirmed. `active`/`known`/`unsafe` are the same fields
// ReviewUpkeep already produces for the MaintainFireSafety UpkeepNeed.
func EvaluateFireSafety(active, known, unsafe bool, pawns []FireSafetyPawnFacts) FireSafetyOutcome {
	if !active {
		return FireSafetyRecovered
	}
	if !known {
		return FireSafetyUnknown
	}
	eligible := false
	for _, p := range pawns {
		if fireSafetyEligible(p) {
			eligible = true
			break
		}
	}
	if eligible && !unsafe {
		return FireSafetyWaitingForNative
	}
	return FireSafetyBlocked
}

func fireSafetyEligible(p FireSafetyPawnFacts) bool {
	dead, dk := p.Dead.Value()
	downed, wk := p.Downed.Value()
	drafted, tk := p.Drafted.Value()
	mental, mk := p.MentalState.Value()
	needsTend, nk := p.NeedsTend.Value()
	bleeding, bk := p.Bleeding.Value()
	firefighting, hk := p.FirefightingEnabled.Value()
	if !dk || !wk || !tk || !mk || !nk || !bk || !hk {
		return false
	}
	return !dead && !downed && !drafted && !mental && !needsTend && !bleeding && firefighting
}
