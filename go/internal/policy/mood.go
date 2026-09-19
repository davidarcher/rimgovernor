package policy

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"math"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type MoodNeed string

const (
	MoodFood MoodNeed = "food"
	MoodRest MoodNeed = "rest"
	MoodJoy  MoodNeed = "joy"
)

type MoodPawn struct {
	ID                                          PawnID
	Mood, Threshold, Target                     domain.Fact[float64]
	Food, Rest, Joy                             domain.Fact[float64]
	Mental, Dead, Downed, Drafted, PlayerForced domain.Fact[bool]
	// Thoughts are the pawn's observed negative thought rows; unknown when
	// the native social block was unreadable.
	Thoughts domain.Fact[[]MoodThought]
}

type MoodCause struct {
	Need  MoodNeed
	Level domain.Fact[float64]
}

type MoodState struct {
	Pawn                        MoodPawn
	Active, Missing, MentalRisk bool
	Causes                      []MoodCause
	// Provision names the upkeep goals whose facilities would remove the
	// pawn's dominant environment thought pressure (moodProvisioning); empty
	// when need relief is the only measured response.
	Provision []MoodProvision
}

type MoodHistory struct{ States []MoodState }

// Ordinary native IDs retain the shared semantic name. Oversized mod IDs use a
// separate hashed namespace so the enclosing journal goal remains bounded.
func MoodGoal(id PawnID) GoalID {
	if len(id) <= 210 {
		return GoalID("EnsureMood-" + string(id))
	}
	digest := sha256.Sum256([]byte(id))
	return GoalID(fmt.Sprintf("EnsureMoodHash-%x", digest[:16]))
}

func IsMoodGoal(id GoalID) bool {
	if s := strings.TrimPrefix(string(id), "EnsureMood-"); s != string(id) {
		return len(s) <= 210 && foodID(s)
	}
	s := strings.TrimPrefix(string(id), "EnsureMoodHash-")
	if s == string(id) || len(s) != 32 || strings.ToLower(s) != s {
		return false
	}
	_, err := hex.DecodeString(s)
	return err == nil
}

func moodNumber(f domain.Fact[float64]) bool {
	v, known := f.Value()
	return !known || !math.IsNaN(v) && !math.IsInf(v, 0)
}

func (p MoodPawn) Validate() error {
	if !foodID(string(p.ID)) {
		return errors.New("invalid mood pawn")
	}
	for _, f := range []domain.Fact[float64]{p.Mood, p.Threshold, p.Target, p.Food, p.Rest, p.Joy} {
		if !moodNumber(f) {
			return errors.New("nonfinite mood evidence")
		}
	}
	return validateMoodThoughts(p.Thoughts)
}

func (h MoodHistory) Validate() error {
	if len(h.States) > 256 {
		return errors.New("mood history exceeds bound")
	}
	seen := map[PawnID]bool{}
	for _, s := range h.States {
		if err := s.Pawn.Validate(); err != nil {
			return err
		}
		if seen[s.Pawn.ID] || len(s.Causes) > 3 {
			return errors.New("invalid mood history")
		}
		if err := validateMoodProvision(s.Provision); err != nil {
			return err
		}
		seen[s.Pawn.ID] = true
		m, mk := s.Pawn.Mood.Value()
		threshold, tk := s.Pawn.Threshold.Value()
		mental, nk := s.Pawn.Mental.Value()
		target, pk := s.Pawn.Target.Value()
		if !s.Active && (s.Missing || s.MentalRisk || nk && mental || mk && tk && (m <= threshold || pk && target < m && target <= threshold)) {
			return errors.New("observed mood risk marked inactive")
		}
		causes := map[MoodNeed]bool{}
		for _, c := range s.Causes {
			if c.Need != MoodFood && c.Need != MoodRest && c.Need != MoodJoy || causes[c.Need] || !moodNumber(c.Level) {
				return errors.New("invalid mood cause")
			}
			if level, known := c.Level.Value(); known && level >= .5 {
				return errors.New("recovered mood cause retained")
			}
			causes[c.Need] = true
		}
	}
	return nil
}

// ReviewMood ports native-threshold and need hysteresis. Thought pressure is a
// present observation, never a prediction of a break or future mood benefit.
func ReviewMood(observed domain.Fact[[]MoodPawn], previous MoodHistory) (MoodHistory, error) {
	if err := previous.Validate(); err != nil {
		return MoodHistory{}, err
	}
	old := map[PawnID]MoodState{}
	for _, s := range previous.States {
		old[s.Pawn.ID] = s
	}
	rows, known := observed.Value()
	if len(rows) > 256 {
		return MoodHistory{}, errors.New("mood census exceeds bound")
	}
	result := MoodHistory{}
	seen := map[PawnID]bool{}
	if known {
		for _, p := range rows {
			if err := p.Validate(); err != nil {
				return MoodHistory{}, err
			}
			if seen[p.ID] {
				return MoodHistory{}, errors.New("duplicate mood pawn")
			}
			seen[p.ID] = true
			prior := old[p.ID]
			s := MoodState{Pawn: p}
			m, mk := p.Mood.Value()
			threshold, tk := p.Threshold.Value()
			mental, mentalKnown := p.Mental.Value()
			s.MentalRisk = prior.MentalRisk
			if mentalKnown {
				s.MentalRisk = mental
			}
			s.Active = prior.Active
			if mk && tk {
				margin := 0.0
				if prior.Active {
					margin = .05
				}
				if mentalKnown {
					s.Active = mental || m <= threshold+margin
				} else if m <= threshold+margin {
					s.Active = true
				}
			}
			if mentalKnown && mental {
				s.Active = true
			}
			s.Active = s.Active || s.MentalRisk
			if target, k := p.Target.Value(); mk && tk && k && target < m && target <= threshold {
				s.Active = true
			}
			for _, need := range []struct {
				name  MoodNeed
				level domain.Fact[float64]
			}{{MoodFood, p.Food}, {MoodRest, p.Rest}, {MoodJoy, p.Joy}} {
				retained := false
				for _, c := range prior.Causes {
					retained = retained || c.Need == need.name
				}
				limit := .3
				if retained {
					limit = .5
				}
				level, k := need.level.Value()
				if k && level < limit || !k && retained {
					s.Causes = append(s.Causes, MoodCause{need.name, need.level})
				}
			}
			sort.Slice(s.Causes, func(i, j int) bool {
				a, ak := s.Causes[i].Level.Value()
				b, bk := s.Causes[j].Level.Value()
				if ak != bk {
					return ak
				}
				if ak && a != b {
					return a < b
				}
				return s.Causes[i].Need < s.Causes[j].Need
			})
			s.Active = s.Active || prior.Active && len(s.Causes) > 0
			s.Provision = moodProvisioning(p.Thoughts)
			if _, k := p.Thoughts.Value(); !k {
				s.Provision = append([]MoodProvision(nil), prior.Provision...)
			}
			// Death or unreadable availability cannot certify an active need recovered.
			if dead, k := p.Dead.Value(); prior.Active && (!k || dead) {
				s.Active = true
			}
			if s.Active || prior.Pawn.ID != "" {
				result.States = append(result.States, s)
			}
		}
	}
	for _, prior := range previous.States {
		if !seen[prior.Pawn.ID] && prior.Active {
			prior.Missing = true
			prior.Causes = append([]MoodCause(nil), prior.Causes...)
			prior.Provision = append([]MoodProvision(nil), prior.Provision...)
			result.States = append(result.States, prior)
		}
	}
	sort.Slice(result.States, func(i, j int) bool { return result.States[i].Pawn.ID < result.States[j].Pawn.ID })
	return result, result.Validate()
}

func (s MoodState) Priority() int {
	if s.MentalRisk {
		return 1
	}
	if mental, known := s.Pawn.Mental.Value(); known && mental {
		return 1
	}
	return 2
}

func (s MoodState) Need() domain.NeedState {
	if s.Missing {
		return domain.NeedUnknown
	}
	dead, dk := s.Pawn.Dead.Value()
	_, mk := s.Pawn.Mood.Value()
	_, tk := s.Pawn.Threshold.Value()
	mental, mentalKnown := s.Pawn.Mental.Value()
	if mentalKnown && mental {
		return domain.NeedDeficit
	}
	if !dk || dead || !mk || !tk || !mentalKnown {
		return domain.NeedUnknown
	}
	if s.Active {
		return domain.NeedDeficit
	}
	return domain.NeedRecovered
}

type MoodMethodReason string

const (
	MoodRecovered   MoodMethodReason = "recovered"
	MoodUnavailable MoodMethodReason = "unknown_mood_or_pawn"
	MoodMentalBreak MoodMethodReason = "native_mental_break"
	MoodPlayerWork  MoodMethodReason = "pawn_unavailable_or_player_work"
	MoodNoCause     MoodMethodReason = "no_measured_correctable_need"
	MoodExhausted   MoodMethodReason = "bounded_methods_exhausted"
	MoodRelief      MoodMethodReason = "native_need_relief"
	// MoodProvisioned defers to the upkeep goal named in the proposal: the
	// pawn's pressure is dominated by environment thoughts that goal's
	// facility removes, so a native relief job would not clear it.
	MoodProvisioned MoodMethodReason = "facility_provision"
)

type MoodProposal struct {
	Pawn        PawnID
	Need        MoodNeed
	Reason      MoodMethodReason
	Goal        GoalID
	Target      float64
	NeedBenefit domain.Fact[float64]
	MoodBenefit domain.Fact[float64]
}

// SelectMoodMethod proposes one bounded native need method, or defers to the
// upkeep goal that owns the pawn's dominant environment pressure. The action
// family must still obtain current job/schedule admission and observe actual
// recovery.
func SelectMoodMethod(s MoodState, used []MoodNeed) (MoodProposal, error) {
	r := MoodProposal{Pawn: s.Pawn.ID}
	if err := (MoodHistory{States: []MoodState{s}}).Validate(); err != nil {
		return r, err
	}
	seen := map[MoodNeed]bool{}
	for _, n := range used {
		if n != MoodFood && n != MoodRest && n != MoodJoy || seen[n] {
			return r, errors.New("invalid used mood method")
		}
		seen[n] = true
	}
	if s.Missing || s.Need() == domain.NeedUnknown {
		r.Reason = MoodUnavailable
		return r, nil
	}
	if mental, k := s.Pawn.Mental.Value(); k && mental {
		r.Reason = MoodMentalBreak
		return r, nil
	}
	if !s.Active {
		r.Reason = MoodRecovered
		return r, nil
	}
	for _, f := range []domain.Fact[bool]{s.Pawn.Dead, s.Pawn.Downed, s.Pawn.Drafted, s.Pawn.PlayerForced} {
		if value, k := f.Value(); !k || value {
			r.Reason = MoodPlayerWork
			return r, nil
		}
	}
	if len(s.Provision) > 0 {
		r.Reason = MoodProvisioned
		r.Goal = s.Provision[0].Goal
		return r, nil
	}
	r.Reason = MoodNoCause
	if len(s.Causes) > 0 {
		r.Reason = MoodExhausted
	}
	for _, cause := range s.Causes {
		level, k := cause.Level.Value()
		if !k || seen[cause.Need] {
			continue
		}
		r.Need = cause.Need
		r.Reason = MoodRelief
		r.Target = .5
		r.NeedBenefit = domain.Known(.5 - level)
		return r, nil
	}
	return r, nil
}
