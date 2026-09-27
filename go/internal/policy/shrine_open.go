package policy

import (
	"math"
	"sort"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ShrinePolicy is the review's casket stance (#460, #875): Opening maps a
// shrine to CasketOpen when ShrineOpenReadiness held at the review, or to the
// readiness hold reason. A shrine it does not name, the zero value, leaves
// every filled casket sealed (#459); the goal then finishes with the caskets
// sealed and re-arms the next review the gate holds.
type ShrinePolicy struct {
	Opening map[string]string
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
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PreviewTick < r.MinimumTick {
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

// Opening readiness holds (#875): each names the first unmet condition of
// ShrineOpenReadiness; the caskets stay sealed and the goal re-arms once the
// gate holds.
const (
	CasketHoldNoBackup      = "open_no_ranged_backup"
	CasketHoldLockInjured   = "open_lock_injured"
	CasketHoldNoCustody     = "open_no_prisoner_bed"
	CasketHoldNoMedicine    = "open_no_medicine"
	CasketHoldNoDoctor      = "open_no_doctor"
	CasketHoldEmergency     = "open_emergency"
	CasketHoldCombat        = "open_combat_active"
	CasketHoldThreatUnknown = "open_threat_unknown"
	CasketHoldThreatTooHigh = "open_threat_too_high"

	shrineLockHealthMinimum = 0.8
)

// ShrineOpenRequest is what ShrineOpenReadiness judges one shrine's filled
// caskets from. CustodyRoom is JoinerCapacity's reading; Medicine the usable
// medicine count; Doctors the colonists capable of Doctor work.
type ShrineOpenRequest struct {
	Caskets           []ShrineCasket
	Squad             []ShrineDefenderFacts
	CustodyRoom       domain.Fact[bool]
	Medicine          domain.Fact[int64]
	Doctors           domain.Fact[int]
	Emergency, Combat bool
	RaidPoints        domain.Fact[float64]
}

// ShrineOpenReadiness is the gate that replaced the operator's opening flag
// (#875): CasketOpen only when a melee lock of healthy colonists covers every
// filled casket with one armed ranged colonist besides, custody has a bed for
// a captive, there is a medicine per casket and a doctor, nothing is on fire,
// and raid points sit under the breach ceiling for a squad of that size.
// Otherwise the first unmet condition's hold reason.
func ShrineOpenReadiness(r ShrineOpenRequest) string {
	lock := ShrineMeleeLock(r.Caskets, r.Squad)
	if lock.Reason != "" {
		return lock.Reason
	}
	lockers := map[domain.PawnID]bool{}
	for _, pawn := range lock.Lockers {
		lockers[pawn] = true
	}
	backup := false
	for _, d := range r.Squad {
		if lockers[d.ID] {
			if health, known := d.HealthFraction.Value(); !known || health < shrineLockHealthMinimum {
				return CasketHoldLockInjured
			}
			continue
		}
		backup = backup || shrineDefenderEligible(d) && shrineRanged(d)
	}
	if !backup {
		return CasketHoldNoBackup
	}
	if room, known := r.CustodyRoom.Value(); !known || !room {
		return CasketHoldNoCustody
	}
	if medicine, known := r.Medicine.Value(); !known || medicine < int64(len(r.Caskets)) {
		return CasketHoldNoMedicine
	}
	if doctors, known := r.Doctors.Value(); !known || doctors == 0 {
		return CasketHoldNoDoctor
	}
	if r.Emergency {
		return CasketHoldEmergency
	}
	if r.Combat {
		return CasketHoldCombat
	}
	points, known := r.RaidPoints.Value()
	if !known || math.IsNaN(points) || math.IsInf(points, 0) {
		return CasketHoldThreatUnknown
	}
	if points > shrineSquadCeiling(len(lock.Lockers)+1) {
		return CasketHoldThreatTooHigh
	}
	return CasketOpen
}
