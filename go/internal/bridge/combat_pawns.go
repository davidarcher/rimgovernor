package bridge

import (
	"context"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"math"
)

// ReadCombatPawns retains optional detailed facts and issues for exact IDs.
// A complete query is not proof that absent health, gear or capability is known.
func (client *Client) ReadCombatPawns(ctx context.Context, identity *c.Identity, ids []string) (*o.ListPawnsReply, Result, error) {
	return client.readPawns(ctx, identity, ids, true)
}

// ValidateCombatPawnSnapshot shares exact-ID validation with ReadCombatPawns.
func ValidateCombatPawnSnapshot(snapshot *o.PawnSnapshot, identity *c.Identity, ids []string) error {
	return validateDetailedPawnSnapshot(snapshot, identity, ids, false)
}
func validateDetailedPawnSnapshot(snapshot *o.PawnSnapshot, identity *c.Identity, ids []string, work bool) error {
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
	return pawnsSnapshotSelected(snapshot, identity, requested, true, work)
}
func combatNumber(v *float64, nonnegative bool) bool {
	return v == nil || !math.IsNaN(*v) && !math.IsInf(*v, 0) && (!nonnegative || *v >= 0)
}
func combatIDs(values []string) error {
	if len(values) > 256 {
		return contract("combat detail list too large")
	}
	seen := map[string]bool{}
	for _, id := range values {
		if validID(id) != nil || seen[id] {
			return contract("invalid duplicate combat detail ID")
		}
		seen[id] = true
	}
	return nil
}
func combatDefinition(v *o.DefinitionRef) error {
	if v == nil {
		return contract("combat definition missing")
	}
	if validID(v.GetDefName()) != nil || !presentationText(v.Label, 16384) {
		return contract("combat definition invalid")
	}
	return nil
}
func combatDetails(row *o.PawnState, ctx *c.ObservationContext) error {
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
		if len(h.Capacities) > 256 || len(h.Hediffs) > 256 || len(h.SurgeryBills) > 256 {
			return contract("combat health list too large")
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
			if err := combatDefinition(v.Definition); err != nil {
				return err
			}
			if v.PartDefName != nil && validID(v.GetPartDefName()) != nil || !presentationText(v.PartLabel, 16384) || !presentationText(v.SeverityLabel, 16384) || v.PartIndex != nil && v.GetPartIndex() < 0 || v.TendExpiresInTicks != nil && v.GetTendExpiresInTicks() < 0 || v.NextTendInTicks != nil && v.GetNextTendInTicks() < 0 {
				return contract("invalid hediff fields")
			}
			for _, n := range []*float64{v.Severity, v.TendQuality, v.Immunity} {
				if !combatNumber(n, false) {
					return contract("nonfinite hediff")
				}
			}
		}
		if h.HediffCompleteness != nil {
			v := h.HediffCompleteness
			if v.Page == nil || v.Page.Complete == nil || v.GetReturned() > uint64(len(h.Hediffs)) || v.Returned != nil && v.GetReturned() != uint64(len(h.Hediffs)) {
				return contract("invalid hediff completeness")
			}
			if v.Page.GetComplete() && (v.Page.GetNextCursor() != "" || v.Matched != nil && v.GetMatched() != uint64(len(h.Hediffs)) || v.Unreadable != nil && v.GetUnreadable() != 0) {
				return contract("contradictory hediff completeness")
			}
			if v.SnapshotToken != nil && validID(v.GetSnapshotToken()) != nil {
				return contract("invalid hediff token")
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
			if len(list) > 256 {
				return contract("equipment list too large")
			}
			seen := map[string]bool{}
			for _, g := range list {
				if g == nil {
					return contract("missing gear")
				}
				if err := pawnsEntity(g.Thing, ctx); err != nil {
					return err
				}
				if seen[g.Thing.GetId()] {
					return contract("duplicate gear")
				}
				seen[g.Thing.GetId()] = true
				for _, id := range []*string{g.Stuff, g.Quality} {
					if id != nil && validID(*id) != nil {
						return contract("invalid gear definition")
					}
				}
				if g.HitPoints != nil && g.GetHitPoints() < 0 || g.MaxHitPoints != nil && g.GetMaxHitPoints() <= 0 {
					return contract("invalid gear hit points")
				}
				for _, n := range []*float64{g.ConditionFraction, g.ArmorSharp, g.ArmorBlunt, g.InsulationCold, g.InsulationHeat} {
					if !combatNumber(n, false) {
						return contract("invalid gear number")
					}
				}
				if err := combatIDs(g.ApparelLayers); err != nil {
					return err
				}
				if err := combatIDs(g.BodyPartGroups); err != nil {
					return err
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
		for _, v := range []*o.DefinitionRef{b.Childhood, b.Adulthood} {
			if v != nil {
				if err := combatDefinition(v); err != nil {
					return err
				}
			}
		}
		if len(b.Skills) > 256 || len(b.Traits) > 256 {
			return contract("biography list too large")
		}
		seen := map[string]bool{}
		for _, v := range b.Skills {
			if v == nil {
				return contract("missing skill")
			}
			if err := combatDefinition(v.Definition); err != nil {
				return err
			}
			id := v.Definition.GetDefName()
			if seen[id] || v.Level != nil && v.GetLevel() < 0 || !combatNumber(v.StoredLevel, true) || v.Passion != nil && validID(v.GetPassion()) != nil {
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
	return nil
}
