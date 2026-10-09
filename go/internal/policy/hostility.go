package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Hostility response: the bot owns every colonist's
// Assign-tab hostility response. Attack is the default for anyone capable
// of violence (Flee drops work at the first rat). Flee is for pawns who
// cannot fight or should not: violence-incapable, children, and pawns
// with serious injuries or blood loss. Ignore is task-scoped: while a
// pawn's work-giver job targets a cell beside a known-passive hostile (a
// sleeping hive's jelly) and no engaging hostile is near that cell.
// The mapping is stateless, so the restore needs no memory: when the job
// moves on, the hostiles wake or an engaging hostile comes near, the same
// mapping returns the pawn's default and the planner writes it back.

// ChildAge is the biological age below which a pawn is a child (vanilla's
// DevelopmentalStage.Child ends at 13).
const ChildAge = 13.0

// SeriousBloodLoss is the BloodLoss severity of vanilla's "moderate" stage;
// SeriousInjuryHealth the summary health below which a pawn is badly hurt.
const (
	SeriousBloodLoss    = 0.3
	SeriousInjuryHealth = 0.6
)

// StealthCells is the Chebyshev reach around a job's target inside which a
// passive hostile makes the job stealth work, and an engaging hostile
// makes it unsafe.
const StealthCells = 15

// HostilityPawn is one colonist's hostility inputs.
type HostilityPawn struct {
	ID PawnID
	// Current is the pawn's hostility response; unknown where the read
	// did not carry it (no configurable response), which is never written.
	Current         domain.Fact[domain.HostilityResponse]
	ViolenceCapable domain.Fact[bool]
	Age             domain.Fact[float64]
	BloodLoss       domain.Fact[float64]
	Health          domain.Fact[float64]
	Job             domain.Fact[PawnJob]
}

// DefaultHostility is the pawn's standing response: Flee for a pawn that
// cannot or should not fight, Attack otherwise. Unknown when a deciding
// fact is unknown.
func DefaultHostility(p HostilityPawn) (domain.HostilityResponse, bool) {
	violent, vk := p.ViolenceCapable.Value()
	if vk && !violent {
		return domain.HostilityFlee, true
	}
	if age, ok := p.Age.Value(); ok && age < ChildAge {
		return domain.HostilityFlee, true
	}
	if loss, ok := p.BloodLoss.Value(); ok && loss >= SeriousBloodLoss {
		return domain.HostilityFlee, true
	}
	if health, ok := p.Health.Value(); ok && health < SeriousInjuryHealth {
		return domain.HostilityFlee, true
	}
	_, ak := p.Age.Value()
	_, hk := p.Health.Value()
	if !vk || !ak || !hk {
		return "", false
	}
	return domain.HostilityAttack, true
}

// StealthJob reports a work-giver job whose target cell stands within
// StealthCells of a known-passive hostile with no engaging hostile within
// that reach. A job no work giver issued (a forced order, rest, a meal),
// an unknown target or an unplaced hostile is never stealth work.
func StealthJob(job domain.Fact[PawnJob], threats []EmergencyThreat) bool {
	j, ok := job.Value()
	if !ok || j.Def == "" || j.Work == "" {
		return false
	}
	target, tk := j.Target.Value()
	if !tk {
		return false
	}
	cell, ck := target.Cell.Value()
	if !ck {
		return false
	}
	passive := false
	for _, t := range threats {
		if dead, known := t.Dead.Value(); known && dead {
			continue
		}
		near, placed := threatNear(t, cell)
		if !placed {
			if t.Engaging() && !t.DistantThreat() {
				return false
			}
			continue
		}
		if !near {
			continue
		}
		if t.Engaging() {
			return false
		}
		passive = true
	}
	return passive
}

// threatNear reports whether a threat stands within StealthCells of cell,
// and whether its place is known at all.
func threatNear(t EmergencyThreat, cell domain.Cell) (near, placed bool) {
	cells := t.Cells
	if p, ok := t.Position.Value(); ok {
		cells = append([]domain.Cell{p}, cells...)
	}
	for _, c := range cells {
		if chebyshev(c, cell) <= StealthCells {
			return true, true
		}
	}
	return false, len(cells) > 0
}

// WantHostility is the response the pawn should hold now: Ignore on
// stealth work for a pawn that would otherwise Attack, else its default.
// A Flee pawn keeps fleeing beside sleeping hostiles.
func WantHostility(p HostilityPawn, threats []EmergencyThreat) (domain.HostilityResponse, bool) {
	mode, ok := DefaultHostility(p)
	if !ok {
		return "", false
	}
	if mode == domain.HostilityAttack && StealthJob(p.Job, threats) {
		return domain.HostilityIgnore, true
	}
	return mode, true
}

// HostilityChanges are the settings to write: every pawn whose known
// response differs from the one it should hold.
func HostilityChanges(pawns []HostilityPawn, threats []EmergencyThreat) []domain.PawnSettings {
	var out []domain.PawnSettings
	for _, p := range pawns {
		current, ck := p.Current.Value()
		want, ok := WantHostility(p, threats)
		if !ck || !ok || current == want {
			continue
		}
		if s, err := domain.NewHostilitySetting(domain.PawnID(p.ID), want); err == nil {
			out = append(out, s)
		}
	}
	return out
}

// HostilityOwed is the review's HostilityOwed fact: a change is owed.
// Unknown pawns owe nothing.
func HostilityOwed(pawns domain.Fact[[]HostilityPawn], threats []EmergencyThreat) domain.Fact[bool] {
	rows, known := pawns.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(HostilityChanges(rows, threats)) > 0)
}
