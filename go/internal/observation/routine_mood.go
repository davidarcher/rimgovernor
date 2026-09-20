package observation

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func routineMood(colony *o.ColonyFactsSnapshot, emergency policy.EmergencyFacts, snapshot *o.PawnSnapshot) domain.Fact[[]policy.MoodPawn] {
	unknown := domain.Unknown[[]policy.MoodPawn]()
	complete, known := emergency.ColonistsComplete.Value()
	if !known || !complete || colony == nil || colony.ColonistCount == nil || snapshot == nil || int(colony.GetColonistCount()) != len(emergency.Colonists) {
		return unknown
	}
	byID := map[string]*o.PawnState{}
	for _, p := range snapshot.Pawns {
		if p == nil || p.Pawn == nil || byID[p.Pawn.GetId()] != nil {
			return unknown
		}
		byID[p.Pawn.GetId()] = p
	}
	forecasts := map[string]*o.PatientForecast{}
	for _, f := range colony.GetForecast().GetObserved().GetPatients() {
		forecasts[f.GetPawnId()] = f
	}
	rows := []policy.MoodPawn{}
	for _, pawn := range emergency.Colonists {
		r := byID[string(pawn.ID)]
		if r == nil {
			continue
		} // Exact missing IDs preserve prior active history.
		dead, dk := pawn.Dead.Value()
		downed, nk := pawn.Downed.Value()
		if !dk || !nk || r.Dead == nil || r.Downed == nil || r.GetDead() != dead || r.GetDowned() != downed || r.Colonist == nil || !r.GetColonist() {
			return unknown
		}
		p := policy.MoodPawn{Break: pawn.MentalState, ID: pawn.ID, Dead: optional(r.Dead), Downed: optional(r.Downed), Drafted: optional(r.Drafted), Mental: nativePresence(r.MentalState, r.Issues, "mental_state")}
		if job := r.Job; job != nil && !hasIssue(r.Issues, "job") {
			p.PlayerForced = optional(job.PlayerForced)
		}
		if n := r.Needs; n != nil && !hasIssue(r.Issues, "needs") {
			p.Mood, p.Threshold = optional(n.Mood), optional(n.BreakThresholdMinor)
			p.Food, p.Rest, p.Joy = optional(n.Food), optional(n.Rest), optional(n.Joy)
		}
		if f := forecasts[string(pawn.ID)]; f != nil && !hasIssue(f.Issues, "mood_target") {
			p.Target = optional(f.MoodTarget)
		}
		p.Thoughts = MoodThoughts(r)
		if r.Social != nil && !hasIssue(r.Issues, "social") {
			p.HighExpectations = optional(r.Social.HighExpectations)
		}
		rows = append(rows, p)
	}
	return domain.Known(rows)
}

// MoodThoughts lifts a pawn row's grouped native thought rows (memories and
// the situational cache) into the negative pressure the mood census keeps:
// one row per def with its total offset, most negative first. A skipped or
// unreadable social block, or a missing memories/situational list, leaves
// the pressure unknown rather than empty.
func MoodThoughts(r *o.PawnState) domain.Fact[[]policy.MoodThought] {
	unknown := domain.Unknown[[]policy.MoodThought]()
	social := r.GetSocial()
	if social == nil || hasIssue(r.Issues, "social") || hasIssue(social.Issues, "memories") || hasIssue(social.Issues, "situational") {
		return unknown
	}
	byDef := map[string]float64{}
	for _, rows := range [][]*o.Thought{social.Memories, social.Situational} {
		for _, t := range rows {
			if t == nil || t.MoodOffsetTotal == nil {
				return unknown
			}
			byDef[t.GetDefName()] += t.GetMoodOffsetTotal()
		}
	}
	result := []policy.MoodThought{}
	for def, offset := range byDef {
		if offset < 0 {
			result = append(result, policy.MoodThought{Def: def, Offset: offset})
		}
	}
	sort.Slice(result, func(i, j int) bool {
		if result[i].Offset != result[j].Offset {
			return result[i].Offset < result[j].Offset
		}
		return result[i].Def < result[j].Def
	})
	return domain.Known(result)
}
