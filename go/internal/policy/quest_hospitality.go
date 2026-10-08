package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func HospitalityFamily(f QuestFamily) bool {
	return f == QuestFamilyHospitalityAnimals || f == QuestFamilyHospitalityJoiners || f == QuestFamilyHospitalityPrisoners || f == QuestFamilyHospitalityRefugee || f == QuestFamilyLaborers
}

type HospitalityWork struct {
	Quest   domain.QuestID
	Assign  *domain.Assign
	Shuttle *domain.QuestShuttle
	Reason  QuestSkipReason
	Waiting bool
}

// SelectHospitalityWork reuses sleeping upkeep and ordinary guest work, including
// crash rescue pickup. Native facts end hosting and prove transporter loading.
func SelectHospitalityWork(f RoundsFacts, tick domain.Tick) (HospitalityWork, error) {
	rows, known := f.QuestOffers.Value()
	if !known {
		return HospitalityWork{Reason: "quest_census_unknown"}, nil
	}
	rows = slices.Clone(rows)
	slices.SortFunc(rows, func(a, b JoinerOffer) int {
		if a.Quest < b.Quest {
			return -1
		}
		if a.Quest > b.Quest {
			return 1
		}
		return 0
	})
	var waiting HospitalityWork
	for _, offer := range rows {
		one := f
		one.QuestOffers = domain.Known([]JoinerOffer{offer})
		work, err := hospitalityWork(one, tick)
		if err != nil || work.Assign != nil || work.Shuttle != nil {
			return work, err
		}
		if waiting.Quest == "" && work.Quest != "" {
			waiting = work
		}
	}
	return waiting, nil
}

func hospitalityWork(f RoundsFacts, tick domain.Tick) (HospitalityWork, error) {
	offers, known := f.QuestOffers.Value()
	if !known {
		return HospitalityWork{Reason: "quest_census_unknown"}, nil
	}
	offers = slices.Clone(offers)
	slices.SortFunc(offers, func(a, b JoinerOffer) int {
		if a.Quest < b.Quest {
			return -1
		}
		if a.Quest > b.Quest {
			return 1
		}
		return 0
	})
	for _, offer := range offers {
		profile, known := offer.Profile.Value()
		if !known || !HospitalityFamily(profile.Family) && profile.Family != QuestFamilyShuttleRescue || profile.NeverAct && profile.Family != QuestFamilyLaborers || offer.State != "Ongoing" && offer.State != "NotYetAccepted" {
			continue
		}
		work := HospitalityWork{Quest: offer.Quest}
		if profile.Family == QuestFamilyShuttleRescue {
			if offer.State != "Ongoing" {
				continue
			}
			if calm, known := f.QuestColonyCalm.Value(); !known || !calm {
				work.Waiting = true
				return work, nil
			}
		}
		if offer.State == "NotYetAccepted" && offer.CanAccept {
			continue
		}
		if profile.Family != QuestFamilyHospitalityAnimals && profile.Family != QuestFamilyHospitalityPrisoners && profile.Family != QuestFamilyShuttleRescue {
			review, err := ReviewSleeping(f.Sleeping, SleepingHistory{}, tick)
			if err != nil {
				return work, err
			}
			if targets, known := review.Targets.Value(); known {
				sleeping, _ := f.Sleeping.Value()
				guests := map[PawnID]bool{}
				for _, guest := range sleeping.Guests {
					guests[guest.ID] = true
				}
				if offer.State == "NotYetAccepted" {
					for _, person := range sleeping.People {
						if person.Title != nil {
							guests[person.ID] = true
						}
					}
				}
				for _, target := range targets {
					if !guests[target.Pawn] || len(target.Available) == 0 {
						continue
					}
					previous := domain.ClearPrevious()
					if target.PreviousBed != "" {
						previous, err = domain.KnownPrevious(target.PreviousBed)
						if err != nil {
							return work, err
						}
					}
					assign, err := domain.NewAssign(domain.PawnID(target.Pawn), target.Available[0], previous)
					if err != nil {
						return work, err
					}
					work.Assign = &assign
					return work, nil
				}
			}
		}
		if offer.State != "Ongoing" {
			continue
		}
		if len(offer.Shuttles) > 1 {
			work.Reason = "shuttle_ambiguous"
			return work, nil
		}
		if len(offer.Shuttles) == 1 {
			shuttle := offer.Shuttles[0]
			loaded, lk := shuttle.AllRequiredLoaded.Value()
			manual, mk := shuttle.ManualLaunchAvailable.Value()
			if !lk || !mk {
				work.Reason = "shuttle_unknown"
				return work, nil
			}
			if loaded {
				if manual {
					intent, err := domain.NewQuestShuttle(offer.Quest, domain.ShuttleLaunchOnly, false, nil, true)
					if err != nil {
						return work, err
					}
					work.Shuttle = &intent
				} else {
					work.Waiting = true
				}
				return work, nil
			}
			available, ak := shuttle.AutoloadAvailable.Value()
			enabled, ek := shuttle.Autoload.Value()
			if !ak || !ek {
				work.Reason = "shuttle_unknown"
				return work, nil
			}
			if !available {
				work.Reason = "autoload_unavailable"
				return work, nil
			}
			if !enabled {
				intent, err := domain.NewQuestShuttle(offer.Quest, domain.ShuttleAutoload, true, nil, false)
				if err != nil {
					return work, err
				}
				work.Shuttle = &intent
			} else {
				work.Waiting = true
			}
			return work, nil
		}
		for _, objective := range offer.Objectives {
			if objective.Kind != o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_HOST_LODGERS {
				continue
			}
			if threshold, known := objective.MinimumMood.Value(); known {
				for _, guest := range objective.LodgerMoods {
					if mood, known := guest.Mood.Value(); known && mood < threshold {
						work.Reason = "lodger_mood_upkeep"
						work.Waiting = true
						return work, nil
					}
				}
			}
		}
		work.Waiting = true
		return work, nil
	}
	return HospitalityWork{}, nil
}

// HospitalityAdmission requires vacant appropriate beds before adding guests.
func HospitalityAdmission(offer JoinerOffer, f RoundsFacts) QuestSkipReason {
	profile, _ := offer.Profile.Value()
	if !HospitalityFamily(profile.Family) || profile.Family == QuestFamilyHospitalityAnimals {
		return ""
	}
	need := int64(0)
	for _, objective := range offer.Objectives {
		if objective.Kind == o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_HOST_LODGERS {
			count, known := objective.Count.Value()
			if !known {
				return "guest_count_unknown"
			}
			need = max(need, count)
		}
	}
	if need <= 0 {
		if profile.ProtectGuests {
			return ""
		}
		return "guest_count_unknown"
	}
	sleeping, known := f.Sleeping.Value()
	if !known {
		return "guest_beds_unknown"
	}
	spare := int64(0)
	for _, bed := range sleeping.Beds {
		if len(bed.Owners) == 0 && len(bed.Users) == 0 && positive(bed.Humanlike) && positive(bed.Roofed) && negative(bed.Medical) && !bed.Slaves && (profile.Family == QuestFamilyHospitalityPrisoners && positive(bed.Prisoners) || profile.Family != QuestFamilyHospitalityPrisoners && negative(bed.Prisoners)) {
			spare++
		}
	}
	if spare < need {
		return "guest_bed_capacity"
	}
	return ""
}

func HospitalityDeficit(f RoundsFacts) bool {
	rows, _ := f.QuestOffers.Value()
	for _, offer := range rows {
		p, known := offer.Profile.Value()
		if known && (HospitalityFamily(p.Family) || p.Family == QuestFamilyShuttleRescue) && (!p.NeverAct || p.Family == QuestFamilyLaborers) && offer.State == "Ongoing" {
			return true
		}
		if known && HospitalityFamily(p.Family) && !p.NeverAct && offer.State == "NotYetAccepted" && !offer.CanAccept {
			for _, objective := range offer.Objectives {
				if objective.Kind == o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_ACCEPT_REQUIREMENT_UNMET {
					return true
				}
			}
		}
	}
	return false
}

func ProtectedQuestGuestIDs(offers domain.Fact[[]JoinerOffer]) map[domain.PawnID]bool {
	ids := map[domain.PawnID]bool{}
	rows, _ := offers.Value()
	for _, offer := range rows {
		profile, known := offer.Profile.Value()
		if !known || !profile.ProtectGuests || offer.State != "Ongoing" {
			continue
		}
		for _, objective := range offer.Objectives {
			if objective.Kind == o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_HOST_LODGERS {
				for _, id := range objective.PawnIDs {
					ids[id] = true
				}
			}
		}
	}
	return ids
}
