package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Self-tend. Vanilla's WorkGiver_TendSelf is Doctor
// work gated on playerSettings.selfTend; it tends the pawn where it stands,
// while other doctors only tend a humanlike patient in bed
// (WorkGiver_Tend.GoodLayingStatusForTend), so an up-and-about pawn with
// self-tend on treats itself before any doctor can. TendUtility multiplies
// a self-tend's quality by SelfTendQualityFactor. The bot turns self-tend on
// when the pawn's own tend quality times that factor is at least the best
// other available doctor's, or no other doctor is available; off
// otherwise. Doctor priority itself belongs to the work planner.

// SelfTendQualityFactor is vanilla TendUtility.SelfTendQualityFactor.
const SelfTendQualityFactor = 0.7

// SelfTendPawn is one colonist's self-tend inputs. TendQuality is the
// MedicalTendQuality stat, unknown for a pawn that cannot doctor.
type SelfTendPawn struct {
	ID          PawnID
	Current     domain.Fact[bool]
	TendQuality domain.Fact[float64]
	Available   domain.Fact[bool]
	// Read: the row carried its policy inputs, so an unknown TendQuality
	// means the pawn cannot doctor. Guest is a guest or prisoner, never a
	// colony doctor.
	Read, Guest bool
}

// SelfTendPawn lifts a work census row into its self-tend inputs.
func (w WorkPawn) SelfTendPawn() SelfTendPawn {
	p := SelfTendPawn{ID: w.ID, Current: w.SelfTend, Available: w.Available}
	if in, ok := w.PolicyInputs.Value(); ok {
		p.Read = true
		p.TendQuality = in.TendQuality
		p.Guest = in.GuestStatus == "Guest" || in.GuestStatus == "Prisoner"
	}
	return p
}

// WantSelfTend is the self-tend setting pawns[i] should hold; false when a
// deciding fact is unknown (its own quality, another colonist's policy
// inputs, or a doctor's availability). A pawn that cannot doctor has no
// setting to decide.
func WantSelfTend(pawns []SelfTendPawn, i int) (bool, bool) {
	own, ok := pawns[i].TendQuality.Value()
	if !ok || pawns[i].Guest {
		return false, false
	}
	own *= SelfTendQualityFactor
	for j, p := range pawns {
		if j == i || p.Guest {
			continue
		}
		if !p.Read {
			return false, false
		}
		quality, doctor := p.TendQuality.Value()
		if !doctor {
			continue
		}
		available, ak := p.Available.Value()
		if !ak {
			return false, false
		}
		if available && quality > own {
			return false, true
		}
	}
	return true, true
}

// SelfTendChanges are the self-tend settings to write: every colonist whose
// known setting differs from the one it should hold.
func SelfTendChanges(pawns []SelfTendPawn) []domain.PawnSettings {
	var out []domain.PawnSettings
	for i, p := range pawns {
		current, ck := p.Current.Value()
		want, ok := WantSelfTend(pawns, i)
		if !ck || !ok || current == want {
			continue
		}
		if s, err := domain.NewSelfTendSetting(domain.PawnID(p.ID), want); err == nil {
			out = append(out, s)
		}
	}
	return out
}

// SelfTendOwed is the review's SelfTendOwed fact: a change is owed.
func SelfTendOwed(pawns domain.Fact[[]SelfTendPawn]) domain.Fact[bool] {
	rows, known := pawns.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(SelfTendChanges(rows)) > 0)
}
