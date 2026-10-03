package bridge

import (
	"fmt"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
)

func validateColonyGear(v *o.GearSnapshot, ctx *c.ObservationContext, size *o.MapSize) error {
	if v == nil || !proto.Equal(v.Context, ctx) {
		return contract("gear census context mismatch")
	}
	if stored := v.StoredApparel; stored != nil {
		seen := map[string]bool{}
		for _, row := range stored.Rows {
			if row == nil || row.DefName == nil || validID(row.GetDefName()) != nil || row.Stuff == nil || row.GetStuff() != "" && validID(row.GetStuff()) != nil || row.Quality == nil || row.GetQuality() < 0 || row.GetQuality() > 6 || row.HpBand == nil || row.GetHpBand() < 5 || row.GetHpBand() > 9 || row.Count == nil || row.GetCount() <= 0 {
				return contract("invalid apparel storage row")
			}
			key := fmt.Sprintf("%s/%s/%d/%d", row.GetDefName(), row.GetStuff(), row.GetQuality(), row.GetHpBand())
			if seen[key] {
				return contract("duplicate apparel storage row")
			}
			seen[key] = true
		}
	}
	if len(v.OutdoorTemperatureByTwelfthC) != 0 || v.CurrentTwelfth != nil || v.TicksToNextTwelfth != nil || v.ActiveWeather != nil {
		if len(v.OutdoorTemperatureByTwelfthC) != 12 || v.CurrentTwelfth == nil || v.GetCurrentTwelfth() >= 12 || v.TicksToNextTwelfth == nil || v.GetTicksToNextTwelfth() <= 0 || v.GetTicksToNextTwelfth() > 300000 {
			return contract("invalid gear seasonal curve")
		}
		for _, n := range v.OutdoorTemperatureByTwelfthC {
			if math.IsNaN(float64(n)) || math.IsInf(float64(n), 0) {
				return contract("invalid gear seasonal temperature")
			}
		}
		if w := v.ActiveWeather; w != nil {
			if w.DefName == nil || (w.GetDefName() != "ColdSnap" && w.GetDefName() != "HeatWave") || w.RemainingTicks == nil || w.GetRemainingTicks() < -1 || w.TemperatureOffsetC == nil || math.IsNaN(float64(w.GetTemperatureOffsetC())) || math.IsInf(float64(w.GetTemperatureOffsetC()), 0) || w.GetDefName() == "ColdSnap" && w.GetTemperatureOffsetC() > 0 || w.GetDefName() == "HeatWave" && w.GetTemperatureOffsetC() < 0 {
				return contract("invalid gear weather condition")
			}
		}
	}
	people := map[string]bool{}
	for _, p := range v.Pawns {
		if p == nil || p.Snapshot == nil || !proto.Equal(p.Snapshot.Context, ctx) {
			return contract("missing or stale gear loadout")
		}
		if !validRef(p.Pawn) {
			return contract("invalid gear pawn")
		}
		id := p.Pawn.GetId()
		if people[id] {
			return contract("duplicate gear pawn")
		}
		people[id] = true
		if err := pawnsRef(p.Snapshot, id, ctx); err != nil {
			return err
		}
		if err := validateApparelPolicy(p.ApparelPolicy); err != nil {
			return err
		}
		if err := validateGearWearer(p); err != nil {
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
		candidates := []*o.GearItem{}
		for _, candidate := range p.Candidates {
			if candidate == nil || candidate.Item == nil || candidate.Gain == nil || !combatNumber(candidate.Gain, true) || candidate.GetGain() <= 0 || candidate.Blocker != nil || candidate.Reason != nil {
				return contract("invalid eligible gear candidate")
			}
			item := candidate.Item
			candidates = append(candidates, item)
		}
		if err := combatDetails(&o.PawnState{Equipment: &o.PawnEquipment{Equipped: candidates}}, ctx); err != nil {
			return err
		}
		if p.LoadoutModel == nil || p.ComfortableMinC == nil || p.ComfortableMaxC == nil {
			return contract("gear loadout without a loadout model or comfortable temperatures")
		}
		if err := validateGearModel(p.LoadoutModel); err != nil {
			return err
		}
	}
	return nil
}

// validateGearWearer checks the wear inputs of a pawn row: the pawn's gender
// and single developmental stage and the body part groups it still has a part
// in.
func validateGearWearer(p *o.GearLoadout) error {
	switch p.GetDevelopmentalStage() {
	case d.DevelopmentalStage_DEVELOPMENTAL_STAGE_NEWBORN, d.DevelopmentalStage_DEVELOPMENTAL_STAGE_BABY, d.DevelopmentalStage_DEVELOPMENTAL_STAGE_CHILD, d.DevelopmentalStage_DEVELOPMENTAL_STAGE_ADULT:
	default:
		return contract("invalid gear wearer stage")
	}
	if p.Gender == nil || p.GetGender().Descriptor().Values().ByNumber(p.GetGender().Number()) == nil || combatIDs(p.BodyPartGroups) != nil {
		return contract("invalid gear wearer")
	}
	return nil
}

// validateGearModel checks the loadout model's wire shape: identities, the
// source vocabulary and finite stats. Policy checks the model's bounds and
// conflicts when the row is mapped; a model it refuses is a named error.
func validateGearModel(m *o.GearLoadoutModel) error {
	if m == nil {
		return nil
	}
	for _, t := range m.Traits {
		if t == nil || validID(t.GetDefName()) != nil {
			return contract("invalid gear model trait")
		}
	}
	for _, list := range [][]*o.GearLoadoutOption{m.Worn, m.Options} {
		for _, x := range list {
			if x == nil || validID(x.GetId()) != nil || validID(x.GetDefName()) != nil || x.Stuff != nil && validID(x.GetStuff()) != nil || x.Quality == nil || x.Condition == nil {
				return contract("invalid gear model option")
			}
			switch x.GetSource() {
			case "worn", "loose", "stored", "bill":
			default:
				return contract("invalid gear model option source")
			}
			if !combatNumber(x.Condition, false) {
				return contract("invalid gear model option condition")
			}
			for _, q := range x.Ingredients {
				if q == nil || validID(q.GetDefName()) != nil || q.GetUnits() <= 0 {
					return contract("invalid gear model ingredient")
				}
			}
		}
	}
	return nil
}
