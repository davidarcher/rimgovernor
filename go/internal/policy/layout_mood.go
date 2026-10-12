package policy

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Mood currency (#2954). The planner trades mood against work and watts, so a
// mood point needs a price. The user's decision (epic #2855): a mood point is
// priced as break risk. One point is the change in a colonist's expected break
// cost a day when their expected mood moves by one point, and a break costs the
// work the colonist loses in it, in the work-days of the #2945 rule (a colonist's
// work-day is worth their food upkeep, turned into watts at WorkDayW).
//
// What the game says (ilspycmd, Verse.AI.MentalBreaker, MentalState,
// MentalBreakWorker, RimWorld.Need_Mood; research on #2954):
//   - Need_Mood.CurInstantLevel = base 0.5 + the thoughts' offsets / 100, so one
//     mood point is 0.01 of the level.
//   - The thresholds are the MentalBreakThreshold stat T (default 0.35): minor
//     below T, major below T x 0.5714286, extreme below T / 7.
//   - A pawn below a threshold for 2000 ticks may break; every 150 ticks the
//     highest tier it is in fires with an MTB of 4 (minor), 0.8 (major) or 0.5
//     (extreme) days. After a break it recovers (MentalState) and may not break
//     again for 15000 awake ticks (Notify_RecoveredFromMentalState).
//   - The break is drawn among the tier's MentalBreakDefs by baseCommonality, and
//     lasts its MentalStateDef's minTicksBeforeRecovery, then ends with
//     recoveryMtbDays or at maxTicksBeforeRecovery (a Catatonic break is a hediff
//     that disappears after a range of ticks).
//
// So with the break a renewal cycle of MTB + cooldown + duration days, a tier
// costs duration/cycle work-days a day: the share of time the colonist is in a
// break. The mood price is the difference of that cost between the mood with and
// without the effect, a step function that is zero while the loss stays inside
// a tier and jumps where it crosses a threshold.
//
// What it leaves out, so the price is a lower bound on the harm: the damage and
// risk of aggro breaks (berserk, tantrum, fire), the breaks that remove the
// pawn (RunWild, GiveUpExit: the game gives no duration, MoodGameTable
// Unpriced), the per-colony-size commonality curves, the sleeping hours in which
// the MTB roll fires but cannot start a break, and trait changes to the
// threshold (the caller passes the pawn's threshold only through the model).

// MoodTier is a break intensity, ordered mildest first.
type MoodTier int

// The break tiers, mildest first.
const (
	TierMinor MoodTier = iota
	TierMajor
	TierExtreme
	moodTiers
)

// MoodGameTable is the game's mood-break numbers that its defs do not carry:
// the literals of MentalBreaker and MentalState, plus the defs they name.
// Fractions are each tier's share of the MentalBreakThreshold stat
// (MajorBreakMoodFraction, ExtremeBreakMoodFraction), MTBDays the tier's
// break MTB (MinorBreakMTBDays ...), CooldownDays the ticks without a break
// after a recovery (MinTicksSinceRecoveryToBreak), PointsPerLevel Need_Mood's
// divisor. Unpriced are the breaks the game gives no duration for: RunWild turns
// the pawn into a wild man and GiveUpExit walks it off the map. HediffBreaks are
// the breaks that are a hediff (Catatonic) and the hediff whose disappear time
// is the break's length. Registered in DefTables.
type MoodGameTable struct {
	ThresholdStat  string
	Fractions      [moodTiers]float64
	MTBDays        [moodTiers]float64
	CooldownDays   float64
	PointsPerLevel float64
	Unpriced       []string
	HediffBreaks   map[string]string
	DarkThought    string
	RoleThoughts   map[RoomRole]string
}

var moodGame = MoodGameTable{
	ThresholdStat:  "MentalBreakThreshold",
	Fractions:      [moodTiers]float64{TierMinor: 1, TierMajor: 0.5714286, TierExtreme: 1.0 / 7},
	MTBDays:        [moodTiers]float64{TierMinor: 4, TierMajor: 0.8, TierExtreme: 0.5},
	CooldownDays:   15000.0 / domain.TicksPerDay,
	PointsPerLevel: 100,
	Unpriced:       []string{"RunWild", "GiveUpExit"},
	HediffBreaks:   map[string]string{"Catatonic": "CatatonicBreakdown"},
	DarkThought:    "EnvironmentDark",
	// The thought each role's mood effect is: the stage is the room's
	// impressiveness stage. Throne and worship carry no mood offset.
	RoleThoughts: map[RoomRole]string{
		RoomRoleBedroom: "SleptInBedroom", RoomRoleBarracks: "SleptInBarracks",
		RoomRoleHospital:       "HospitalPatientRoomStats",
		RoomRolePrisonCell:     "PrisonCell",
		RoomRolePrisonBarracks: "PrisonBarracks",
	},
}

// MoodGame is the table the bridge reads the break model with.
func MoodGame() MoodGameTable { return moodGame }

// StateDays is the mean days a mental state lasts: the game ends it at
// minTicks plus an exponential wait of mean mtbDays (rolled every 30 ticks) and
// forces the end at maxTicks, so the mean of that truncated wait.
func StateDays(minTicks, maxTicks, mtbDays float64) float64 {
	min := minTicks / domain.TicksPerDay
	if mtbDays <= 0 {
		return min
	}
	span := max(maxTicks-minTicks, 0) / domain.TicksPerDay
	return min + mtbDays*(1-math.Exp(-span/mtbDays))
}

// MoodTierModel is one tier's break mix: the sum of its priced breaks'
// commonalities and their weighted mean duration in days. Weight 0 is a tier
// with no priced break.
type MoodTierModel struct{ Weight, Days float64 }

// MoodBreakModel is the break cost curve of a colonist: the break threshold and
// each tier's mix. The zero value is unknown.
type MoodBreakModel struct {
	Threshold float64
	Tiers     [moodTiers]MoodTierModel
}

// Valid reports a model with a threshold and a priced break in every tier.
func (m MoodBreakModel) Valid() bool {
	if !finitePositive(m.Threshold) {
		return false
	}
	for _, t := range m.Tiers {
		if !finitePositive(t.Weight) || !finitePositive(t.Days) {
			return false
		}
	}
	return true
}

// tierAt is the tier a mood level is in; false above the minor threshold.
func (m MoodBreakModel) tierAt(level float64) (MoodTier, bool) {
	for tier := TierExtreme; tier >= TierMinor; tier-- {
		if level < m.Threshold*moodGame.Fractions[tier] {
			return tier, true
		}
	}
	return 0, false
}

// CostPerDay is the work-days a colonist at the mood level loses a day to
// breaks: the share of a break cycle spent in the break.
func (m MoodBreakModel) CostPerDay(level float64) float64 {
	tier, ok := m.tierAt(level)
	if !ok {
		return 0
	}
	days := m.Tiers[tier].Days
	return days / (moodGame.MTBDays[tier] + moodGame.CooldownDays + days)
}

// MoodStage is one stage of a ThoughtDef; a null stage (the game keeps unused
// ones null) is not Present and carries no mood.
type MoodStage struct {
	Effect  float64
	Present bool
}

// MoodModel is everything the mood price reads from the catalog: the break
// curve and the stages of the thoughts the planner prices.
type MoodModel struct {
	Break  MoodBreakModel
	Stages map[string][]MoodStage
}

// StageEffect is the mood a thought gives at a stage index, clamped to the
// stages it has; a null stage gives none. False when the thought is unknown.
func (m MoodModel) StageEffect(thought string, stage int) (float64, bool) {
	stages := m.Stages[thought]
	if len(stages) == 0 {
		return 0, false
	}
	s := stages[min(max(stage, 0), len(stages)-1)]
	if !s.Present {
		return 0, true
	}
	return s.Effect, true
}

// DarkPoints is the mood points a colonist loses to darkness (EnvironmentDark's
// stage), 0 and false when unknown. Traits that nullify it (NightOwl,
// Undergrounder) are the caller's.
func (m MoodModel) DarkPoints() (float64, bool) {
	effect, ok := m.StageEffect(moodGame.DarkThought, 0)
	return -effect, ok
}

// LostPoints is the mood points a lost role effect costs a pawn in a room of the
// given impressiveness stage: what the role it was meant to have gives, less
// what the role it got gives. Throne and worship carry no mood. False when a
// role's thought is unknown.
func (m MoodModel) LostPoints(lost LostRoleEffect, stage int) (float64, bool) {
	switch lost.Effect {
	case EffectThrone, EffectWorship:
		return 0, true
	case EffectSleepMood, EffectPatientMood, EffectPrisonMood:
	default:
		return 0, false
	}
	had, ok := m.roleEffect(lost.Role, stage)
	if !ok {
		return 0, false
	}
	got, ok := m.roleEffect(lost.Got, stage)
	if !ok {
		return 0, false
	}
	return had - got, true
}

// roleEffect is a role's mood at a stage; a role with no mood thought gives 0.
func (m MoodModel) roleEffect(role RoomRole, stage int) (float64, bool) {
	thought, ok := moodGame.RoleThoughts[role]
	if !ok {
		return 0, true
	}
	return m.StageEffect(thought, stage)
}

// MoodSource is a way to buy mood instead of paying for its loss, priced in
// sustained watts for the whole group of pawns it serves (a bedroom
// impressiveness upgrade is one).
type MoodSource struct {
	Points, W float64
}

// MoodPriceInput is the mood price's input. Points is the mood a change takes
// from each of Pawns colonists (negative when it gives mood) and ExpectedMood
// their mood level with the effect, 0 to 1. WorkDayW is a colonist's work-day in
// watts (LightPrice.WorkDayW); Sources are the other ways to buy the same mood.
type MoodPriceInput struct {
	Model        MoodBreakModel
	WorkDayW     float64
	Points       float64
	Pawns        int
	ExpectedMood float64
	Sources      []MoodSource
}

// MoodPrice is a mood change in work-days a day and sustained watts. Capped is
// set when a source of the same mood is cheaper than the break risk.
type MoodPrice struct {
	WorkDays, W float64
	Capped      bool
}

// PriceMood prices a change of mood points as break risk. It is false for an
// unknown model or work-day or a mood outside 0 to 1. The price is never more
// than the cheapest source of the same mood: a loss it is cheaper to buy back
// is priced at the buying.
func PriceMood(in MoodPriceInput) (MoodPrice, bool) {
	level := in.ExpectedMood
	if !in.Model.Valid() || !finitePositive(in.WorkDayW) || in.Pawns < 0 || math.IsNaN(in.Points) || math.IsInf(in.Points, 0) ||
		math.IsNaN(level) || level < 0 || level > 1 {
		return MoodPrice{}, false
	}
	after := min(max(level-in.Points/moodGame.PointsPerLevel, 0), 1)
	days := float64(in.Pawns) * (in.Model.CostPerDay(after) - in.Model.CostPerDay(level))
	price := MoodPrice{WorkDays: days, W: days * in.WorkDayW}
	if in.Points > 0 {
		cheapest := math.Inf(1)
		for _, s := range in.Sources {
			if s.Points >= in.Points && finitePositive(s.W) {
				cheapest = min(cheapest, s.W)
			}
		}
		if price.W > cheapest {
			price.W, price.WorkDays, price.Capped = cheapest, cheapest/in.WorkDayW, true
		}
	}
	return price, true
}

// Add folds one break into the tier's mix (the bridge builds the model).
func (t MoodTierModel) Add(weight, days float64) MoodTierModel {
	if !finitePositive(weight) || !finitePositive(days) {
		return t
	}
	total := t.Weight + weight
	return MoodTierModel{Weight: total, Days: (t.Days*t.Weight + days*weight) / total}
}

// UnpricedBreak reports a break the game gives no duration for.
func UnpricedBreak(def string) bool { return slices.Contains(moodGame.Unpriced, def) }

func moodThoughtDefs() []string {
	out := []string{moodGame.DarkThought}
	for _, thought := range moodGame.RoleThoughts {
		out = append(out, thought)
	}
	slices.Sort(out)
	return out
}

func moodBreakHediffs() []string {
	var out []string
	for _, hediff := range moodGame.HediffBreaks {
		out = append(out, hediff)
	}
	slices.Sort(out)
	return out
}
