package policy

// Hazard thresholds Go authors into every clock WatchPolicy (and the hunt
// route margin into the colony facts request and native rules). Native holds no
// literal for them: it validates presence and finiteness and applies what it
// is sent. docs/developers/architecture/hazard-detection-bounds.md describes
// each.
const (
	// SeriousSingleHitDamage: one blow of at least this much is a serious
	// injury, above every common automatic round (charge rifle 15, heavy SMG
	// 12, assault rifle 11) but under a pila (26) or sniper round (25).
	SeriousSingleHitDamage float32 = 20
	// SeriousSummaryHealthFloor: summary health crossing under this is
	// serious. MedicalRestSafety uses the same value: a resting patient at or
	// under it needs a fresh medical review.
	SeriousSummaryHealthFloor float32 = 0.5
	// SeriousBleedRateFloor: a total bleed rate (fraction of blood per day)
	// crossing over this is serious.
	SeriousBleedRateFloor float32 = 1.0
	// SeriousVitalPartFloor: a vital part hit to under this fraction of its
	// health is serious whatever the damage was.
	SeriousVitalPartFloor float32 = 0.5
	// ExplosiveNearMarginCells: an explosive landing within its blast radius
	// plus this many cells of a colonist is launched near them.
	ExplosiveNearMarginCells float32 = 3
	// MeleeReachCells: the range assumed for a pawn without a ranged weapon.
	MeleeReachCells float32 = 1.5
	// InjurySeverityFloorTicks: a new wound that bleeds the colonist out
	// within this many game ticks (two in-game hours) keeps the stop.
	InjurySeverityFloorTicks uint32 = 5000
	// HuntPredatorMarginCells: a hunt route passing within this many cells
	// (Chebyshev) of a wild predator is unsafe.
	HuntPredatorMarginCells float32 = 25
)
