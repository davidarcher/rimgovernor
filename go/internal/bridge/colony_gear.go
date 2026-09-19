package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// gearCandidateBound mirrors NativeGearFacts.CandidateBound.
const gearCandidateBound = 8

func validateColonyGear(v *o.GearSnapshot, ctx *c.ObservationContext, size *o.MapSize) error {
	if v == nil || !proto.Equal(v.Context, ctx) {
		return contract("gear census context mismatch")
	}
	if err := colonyCounts(v.Completeness, len(v.Pawns), 256); err != nil {
		return err
	}
	if v.Completeness.GetFiltered() != 0 {
		return contract("filtered gear census")
	}
	people := map[string]bool{}
	for _, p := range v.Pawns {
		if p == nil || p.Snapshot == nil || !proto.Equal(p.Snapshot.Context, ctx) {
			return contract("missing or stale gear loadout")
		}
		if err := pawnsEntity(p.Pawn, ctx); err != nil {
			return err
		}
		if p.Pawn.Position != nil && !colonyCell(p.Pawn.Position, size) {
			return contract("gear pawn outside map")
		}
		id := p.Pawn.GetId()
		if people[id] {
			return contract("duplicate gear pawn")
		}
		people[id] = true
		if err := pawnsRef(p.Snapshot, id, ctx); err != nil {
			return err
		}
		if !presentationText(p.Blocker, 4096) || p.Blocker != nil && p.GetBlocker() == "" || p.Blocker != nil && len(p.Candidates) > 0 {
			return contract("invalid blocked gear loadout")
		}
		if !combatNumber(p.ComfortableMinC, false) || !combatNumber(p.ComfortableMaxC, false) || p.ComfortableMinC != nil && p.ComfortableMaxC != nil && p.GetComfortableMinC() > p.GetComfortableMaxC() {
			return contract("invalid gear temperature range")
		}
		if err := combatDetails(&o.PawnState{Equipment: p.Equipment}, ctx); err != nil {
			return err
		}
		if err := colonyCounts(p.Completeness, len(p.Candidates), 256); err != nil {
			return err
		}
		// A loadout carries at most gearCandidateBound candidates (the best
		// by gain); further eligible items count as filtered only once the
		// bound is full, so a short census with omissions stays a contract
		// error (issue #320).
		if p.Completeness.GetFiltered() != 0 && len(p.Candidates) < gearCandidateBound || len(p.ReplacementNeeds) > 256 {
			return contract("incomplete gear candidates or oversized needs")
		}
		candidates := []*o.GearItem{}
		for _, candidate := range p.Candidates {
			if candidate == nil || candidate.Item == nil || candidate.Gain == nil || !combatNumber(candidate.Gain, true) || candidate.GetGain() <= 0 || candidate.Blocker != nil || candidate.Reason != nil {
				return contract("invalid eligible gear candidate")
			}
			item := candidate.Item
			if item.Apparel == nil || item.Weapon == nil || item.GetApparel() == item.GetWeapon() {
				return contract("unknown gear candidate kind")
			}
			if item.Thing == nil || validID(item.Thing.GetDefName()) != nil || item.Thing.Position == nil || !colonyCell(item.Thing.Position, size) {
				return contract("gear candidate outside map")
			}
			candidates = append(candidates, item)
		}
		if err := combatDetails(&o.PawnState{Equipment: &o.PawnEquipment{Equipped: candidates}}, ctx); err != nil {
			return err
		}
		needs := map[[3]string]bool{}
		for _, n := range p.ReplacementNeeds {
			if n == nil || validID(n.GetDefName()) != nil || n.Stuff != nil && validID(n.GetStuff()) != nil || validID(n.GetReason()) != nil || !combatNumber(n.Gain, false) {
				return contract("invalid gear production need")
			}
			key := [3]string{n.GetDefName(), n.GetStuff(), n.GetReason()}
			if needs[key] {
				return contract("duplicate gear production need")
			}
			needs[key] = true
		}
	}
	return nil
}
