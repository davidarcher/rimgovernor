package bridge

import (
	"context"
	"fmt"
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func (client *Client) ReadColonyFacts(ctx context.Context, identity *c.Identity, planning bool, definitions []string) (*o.ColonyFactsReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if len(definitions) > 256 || !planning && len(definitions) != 0 {
		return nil, Result{}, contract("invalid colony definition selection")
	}
	seen := map[string]bool{}
	for _, name := range definitions {
		if validID(name) != nil || seen[name] {
			return nil, Result{}, contract("invalid or duplicate colony definition")
		}
		seen[name] = true
	}
	request := &o.ColonyFactsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Planning: proto.Bool(planning), RequestedDefinitionNames: append([]string(nil), definitions...), Page: &c.PageRequest{Limit: proto.Uint32(256)}}
	reply := &o.ColonyFactsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_colony_facts", request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ColonyFactsReply_Failure:
		err = failure(v.Failure, raw)
	case *o.ColonyFactsReply_Unavailable:
		err = unavailable(v.Unavailable, raw)
	case *o.ColonyFactsReply_Observed:
		err = ValidateColonyFacts(v.Observed, request.Scope.ExpectedIdentity)
		if err == nil && v.Observed.GetPlanning().GetObserved() != nil {
			if !planning {
				err = contract("unrequested planning facts")
			}
			if len(seen) > 0 {
				rows := v.Observed.GetPlanning().GetObserved().Definitions
				if len(rows) != len(seen) {
					err = contract("missing requested definition")
				}
				for _, row := range rows {
					if !seen[row.GetDefinition().GetDefName()] {
						err = contract("unrequested definition")
					}
				}
			}
		}
	default:
		err = contract("missing colony outcome")
	}
	return reply, raw, err
}

func colonyCounts(v *o.Completeness, count, limit int) error {
	if count > limit || v == nil || v.Page == nil || v.Page.Complete == nil || !v.Page.GetComplete() || v.Page.GetNextCursor() != "" || v.Matched == nil || v.Returned == nil || v.Filtered == nil || v.Unreadable == nil || v.GetMatched() != uint64(count) || v.GetReturned() != uint64(count) || v.GetUnreadable() != 0 || v.GetFiltered() > math.MaxUint64-uint64(count) {
		return contract("incomplete colony census")
	}
	return nil
}
func colonyQuantities(rows []*o.Quantity) error {
	if len(rows) > 256 {
		return contract("colony quantities exceed bound")
	}
	seen := map[string]bool{}
	for _, row := range rows {
		if row == nil || validID(row.GetDefName()) != nil || seen[row.GetDefName()] || row.Units != nil && row.GetUnits() < 0 {
			return contract("invalid colony quantity")
		}
		seen[row.GetDefName()] = true
	}
	return nil
}
func colonySize(v *o.MapSize) bool {
	return v != nil && v.Width != nil && v.Height != nil && v.GetWidth() > 0 && v.GetHeight() > 0 && v.GetWidth() <= 4096 && v.GetHeight() <= 4096
}
func colonyCell(v *c.Cell, size *o.MapSize) bool {
	return v != nil && v.X != nil && v.Z != nil && v.GetX() >= 0 && v.GetZ() >= 0 && uint32(v.GetX()) < size.GetWidth() && uint32(v.GetZ()) < size.GetHeight()
}

// ValidateColonyFacts validates the implemented core and planning projection.
// Unported independent sections must remain explicitly unavailable.
func ValidateColonyFacts(v *o.ColonyFactsSnapshot, identity *c.Identity) error {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) || !colonySize(v.MapSize) || !colonyCell(v.Center, v.MapSize) {
		return contract("invalid colony context/geometry")
	}
	if err := buildingUnknown(v); err != nil {
		return err
	}
	if err := colonyCounts(v.Completeness, 1, 1); err != nil {
		return err
	}
	if v.Completeness.GetFiltered() != 0 {
		return contract("filtered colony core")
	}
	if err := pawnsIssues(v.Issues, v.ProtoReflect()); err != nil {
		return err
	}
	if err := colonyQuantities(v.Resources); err != nil {
		return err
	}
	for _, value := range []*uint32{v.ColonistCount, v.WorkerCount, v.BedCapacity, v.IndoorSleepingCapacity} {
		if value != nil && *value > 65536 {
			return contract("colony count exceeds bound")
		}
	}
	if v.WorkerCount != nil && v.ColonistCount != nil && v.GetWorkerCount() > v.GetColonistCount() || v.IndoorSleepingCapacity != nil && v.BedCapacity != nil && v.GetIndoorSleepingCapacity() > v.GetBedCapacity() {
		return contract("inconsistent colony capacity")
	}
	for _, value := range []*float64{v.FoodNutrition, v.NutritionPerDay, v.FoodRunwayDays, v.PendingFoodNutrition, v.PendingWoodUnits} {
		if !combatNumber(value, true) {
			return contract("invalid colony nutrition")
		}
	}
	for _, value := range []*float64{v.SleepingTemperatureMinC, v.SleepingTemperatureMaxC, v.OutdoorTemperatureC} {
		if !combatNumber(value, false) {
			return contract("invalid colony temperature")
		}
	}
	if v.SleepingTemperatureMinC != nil && v.SleepingTemperatureMaxC != nil && v.GetSleepingTemperatureMinC() > v.GetSleepingTemperatureMaxC() {
		return contract("inverted sleeping temperature range")
	}
	if v.Biome != nil && validID(v.GetBiome()) != nil {
		return contract("invalid biome")
	}
	if len(v.ForbiddenSupplies) > 256 {
		return contract("forbidden supplies exceed bound")
	}
	seen := map[[2]int32]bool{}
	for _, cell := range v.ForbiddenSupplies {
		key := [2]int32{cell.GetX(), cell.GetZ()}
		if !colonyCell(cell, v.MapSize) || seen[key] {
			return contract("invalid forbidden supply cell")
		}
		seen[key] = true
	}
	if v.Naming != nil {
		if v.Naming.WindowId == nil || v.Naming.GetWindowId() < 0 || !proto.Equal(v.Naming, &o.ColonyNaming{WindowId: v.Naming.WindowId}) {
			return contract("invalid naming window census")
		}
		for _, issue := range v.Issues {
			if issue.GetField() == "naming" {
				return contract("unavailable naming window contains observation")
			}
		}
	}
	if len(v.PolicyResources) != 0 || len(v.FoodCorpses) != 0 || v.Waste != nil {
		return contract("unreviewed colony section")
	}
	if climate := v.FoodClimate; climate != nil {
		if err := pawnsIssues(climate.Issues, climate.ProtoReflect()); err != nil {
			return err
		}
		for _, number := range []*float64{climate.GrowingDays, climate.GrowingDaysRemaining} {
			if !combatNumber(number, true) || number != nil && *number > 60 {
				return contract("invalid seasonal crop budget")
			}
		}
		for _, issue := range v.Issues {
			if issue.GetField() == "food_climate" {
				return contract("unavailable climate contains observations")
			}
		}
	}
	if err := validateColonyAcquisition(v); err != nil {
		return err
	}
	if err := validateColonyEnvironment(v); err != nil {
		return err
	}
	if err := validateColonyRecovery(v); err != nil {
		return err
	}
	if err := validateColonyProduction(v); err != nil {
		return err
	}
	if food := v.GetFoodSupply().GetObserved(); food != nil {
		if err := ValidateFoodSupply(food); err != nil {
			return err
		}
	} else if err := validateUnavailable(v.GetFoodSupply().GetUnavailable()); err != nil {
		return err
	}
	if forecast := v.GetForecast().GetObserved(); forecast != nil {
		if err := ValidateForecast(forecast, v.GetFoodSupply().GetObserved()); err != nil {
			return err
		}
	} else if err := validateUnavailable(v.GetForecast().GetUnavailable()); err != nil {
		return err
	}
	if upkeep := v.GetUpkeep().GetObserved(); upkeep != nil {
		if err := validateDirectUpkeep(upkeep, v.MapSize, v.Context.Identity.GetMapId()); err != nil {
			return err
		}
		if err := validateColonyUpkeep(upkeep, v.MapSize); err != nil {
			return err
		}
	} else if err := validateUnavailable(v.GetUpkeep().GetUnavailable()); err != nil {
		return err
	}
	if power := v.GetDevelopment().GetObserved(); power != nil {
		if err := validateColonyPower(power, identity, v.MapSize); err != nil {
			return err
		}
	} else if err := validateUnavailable(v.GetDevelopment().GetUnavailable()); err != nil {
		return err
	}
	if v.Planning == nil {
		return contract("missing planning availability")
	}
	switch p := v.Planning.Outcome.(type) {
	case *o.PlanningSection_Unavailable:
		return validateUnavailable(p.Unavailable)
	case *o.PlanningSection_Observed:
		return validateColonyPlanning(p.Observed, v.Context, v.MapSize)
	default:
		return contract("missing planning outcome")
	}
}

func validateColonyPlanning(p *o.PlanningFacts, ctx *c.ObservationContext, size *o.MapSize) error {
	if p == nil {
		return contract("unsupported planning projection")
	}
	if err := pawnsIssues(p.Issues, p.ProtoReflect()); err != nil {
		return err
	}
	if snapshot := p.ZoneMapSnapshot; snapshot != nil {
		if !proto.Equal(snapshot.Context, ctx) || snapshot.GetEntityId() != fmt.Sprintf("map-%d", ctx.Identity.GetMapId()) || validID(snapshot.GetToken()) != nil {
			return contract("zone map snapshot mismatch")
		}
	}
	if p.Gear != nil {
		if err := validateColonyGear(p.Gear, ctx, size); err != nil {
			return err
		}
	}
	if err := colonyCounts(p.Completeness, len(p.Definitions), 256); err != nil {
		return err
	}
	seen := map[string]bool{}
	for _, d := range p.Definitions {
		if d == nil || d.Definition == nil || validID(d.Definition.GetDefName()) != nil || seen[d.Definition.GetDefName()] {
			return contract("invalid planning definition")
		}
		seen[d.Definition.GetDefName()] = true
		if err := pawnsIssues(d.Issues, d.ProtoReflect()); err != nil {
			return err
		}
		if err := colonyQuantities(d.Costs); err != nil {
			return err
		}
		if !presentationText(d.Definition.Label, 4096) || d.Stuff != nil && validID(d.GetStuff()) != nil || d.Size != nil && !colonySize(d.Size) || d.ConstructionSkill != nil && (d.GetConstructionSkill() < 0 || d.GetConstructionSkill() > 20) || len(d.ResearchPrerequisites) > 256 {
			return contract("invalid planning definition facts")
		}
		for _, name := range d.ResearchPrerequisites {
			if validID(name) != nil {
				return contract("invalid research prerequisite")
			}
		}
		for _, number := range []*float64{d.RestEffectiveness, d.GrowDays, d.FertilityMin, d.FertilitySensitivity, d.HarvestNutrition, d.NutritionDemandPerDay} {
			if !combatNumber(number, true) {
				return contract("invalid planning definition number")
			}
		}
	}
	v := p.Cells
	if v == nil || !proto.Equal(v.Context, ctx) || !proto.Equal(v.MapSize, size) || v.Region == nil || !colonyCell(v.Region.Minimum, size) || !colonyCell(v.Region.Maximum, size) || v.Region.Minimum.GetX() > v.Region.Maximum.GetX() || v.Region.Minimum.GetZ() > v.Region.Maximum.GetZ() {
		return contract("invalid planning cell scope")
	}
	if err := colonyCounts(v.Completeness, len(v.Cells), 4096); err != nil {
		return err
	}
	area := uint64(v.Region.Maximum.GetX()-v.Region.Minimum.GetX()+1) * uint64(v.Region.Maximum.GetZ()-v.Region.Minimum.GetZ()+1)
	if area > 4096 || uint64(len(v.Cells))+v.Completeness.GetFiltered() != area {
		return contract("planning region coverage mismatch")
	}
	seenCells := map[[2]int32]bool{}
	for _, row := range v.Cells {
		if row == nil || !colonyCell(row.Cell, size) {
			return contract("invalid planning cell")
		}
		key := [2]int32{row.Cell.GetX(), row.Cell.GetZ()}
		if seenCells[key] || key[0] < v.Region.Minimum.GetX() || key[0] > v.Region.Maximum.GetX() || key[1] < v.Region.Minimum.GetZ() || key[1] > v.Region.Maximum.GetZ() {
			return contract("duplicate or unselected planning cell")
		}
		seenCells[key] = true
		if err := pawnsIssues(row.Issues, row.ProtoReflect()); err != nil {
			return err
		}
		if !combatNumber(row.Fertility, true) || !combatNumber(row.TemperatureC, false) || len(row.Things) != 0 || len(row.AreaIds) != 0 || len(row.Designations) != 0 {
			return contract("invalid planning cell details")
		}
		for _, name := range []*string{row.Terrain, row.Roof, row.ZoneId, row.RoomId} {
			if name != nil && validID(*name) != nil {
				return contract("invalid planning cell identifier")
			}
		}
	}
	return nil
}
