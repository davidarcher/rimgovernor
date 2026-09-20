package policy

import (
	"sort"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ShrinePolicy is the operator's casket stance (#460). OpenCaskets off, the
// default, leaves every filled casket sealed (#459); on, ClearAncientShrine
// opens them under a melee lock once the shrine is open and guard-free. It
// belongs on once the colony can hold prisoners: the ancients inside wake
// hostile, and a downed one is worth capturing (very low recruitment
// resistance), which needs a prisoner bed.
type ShrinePolicy struct {
	OpenCaskets bool
	// HeatFallback permits heaters and a ranged opening only when the melee
	// lock cannot be staffed. The zero value preserves the melee strategy.
	HeatFallback bool
}

// Casket decisions and holds under the opening policy (#460).
const (
	// CasketOpen is a filled casket the policy opens.
	CasketOpen = "open"
	// CasketHoldLockUnderstaffed holds a shrine whose filled caskets outnumber
	// the violence-capable melee colonists free to stand in front of them.
	CasketHoldLockUnderstaffed = "lock_understaffed"

	// Occupant decisions: what the colony does with each humanlike the
	// caskets released.
	OccupantCapture  = "capture"
	OccupantRelease  = "release"
	OccupantBury     = "bury"
	OccupantFight    = "fight"
	OccupantCaptured = "captured"
)

// ShrineOccupant is one humanlike the caskets released, or its corpse, as
// the census reads it. Megascarabs are never listed: they are never hostile
// and the colony ignores them.
type ShrineOccupant struct {
	EntityID                        string
	Hostile, Downed, Dead, Prisoner bool
	Faction                         string
}

// OccupantDecision decides one released occupant. A corpse is buried
// (MaintainWaste's work); a standing hostile is fought (ActiveCombat's); a
// downed hostile and a standing neutral are captured while custody has
// room, released otherwise; a colony prisoner is done. custodyRoom is
// JoinerCapacity's reading: whether the colony can host one more.
func OccupantDecision(occupant ShrineOccupant, custodyRoom bool) string {
	switch {
	case occupant.Dead:
		return OccupantBury
	case occupant.Prisoner:
		return OccupantCaptured
	case occupant.Hostile && !occupant.Downed:
		return OccupantFight
	case custodyRoom:
		return OccupantCapture
	}
	return OccupantRelease
}

// ShrineOpenTargets are the filled caskets the goal owes an opening on
// (#460): those of an open, guard-free shrine touching Home, in casket
// identity order per shrine, and only under a policy that opens caskets.
func ShrineOpenTargets(rows []AncientShrine, shrine ShrinePolicy) map[string][]ShrineCasket {
	out := map[string][]ShrineCasket{}
	if !shrine.OpenCaskets {
		return out
	}
	for _, row := range rows {
		if !row.InHome || row.Sealed || !row.GuardsKnown || row.GuardsAlive() {
			continue
		}
		var caskets []ShrineCasket
		for _, casket := range row.Caskets {
			if CasketDecisionUnder(casket, row, shrine) == CasketOpen {
				caskets = append(caskets, casket)
			}
		}
		if len(caskets) == 0 {
			continue
		}
		sort.Slice(caskets, func(i, j int) bool { return caskets[i].EntityID < caskets[j].EntityID })
		out[row.ID] = caskets
	}
	return out
}

// ShrineLock is the melee lock's staffing: one violence-capable melee
// colonist per filled casket (Lockers, by casket identity), the casket the
// Opener opens (opening one casket opens every casket of the group), or the
// hold reason when the squad cannot cover every casket.
type ShrineLock struct {
	Lockers map[string]domain.PawnID
	Opener  domain.PawnID
	Casket  string
	Reason  string
}

// ShrineMeleeLock staffs the lock. Eligible colonists are the squad
// defenders SelectSquadDefense would draft whose primary is not a ranged
// weapon (a melee weapon or fists: MeleeEquipped is equipment known); the
// healthiest stand first, the opener is the locker of the lowest casket.
// One colonist is never left free here: the opener is a locker, and the
// ancients wake with cryptosleep sickness, so the fight is the lock itself.
func ShrineMeleeLock(caskets []ShrineCasket, squad []ShrineDefenderFacts) ShrineLock {
	out := ShrineLock{Lockers: map[string]domain.PawnID{}}
	if len(caskets) == 0 {
		out.Reason = CasketHoldLockUnderstaffed
		return out
	}
	var pool []ShrineDefenderFacts
	for _, d := range squad {
		melee, ok := d.MeleeEquipped.Value()
		ranged, rk := d.RangedEquipped.Value()
		if shrineDefenderEligible(d) && ok && melee && rk && !ranged {
			pool = append(pool, d)
		}
	}
	if len(pool) < len(caskets) {
		out.Reason = CasketHoldLockUnderstaffed
		return out
	}
	sort.Slice(pool, func(i, j int) bool {
		a, _ := pool[i].HealthFraction.Value()
		b, _ := pool[j].HealthFraction.Value()
		if a != b {
			return a > b
		}
		return pool[i].ID < pool[j].ID
	})
	sorted := append([]ShrineCasket(nil), caskets...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].EntityID < sorted[j].EntityID })
	for i, casket := range sorted {
		out.Lockers[casket.EntityID] = pool[i].ID
	}
	out.Opener, out.Casket = pool[0].ID, sorted[0].EntityID
	return out
}

// OpenerUnavailable mirrors RepairerUnavailable: the opener is dead, downed,
// in a mental state or already running the Open job.
const OpenerUnavailable Reason = "opener_unavailable"

// OpenCasketPawnFacts describes the already-selected opener. Drafted is
// read but not refused: the melee lock drafts the opener first.
type OpenCasketPawnFacts struct {
	Pawn                  domain.PawnID
	SnapshotToken         string
	Dead, Downed, Drafted domain.Fact[bool]
	MentalState           domain.Fact[bool]
	ExistingJobDef        domain.Fact[string]
}

// OpenCasketFacts is the inspection EvaluateOpenCasket judges: the casket
// must still exist and hold something (an opened casket is done, not
// reopened).
type OpenCasketFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Pawn                  OpenCasketPawnFacts
	Casket                string
	CasketSnapshotToken   string
	Exists, HasContents   domain.Fact[bool]
	NativeCanTry          domain.Fact[bool]
}

type OpenCasketRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       OpenCasketFacts
}

// EvaluateOpenCasket re-validates one opener/casket pair immediately before
// dispatch, the way EvaluateRepair does; admission proves eligibility now,
// not that the Open job completes.
func EvaluateOpenCasket(r OpenCasketRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	open, ok := r.Action.OpenCasket()
	canonical, err := domain.NewOpenCasketAction(r.Action.ID(), open)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PreviewTick < r.MinimumTick || !f.PreviewTick.FreshFor(f.PawnTick) {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.PreviewTick < v.Tick || (v.Stage == domain.Prepared && !sameWorld(v.Snapshot, r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	if f.Pawn.Pawn != open.Pawn() || f.Casket != open.Casket() || !validToken(f.Pawn.SnapshotToken) || !validToken(f.CasketSnapshotToken) {
		return refuse(UnknownFacts)
	}
	for _, fact := range []domain.Fact[bool]{f.Pawn.Dead, f.Pawn.Downed, f.Pawn.Drafted, f.Pawn.MentalState, f.Exists, f.HasContents, f.NativeCanTry} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	existingJob, known := f.Pawn.ExistingJobDef.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	dead, _ := f.Pawn.Dead.Value()
	downed, _ := f.Pawn.Downed.Value()
	mental, _ := f.Pawn.MentalState.Value()
	if dead || downed || existingJob == open.JobDef() {
		return refuse(OpenerUnavailable)
	}
	if mental {
		return refuse(PlayerOrder)
	}
	if exists, _ := f.Exists.Value(); !exists {
		return refuse(StructureIneligible)
	}
	if filled, _ := f.HasContents.Value(); !filled {
		return refuse(StructureIneligible)
	}
	if eligible, _ := f.NativeCanTry.Value(); !eligible {
		return refuse(NativeIneligible)
	}
	return DraftDecision{Admitted: true}
}
