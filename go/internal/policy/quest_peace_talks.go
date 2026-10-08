package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Loss includes vanilla disaster's forced-hostility adjustment; gain is capped
// by native faction goodwill limits. These are observations, not Go tuning.
type PeaceTalksRisk struct {
	WorstGoodwillLoss domain.Fact[int32]
	BestGoodwillGain  domain.Fact[int32]
	IdeologyActive    domain.Fact[bool]
}

type PeaceTalksPawn struct {
	ID     PawnID
	Social domain.Fact[int32]
	Leader domain.Fact[bool]
}

func PeaceTalksAdmission(offer JoinerOffer, f RoundsFacts) QuestSkipReason {
	site, found := questExpeditionSite(offer, f)
	if !found {
		return "site_unknown"
	}
	return PlanPeaceTalks(offer, site, f, f.AnimalUpkeep.Food, DefaultRoundsPolicy()).Reason
}

func PlanPeaceTalks(offer JoinerOffer, site ExpeditionSite, f RoundsFacts, food domain.Fact[FoodSupply], p RoundsPolicy) ExpeditionPlan {
	fail := func(reason QuestSkipReason) ExpeditionPlan {
		return ExpeditionPlan{Quest: offer.Quest, Site: site.ID, Reason: reason}
	}
	sites, known := f.QuestSites.Value()
	if !known {
		return fail("diplomacy_unknown")
	}
	var risk PeaceTalksRisk
	found := false
	for _, row := range sites {
		if row.ID == site.ID {
			risk, found = row.PeaceTalks.Value()
			break
		}
	}
	if !found {
		return fail("diplomacy_unknown")
	}
	if reason := PeaceTalksDownside(risk); reason != "" {
		return fail(reason)
	}
	departures, known := f.QuestDeparturePawns.Value()
	if !known {
		return fail("capacity_unknown")
	}
	pawns := make([]PeaceTalksPawn, 0, len(departures))
	for _, row := range departures {
		pawns = append(pawns, PeaceTalksPawn{ID: row.ID, Social: row.SocialLevel, Leader: row.FactionLeader})
	}
	diplomat, reason := PeaceTalksNegotiator(offer, f, domain.Known(pawns), risk.IdeologyActive)
	if reason != "" {
		return fail(reason)
	}
	return PlanExpeditionCrew(offer, site, f, food, p, []domain.PawnID{diplomat})
}

func PeaceTalksDownside(risk PeaceTalksRisk) QuestSkipReason {
	loss, lk := risk.WorstGoodwillLoss.Value()
	gain, gk := risk.BestGoodwillGain.Value()
	if !lk || !gk || loss < 0 || gain < 0 {
		return "diplomacy_unknown"
	}
	if loss > gain {
		return "goodwill_downside"
	}
	return ""
}

// Negotiator staffing first preserves ordinary home work/defense. Ideology's
// leader is preferred when eligible; otherwise Social ranks candidates. Vanilla
// still chooses its diplomat by NegotiationAbility when the caravan arrives.
func PeaceTalksNegotiator(offer JoinerOffer, f RoundsFacts, pawns domain.Fact[[]PeaceTalksPawn], ideology domain.Fact[bool]) (domain.PawnID, QuestSkipReason) {
	rows, known := pawns.Value()
	ideo, ik := ideology.Value()
	if !known || !ik {
		return "", "diplomacy_unknown"
	}
	rows = slices.Clone(rows)
	// Native omits the diplomacy skill when NegotiationAbility is disabled.
	// Unavailable candidates cannot displace a known capable diplomat.
	rows = slices.DeleteFunc(rows, func(pawn PeaceTalksPawn) bool {
		social, known := pawn.Social.Value()
		_, leaderKnown := pawn.Leader.Value()
		return !known || social < 0 || ideo && !leaderKnown
	})
	if len(rows) == 0 {
		return "", "diplomacy_unknown"
	}
	sort.Slice(rows, func(i, j int) bool {
		a, _ := rows[i].Leader.Value()
		b, _ := rows[j].Leader.Value()
		if ideo && a != b {
			return a
		}
		sa, _ := rows[i].Social.Value()
		sb, _ := rows[j].Social.Value()
		if sa != sb {
			return sa > sb
		}
		return rows[i].ID < rows[j].ID
	})
	spare, sk := f.QuestSparePawns.Value()
	departures, dk := f.QuestDeparturePawns.Value()
	capacity, ck := f.DefenseCapacity.Value()
	raid, rk := f.RaidPoints.Value()
	if !sk || !dk || !ck || !rk || !finite(capacity) || !finite(raid) || capacity < 0 || raid < 0 {
		return "", "capacity_unknown"
	}
	for _, pawn := range rows {
		if !slices.Contains(spare, pawn.ID) {
			continue
		}
		trial := f
		trial.QuestSparePawns = domain.Known([]PawnID{pawn.ID})
		mission := offer
		mission.Profile = domain.Known(QuestProfile{Family: QuestFamilyPawnLend})
		mission.Objectives = []QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_LOAD_PAWNS, Count: domain.Known(int64(1))}}
		crew, reason := departureSquad(mission, trial, false)
		if reason != "" || len(crew) != 1 {
			continue
		}
		for _, departure := range departures {
			if departure.ID != pawn.ID {
				continue
			}
			strength, known := departure.DefensePoints.Value()
			if !known || !finite(strength) || strength < 0 {
				return "", "defense_unknown"
			}
			if capacity-strength < raid {
				continue
			}
			return crew[0], ""
		}
	}
	return "", "negotiator_capacity"
}
