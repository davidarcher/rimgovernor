package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type CoreSiteWork struct {
	Quest        domain.QuestID
	Kind, Reason string
	Mine         domain.Acquisition
	Help         domain.RecoveryService
	Recruit      domain.PrisonerInteraction
}

// PlanCoreSiteWork follows only the exact quest-linked site and native targets.
// A designation starts work; the mineable disappearing is its completion evidence.
func PlanCoreSiteWork(offer JoinerOffer, site WorldSite, f RoundsFacts, currentMap domain.MapID) CoreSiteWork {
	result := CoreSiteWork{Quest: offer.Quest}
	profile, known := offer.Profile.Value()
	if !known || profile.Family != QuestFamilySite && profile.Family != QuestFamilyBanditCamp && !(profile.Family == QuestFamilyHack && offer.State == "EndedFailed") && !OdysseyGroundSiteRoot(offer.ScriptDef) || offer.State != "Ongoing" && offer.State != "EndedSuccess" && offer.State != "EndedFailed" || !slices.Contains(site.QuestIDs, offer.Quest) {
		return result
	}
	result.Kind = "wait"
	siteMap, known := site.Map.Value()
	if !known || siteMap != currentMap {
		result.Reason = "site_map_unavailable"
		return result
	}
	threat, known := site.Threat.Value()
	if !known || threat {
		result.Reason = "site_security"
		return result
	}
	if offer.State == "EndedFailed" {
		result.Kind = "return"
		result.Reason = "quest_failed"
		return result
	}
	switch offer.ScriptDef {
	case "OpportunitySite_DownedRefugee", "OpportunitySite_PrisonerWillingToJoin":
		return coreSiteJoiner(offer, site, f, result)
	case "OpportunitySite_LongRangeMineralScannerLump", "LongRangeMineralScannerLump":
		targets := slices.Clone(site.MiningTargets)
		sort.Slice(targets, func(i, j int) bool { return targets[i].ID < targets[j].ID })
		for _, target := range targets {
			if target.Map != currentMap {
				result.Reason = "mining_target_map"
				return result
			}
			designated, known := target.Designated.Value()
			if !known {
				result.Reason = "mining_designation_unknown"
				return result
			}
			if designated {
				continue
			}
			mine, err := domain.NewAcquisition(target.ID, target.Def, target.Cell)
			if err != nil {
				result.Reason = "mining_target_unknown"
				return result
			}
			result.Kind, result.Mine = "mine", mine
			return result
		}
		if len(targets) > 0 {
			result.Reason = "mining_work"
			return result
		}
	}
	result.Kind = "return"
	return result
}

func coreSiteJoiner(offer JoinerOffer, site WorldSite, f RoundsFacts, result CoreSiteWork) CoreSiteWork {
	ids := []domain.PawnID{}
	for _, objective := range offer.Objectives {
		if objective.Kind == o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_RESCUE_PAWNS {
			ids = append(ids, objective.PawnIDs...)
		}
	}
	sort.Slice(ids, func(i, j int) bool { return ids[i] < ids[j] })
	rows, known := f.Custody.Value()
	if !known || len(ids) == 0 {
		result.Reason = "rescue_target_unknown"
		return result
	}
	for _, id := range ids {
		index := slices.IndexFunc(rows, func(row CustodyFacts) bool { return row.Pawn == id })
		if index < 0 {
			result.Reason = "rescue_target_unavailable"
			return result
		}
		row := rows[index]
		dead, dk := row.Dead.Value()
		admitted, ak := row.Admitted.Value()
		if !dk || !ak {
			result.Reason = "rescue_state_unknown"
			return result
		}
		if dead {
			result.Kind = "return"
			result.Reason = "rescue_target_dead"
			return result
		}
		if admitted {
			continue
		}
		willing, wk := row.WillJoinIfRescued.Value()
		if wk && willing {
			extraction, ek := site.Extraction.Value()
			if ek {
				actors := slices.Clone(extraction.Crew)
				sort.Slice(actors, func(i, j int) bool { return actors[i] < actors[j] })
				for _, actor := range actors {
					index := slices.IndexFunc(rows, func(row CustodyFacts) bool { return row.Pawn == actor })
					if index < 0 || actor == id {
						continue
					}
					performer := rows[index]
					dead, dk := performer.Dead.Value()
					downed, nk := performer.Downed.Value()
					admitted, ak := performer.Admitted.Value()
					if !dk || !nk || !ak || dead || downed || !admitted {
						continue
					}
					help, err := domain.NewRecoveryService(actor, string(id), domain.RecoveryServiceOfferHelp)
					if err == nil {
						result.Kind, result.Help = "help", help
						return result
					}
				}
			}
			result.Reason = "rescue_actor_unavailable"
			return result
		}
		prisoners, pk := f.Prisoners.Value()
		if pk {
			for _, prisoner := range prisoners {
				if prisoner.Pawn != id {
					continue
				}
				owned, ok := prisoner.Prisoner.Value()
				recruitable, rk := prisoner.Recruitable.Value()
				mode, mk := prisoner.CurrentInteraction.Value()
				if ok && owned && rk && recruitable && mk && mode != domain.PrisonerInteractionRecruit {
					recruit, err := domain.NewPrisonerInteraction(id, domain.PrisonerInteractionRecruit)
					if err == nil {
						result.Kind, result.Recruit = "recruit", recruit
						return result
					}
				}
			}
		}
		result.Kind, result.Reason = "waitrescue", "rescue_work"
		return result
	}
	result.Kind = "return"
	return result
}
