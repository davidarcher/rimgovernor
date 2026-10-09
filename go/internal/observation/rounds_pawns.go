package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Compare independent same-tick censuses before deriving available armed capacity.
// Missing or contradictory pawn details must not recover a defense deficit.
//
// The second count is the living, conscious colonists able to fight who hold
// no weapon: EnsureBasicDefense stays owed while any remain, so a loose
// starting club is equipped (or a weapon crafted) for every one of them, not
// only the first two. A row without its biography leaves it unknown.
func roundsArmed(colony *o.ColonyFactsSnapshot, emergency policy.EmergencyFacts, pawns *o.PawnSnapshot, downsides policy.CreepJoinerDownsides) (domain.Fact[int64], domain.Fact[int64]) {
	unknown := domain.Unknown[int64]()
	if colony == nil || colony.ColonistCount == nil || int(colony.GetColonistCount()) != len(emergency.Colonists) || len(pawns.Pawns) != len(emergency.Colonists) {
		return unknown, unknown
	}
	byID := make(map[string]*o.PawnState, len(pawns.Pawns))
	for _, row := range pawns.Pawns {
		byID[row.Pawn.GetId()] = row
	}
	var armed, unarmed int64
	unarmedKnown := true
	for _, pawn := range emergency.Colonists {
		row := byID[string(pawn.ID)]
		dead, dk := pawn.Dead.Value()
		downed, nk := pawn.Downed.Value()
		if row == nil || !dk || !nk || row.Dead == nil || row.Downed == nil || row.GetDead() != dead || row.GetDowned() != downed || row.Colonist == nil || !row.GetColonist() {
			return unknown, unknown
		}
		if dead || downed {
			continue
		}
		if row.Equipment == nil || row.Equipment.Armed == nil {
			return unknown, unknown
		}
		if row.Equipment.GetArmed() {
			armed++
			continue
		}
		// A colonist held back from arms (a creepjoiner whose downside has not
		// shown) is owed no weapon: counting it would hold
		// EnsureBasicDefense open for good.
		if downsides.ArmsHold(bridge.CreepJoinerPawn(row)) != "" {
			continue
		}
		if row.Biography == nil || hasIssue(row.Biography.GetIssues(), "disabled_work_tags") {
			unarmedKnown = false
			continue
		}
		violent := true
		for _, tag := range row.Biography.GetDisabledWorkTags() {
			violent = violent && tag != "Violent"
		}
		if violent {
			unarmed++
		}
	}
	if !unarmedKnown {
		return domain.Known(armed), unknown
	}
	return domain.Known(armed), domain.Known(unarmed)
}

// roundsDefenders is each census colonist's defense capacity facts
// from the same pawn rows roundsArmed compares: dead and downed
// from the emergency census, violence from the biography, melee power as
// the observed MeleeDPS times summary health and the observed ranged DPS.
// A row the census does not match leaves the roster unknown; a missing
// stat leaves that fact unknown.
func roundsDefenders(colony *o.ColonyFactsSnapshot, emergency policy.EmergencyFacts, pawns *o.PawnSnapshot) domain.Fact[[]policy.SquadDefenderFacts] {
	unknown := domain.Unknown[[]policy.SquadDefenderFacts]()
	if colony == nil || colony.ColonistCount == nil || int(colony.GetColonistCount()) != len(emergency.Colonists) || len(pawns.Pawns) != len(emergency.Colonists) {
		return unknown
	}
	byID := make(map[string]*o.PawnState, len(pawns.Pawns))
	for _, row := range pawns.Pawns {
		byID[row.Pawn.GetId()] = row
	}
	out := make([]policy.SquadDefenderFacts, 0, len(emergency.Colonists))
	for _, pawn := range emergency.Colonists {
		row := byID[string(pawn.ID)]
		if row == nil {
			return unknown
		}
		d := policy.SquadDefenderFacts{ID: domain.PawnID(pawn.ID), Dead: pawn.Dead, Downed: pawn.Downed}
		if bt, ok := bridge.PawnBiotech(row.Biotech).Value(); ok {
			d.Deathresting = bt.IsDeathresting()
		}
		if row.Biography != nil && !hasIssue(row.Biography.GetIssues(), "disabled_work_tags") {
			violent := true
			for _, tag := range row.Biography.GetDisabledWorkTags() {
				violent = violent && tag != "Violent"
			}
			d.ViolenceCapable = domain.Known(violent)
		}
		if e := row.Equipment; e != nil {
			d.RangedDPS = optional(e.RangedDps)
			if e.MeleeDps != nil && row.Health != nil && row.Health.SummaryFraction != nil {
				d.MeleePower = domain.Known(e.GetMeleeDps() * row.Health.GetSummaryFraction())
			}
		}
		out = append(out, d)
	}
	return domain.Known(out)
}
