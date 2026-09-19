package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
)

func validateColonyPower(v *o.DevelopmentFacts, identity *c.Identity, size *o.MapSize) error {
	if v == nil || !proto.Equal(v, &o.DevelopmentFacts{Power: v.Power, Furniture: v.Furniture, Completeness: v.Completeness, Networks: v.Networks}) {
		return contract("unsupported development facts")
	}
	if err := colonyCounts(v.Completeness, len(v.Power)+len(v.Furniture), 256); err != nil {
		return err
	}
	if len(v.Networks) > 256 {
		return contract("power network census exceeds bound")
	}
	seen := map[string]bool{}
	for _, row := range v.Power {
		if row == nil || row.Building == nil {
			return contract("missing power building")
		}
		b := row.Building
		ref := b.Building
		if !powerEntity(ref, identity, size) || seen[ref.GetId()] {
			return contract("invalid power building identity")
		}
		seen[ref.GetId()] = true
		if !proto.Equal(b, &o.BuildingState{Building: ref, Service: b.Service, Settings: b.Settings, OccupiedCells: b.OccupiedCells}) {
			return contract("unsupported power building detail")
		}
		if len(b.OccupiedCells) > 4096 {
			return contract("power footprint exceeds bound")
		}
		occupied := map[[2]int32]bool{}
		for _, cell := range b.OccupiedCells {
			key := [2]int32{cell.GetX(), cell.GetZ()}
			if !colonyCell(cell, size) || occupied[key] {
				return contract("invalid power footprint")
			}
			occupied[key] = true
		}
		if len(occupied) > 0 && (ref.Position == nil || !occupied[[2]int32{ref.Position.GetX(), ref.Position.GetZ()}]) {
			return contract("power footprint misses anchor")
		}
		s := b.Service
		if s == nil || !proto.Equal(s, &o.BuildingServiceState{Connected: s.Connected, PowerOn: s.PowerOn, PowerOutputW: s.PowerOutputW, SwitchedOn: s.SwitchedOn, PowerNetId: s.PowerNetId, Fuel: s.Fuel, TargetFuel: s.TargetFuel, OutOfFuel: s.OutOfFuel, BrokenDown: s.BrokenDown, AllowedFuelDefs: s.AllowedFuelDefs}) {
			return contract("unsupported power service detail")
		}
		if len(s.AllowedFuelDefs) > 256 {
			return contract("power fuel definitions exceed bound")
		}
		for _, def := range s.AllowedFuelDefs {
			if validID(def) != nil {
				return contract("invalid power fuel definition")
			}
		}
		for _, value := range []*float64{s.Fuel, s.TargetFuel, row.StoredWattDays, row.CapacityWattDays} {
			if value != nil && (math.IsNaN(*value) || math.IsInf(*value, 0) || *value < 0 || *value > 1e12) {
				return contract("invalid power service quantity")
			}
		}
		if row.StoredWattDays != nil && row.CapacityWattDays != nil && row.GetStoredWattDays() > row.GetCapacityWattDays() {
			return contract("power storage exceeds capacity")
		}
		if b.Settings == nil || !proto.Equal(b.Settings, &o.BuildingSettings{Forbidden: b.Settings.Forbidden}) {
			return contract("unsupported power settings detail")
		}
		for _, value := range []*float64{row.BaseW, s.PowerOutputW} {
			if value != nil && (math.IsNaN(*value) || math.IsInf(*value, 0) || math.Abs(*value) > 1e12) {
				return contract("invalid power wattage")
			}
		}
		if s.PowerNetId != nil && (validID(s.GetPowerNetId()) != nil || s.Connected != nil && !s.GetConnected()) {
			return contract("invalid power network identity")
		}
	}
	networks := map[string]bool{}
	for _, net := range v.Networks {
		if net == nil || validID(net.GetId()) != nil || networks[net.GetId()] || !proto.Equal(net, &o.PowerNetwork{Id: net.Id, Producers: net.Producers, Consumers: net.Consumers, Batteries: net.Batteries, Transmitters: net.Transmitters, Connectors: net.Connectors, GenerationW: net.GenerationW, ConsumptionW: net.ConsumptionW, NetW: net.NetW, StoredWattDays: net.StoredWattDays, CapacityWattDays: net.CapacityWattDays, HasSource: net.HasSource, HasActiveSource: net.HasActiveSource, Completeness: net.Completeness}) {
			return contract("invalid power network")
		}
		networks[net.GetId()] = true
		for _, value := range []*float64{net.GenerationW, net.ConsumptionW, net.NetW, net.StoredWattDays, net.CapacityWattDays} {
			if value != nil && (math.IsNaN(*value) || math.IsInf(*value, 0) || math.Abs(*value) > 1e12) {
				return contract("invalid power network wattage")
			}
		}
		if net.GenerationW != nil && net.GetGenerationW() < 0 || net.ConsumptionW != nil && net.GetConsumptionW() < 0 || net.StoredWattDays != nil && net.GetStoredWattDays() < 0 || net.CapacityWattDays != nil && net.GetCapacityWattDays() < 0 {
			return contract("negative power network quantity")
		}
		if c := net.Completeness; c != nil && (c.Page == nil || !c.Page.GetComplete() || c.GetUnreadable() != 0 || c.GetMatched() != c.GetReturned()) {
			return contract("incomplete power network census")
		}
	}
	conduits := map[[2]int32]bool{}
	for _, row := range v.Furniture {
		if row == nil || !proto.Equal(row, &o.DevelopmentFurniture{Building: row.Building}) || !powerEntity(row.Building, identity, size) {
			return contract("invalid power conduit")
		}
		ref := row.Building
		key := [2]int32{ref.GetPosition().GetX(), ref.GetPosition().GetZ()}
		if ref.GetDefName() != "PowerConduit" || ref.Position == nil || seen[ref.GetId()] || conduits[key] {
			return contract("invalid or duplicate power conduit")
		}
		seen[ref.GetId()], conduits[key] = true, true
	}
	return nil
}

func powerEntity(ref *o.EntityRef, identity *c.Identity, size *o.MapSize) bool {
	return ref != nil && validID(ref.GetId()) == nil && ref.MapId != nil && ref.GetMapId() == identity.GetMapId() &&
		(ref.DefName == nil || validID(ref.GetDefName()) == nil) && (ref.Position == nil || colonyCell(ref.Position, size)) &&
		proto.Equal(ref, &o.EntityRef{Id: ref.Id, MapId: ref.MapId, DefName: ref.DefName, Position: ref.Position})
}

func validateColonyEnvironment(v *o.ColonyFactsSnapshot) error {
	if len(v.Environment) > 256 {
		return contract("environment census exceeds bound")
	}
	seen := map[string]bool{}
	for _, row := range v.Environment {
		if row == nil || validID(row.GetId()) != nil || validID(row.GetDefName()) != nil || seen[row.GetId()] {
			return contract("invalid environment condition")
		}
		// Native fills implementation (the GameCondition type name), label,
		// permanent and, for a timed condition, ticks_left >= 0 (#362).
		if (row.Implementation != nil && validID(row.GetImplementation()) != nil) || !diagnostic(row.Label) {
			return contract("invalid environment condition")
		}
		if row.TicksLeft != nil && (row.GetTicksLeft() < 0 || row.GetPermanent()) {
			return contract("invalid environment condition duration")
		}
		if !proto.Equal(row, &o.EnvironmentCondition{Id: row.Id, DefName: row.DefName, Implementation: row.Implementation, Label: row.Label, Permanent: row.Permanent, TicksLeft: row.TicksLeft}) {
			return contract("invalid environment condition")
		}
		seen[row.GetId()] = true
	}
	for _, issue := range v.Issues {
		if issue.GetField() == "environment" && len(v.Environment) > 0 {
			return contract("unavailable environment contains observations")
		}
	}
	return nil
}
