package bridge

import (
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func validateColonyPower(v *o.DevelopmentFacts, identity *c.Identity, size *o.MapSize) error {
	if v == nil || !proto.Equal(v, &o.DevelopmentFacts{Power: v.Power, Furniture: v.Furniture, Networks: v.Networks, ShortCircuitTick: v.ShortCircuitTick, Geysers: v.Geysers}) {
		return contract("unsupported development facts")
	}
	if v.ShortCircuitTick != nil && v.GetShortCircuitTick() < 0 {
		return contract("invalid short circuit tick")
	}
	seen := map[string]bool{}
	for _, row := range v.Power {
		if row == nil || row.Building == nil {
			return contract("missing power building")
		}
		ref := row.Building
		if !validRef(ref) || seen[ref.GetId()] {
			return contract("invalid power building identity")
		}
		seen[ref.GetId()] = true
		for _, value := range []*float64{row.StoredWattDays, row.CapacityWattDays} {
			if value != nil && (math.IsNaN(*value) || math.IsInf(*value, 0) || *value < 0 || *value > 1e12) {
				return contract("invalid power storage quantity")
			}
		}
		if row.StoredWattDays != nil && row.CapacityWattDays != nil && row.GetStoredWattDays() > row.GetCapacityWattDays() {
			return contract("power storage exceeds capacity")
		}
		if value := row.BaseW; value != nil && (math.IsNaN(*value) || math.IsInf(*value, 0) || math.Abs(*value) > 1e12) {
			return contract("invalid power wattage")
		}
	}
	networks := map[string]bool{}
	for _, net := range v.Networks {
		if net == nil || validID(net.GetId()) != nil || networks[net.GetId()] || !proto.Equal(net, &o.PowerNetwork{Id: net.Id, Producers: net.Producers, Consumers: net.Consumers, Batteries: net.Batteries, Transmitters: net.Transmitters, Connectors: net.Connectors, GenerationW: net.GenerationW, ConsumptionW: net.ConsumptionW, NetW: net.NetW, StoredWattDays: net.StoredWattDays, CapacityWattDays: net.CapacityWattDays, HasSource: net.HasSource, HasActiveSource: net.HasActiveSource}) {
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
	}
	geysers := map[string]bool{}
	for _, row := range v.Geysers {
		ref := row.GetGeyser()
		if row == nil || !proto.Equal(row, &o.SteamGeyser{Geyser: row.Geyser, Cells: row.Cells, Occupied: row.Occupied}) || !validRef(ref) || row.Occupied == nil || geysers[ref.GetId()] {
			return contract("invalid steam geyser")
		}
		geysers[ref.GetId()] = true
		if len(row.Cells) < 1 {
			return contract("invalid steam geyser footprint")
		}
		cells := map[[2]int32]bool{}
		for _, cell := range row.Cells {
			key := [2]int32{cell.GetX(), cell.GetZ()}
			if !colonyCell(cell, size) || cells[key] {
				return contract("invalid steam geyser footprint")
			}
			cells[key] = true
		}
	}
	for _, row := range v.Furniture {
		if row == nil || !proto.Equal(row, &o.DevelopmentFurniture{Building: row.Building}) || !validRef(row.Building) || seen[row.Building.GetId()] {
			return contract("invalid or duplicate power conduit")
		}
		seen[row.Building.GetId()] = true
	}
	return nil
}

func validateColonyEnvironment(v *o.ColonyFactsSnapshot) error {
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
