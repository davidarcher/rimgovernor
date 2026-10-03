package bridge

import (
	"context"
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// ReadCombatPawns retains optional detailed facts and issues for exact IDs.
// A complete query is not proof that absent health, gear or capability is known.
func (client *Client) ReadCombatPawns(ctx context.Context, identity *c.Identity, ids []string) (*o.ListPawnsReply, Result, error) {
	return client.readPawns(ctx, identity, ids, true)
}

func validateDetailedPawnSnapshot(snapshot *o.PawnSnapshot, identity *c.Identity, ids []string, want pawnDetails) error {
	if err := ValidateIdentity(identity); err != nil {
		return err
	}
	if len(ids) < 1 || len(ids) > 256 {
		return contract("pawn IDs outside 1..256")
	}
	requested := make(map[string]bool, len(ids))
	for _, id := range ids {
		if validID(id) != nil || requested[id] {
			return contract("invalid or duplicate requested pawn")
		}
		requested[id] = true
	}
	if err := buildingUnknown(snapshot); err != nil {
		return err
	}
	return pawnsSnapshotSelected(snapshot, identity, requested, want)
}
func combatNumber(v *float64, nonnegative bool) bool {
	return v == nil || !math.IsNaN(*v) && !math.IsInf(*v, 0) && (!nonnegative || *v >= 0)
}
func combatIDs(values []string) error {
	seen := map[string]bool{}
	for _, id := range values {
		if validID(id) != nil || seen[id] {
			return contract("invalid duplicate combat detail ID")
		}
		seen[id] = true
	}
	return nil
}
func combatDefName(v *string) error {
	if v == nil {
		return contract("combat definition missing")
	}
	if validID(*v) != nil {
		return contract("combat definition invalid")
	}
	return nil
}
func validBiocode(g *o.GearItem) bool {
	return g.BiocodedTo == nil || validID(g.GetBiocodedTo()) == nil && (g.Biocoded == nil || g.GetBiocoded())
}

func combatDetails(row *o.PawnState, ctx *c.ObservationContext) error {
	if !combatNumber(row.RaidArmor, true) {
		return contract("invalid raid armor")
	}
	if h := row.Health; h != nil {
		for _, v := range []*float64{h.SummaryFraction, h.BleedRatePerDay, h.Pain, h.BloodLoss, h.HoursUntilDeathFromBloodLoss} {
			if !combatNumber(v, true) {
				return contract("invalid combat health number")
			}
		}
		if h.SummaryFraction != nil && h.GetSummaryFraction() > 1 {
			return contract("invalid combat health fraction")
		}
		if h.BedId != nil && validID(h.GetBedId()) != nil {
			return contract("invalid combat bed")
		}
		seen := map[string]bool{}
		for _, v := range h.Capacities {
			if v == nil || validID(v.GetDefName()) != nil || seen[v.GetDefName()] || !combatNumber(v.Level, true) {
				return contract("invalid combat capacity")
			}
			seen[v.GetDefName()] = true
			if v.Unavailable != nil {
				if v.Level != nil {
					return contract("capacity known and unavailable")
				}
				if err := validateUnavailable(v.Unavailable); err != nil {
					return err
				}
			}
		}
		for _, v := range h.Hediffs {
			if v == nil {
				return contract("missing hediff")
			}
			if err := combatDefName(v.DefName); err != nil {
				return err
			}
			if v.PartDefName != nil && validID(v.GetPartDefName()) != nil || !presentationText(v.PartLabel, 16384) || !presentationText(v.SeverityLabel, 16384) || v.PartIndex != nil && v.GetPartIndex() < 0 || v.TendExpiresInTicks != nil && v.GetTendExpiresInTicks() < 0 || v.NextTendInTicks != nil && v.GetNextTendInTicks() < 0 {
				return contract("invalid hediff fields")
			}
			for _, n := range []*float64{v.Severity, v.TendQuality, v.Immunity, v.SeverityPerDay, v.ImmunityPerDay} {
				if !combatNumber(n, false) {
					return contract("nonfinite hediff")
				}
			}
		}
		seen = map[string]bool{}
		for _, v := range h.SurgeryBills {
			if v == nil || validID(v.GetId()) != nil || seen[v.GetId()] || v.Recipe != nil && validID(v.GetRecipe()) != nil || v.PartIndex != nil && v.GetPartIndex() < 0 {
				return contract("invalid surgery bill")
			}
			seen[v.GetId()] = true
		}
		if h.Snapshot != nil {
			if err := pawnsRef(h.Snapshot, row.Pawn.GetId(), ctx); err != nil {
				return err
			}
		}
		if err := pawnsIssues(h.Issues, h.ProtoReflect()); err != nil {
			return err
		}
	}
	if e := row.Equipment; e != nil {
		for _, id := range []*string{e.PrimaryId, e.CarriedThingId} {
			if id != nil && validID(*id) != nil {
				return contract("invalid equipment identity")
			}
		}
		for _, list := range [][]*o.GearItem{e.Equipped, e.Apparel, e.InventoryWeapons} {
			seen := map[string]bool{}
			for _, g := range list {
				if g == nil {
					return contract("missing gear")
				}
				if !validRef(g.Thing) {
					return contract("invalid gear reference")
				}
				if seen[g.Thing.GetId()] {
					return contract("duplicate gear")
				}
				seen[g.Thing.GetId()] = true
				for _, id := range []*string{g.Stuff} {
					if id != nil && validID(*id) != nil {
						return contract("invalid gear definition")
					}
				}
				if !validBiocode(g) {
					return contract("invalid gear biocode")
				}
				if g.HitPoints != nil && g.GetHitPoints() < 0 || g.MaxHitPoints != nil && g.GetMaxHitPoints() <= 0 {
					return contract("invalid gear hit points")
				}
				for _, n := range []*float64{g.ConditionFraction, g.ArmorSharp, g.ArmorBlunt, g.InsulationCold, g.InsulationHeat} {
					if !combatNumber(n, false) {
						return contract("invalid gear number")
					}
				}
			}
		}
		if e.Armed != nil && !e.GetArmed() && e.PrimaryId != nil {
			return contract("unarmed pawn has primary")
		}
		if e.InventoryItemCount != nil && uint64(e.GetInventoryItemCount()) < uint64(len(e.InventoryWeapons)) {
			return contract("inventory count mismatch")
		}
		if err := pawnsIssues(e.Issues, e.ProtoReflect()); err != nil {
			return err
		}
	}
	if b := row.Biography; b != nil {
		if !combatNumber(b.BiologicalAgeYears, true) || !combatNumber(b.ChronologicalAgeYears, true) || !presentationText(b.Title, 16384) || !presentationText(b.TitleSource, 16384) {
			return contract("invalid biography")
		}
		for _, v := range []*string{b.ChildhoodDefName, b.AdulthoodDefName} {
			if v != nil {
				if err := combatDefName(v); err != nil {
					return err
				}
			}
		}
		seen := map[string]bool{}
		for _, v := range b.Skills {
			if v == nil {
				return contract("missing skill")
			}
			if err := combatDefName(v.DefName); err != nil {
				return err
			}
			id := v.GetDefName()
			if seen[id] || v.Level != nil && v.GetLevel() < 0 || !combatNumber(v.StoredLevel, true) || v.Passion != nil && PassionName(v.GetPassion()) == "" {
				return contract("invalid skill")
			}
			seen[id] = true
		}
		// Trait degrees are signed native values; hediffs and traits can repeat.
		for _, v := range b.Traits {
			if v == nil || validID(v.GetDefName()) != nil {
				return contract("invalid trait")
			}
		}
		for _, list := range [][]string{b.IncapableWorkTypes, b.IncapableSources, b.DisabledWorkTags} {
			if err := combatIDs(list); err != nil {
				return err
			}
		}
		if err := pawnsIssues(b.Issues, b.ProtoReflect()); err != nil {
			return err
		}
	}
	if a := row.AnimalState; a != nil {
		// Training rows carry MaintainHerd's training deficit: one per
		// trainable def.
		trainables := map[string]bool{}
		for _, entry := range a.Training {
			if entry == nil || validID(entry.GetDefName()) != nil || trainables[entry.GetDefName()] || !presentationText(entry.Reason, 4096) {
				return contract("invalid animal training")
			}
			trainables[entry.GetDefName()] = true
		}
		if a.PenId != nil && (validID(a.GetPenId()) != nil || a.Contained != nil && !a.GetContained()) || a.AllowedAreaId != nil && a.GetAllowedAreaId() != "" && validID(a.GetAllowedAreaId()) != nil {
			return contract("invalid animal herd facts")
		}
		if err := pawnsIssues(a.Issues, a.ProtoReflect()); err != nil {
			return err
		}
	}
	return nil
}
