package bridge

import (
	"context"
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
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
	request := colonyFactsRequest(identity, planning, definitions)
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

// colonyFactsRequest is the exact request ReadColonyFacts issues; the
// bundle seeds its colony_facts section under the planning form of it
// (planning, no definitions).
func colonyFactsRequest(identity *c.Identity, planning bool, definitions []string) *o.ColonyFactsRequest {
	return &o.ColonyFactsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Planning: proto.Bool(planning), RequestedDefinitionNames: append([]string(nil), definitions...), Page: &c.PageRequest{Limit: proto.Uint32(256)}}
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
	if v.PlayerTechLevel != nil && validID(v.GetPlayerTechLevel()) != nil {
		return contract("invalid player tech level")
	}
	if len(v.ForbiddenSupplies) > 256 {
		return contract("forbidden supplies exceed bound")
	}
	seen := map[string]bool{}
	for _, row := range v.ForbiddenSupplies {
		if row == nil || validID(row.GetId()) != nil || seen[row.GetId()] || validID(row.GetDefName()) != nil || row.MapId == nil || row.GetMapId() != v.Context.Identity.GetMapId() || !colonyCell(row.Position, v.MapSize) {
			return contract("invalid forbidden supply")
		}
		seen[row.GetId()] = true
	}
	if v.Naming != nil {
		if v.Naming.WindowId == nil || v.Naming.GetWindowId() < 0 || v.Naming.FactionName == nil || v.Naming.SettlementName == nil || len(v.Naming.Issues) != 0 ||
			validID(v.Naming.GetFactionName()) != nil || validID(v.Naming.GetSettlementName()) != nil || len(v.Naming.ProtoReflect().GetUnknown()) != 0 {
			return contract("invalid naming window census")
		}
		for _, issue := range v.Issues {
			if issue.GetField() == "naming" {
				return contract("unavailable naming window contains observation")
			}
		}
	}
	if err := validateChoiceDialog(v.Dialog); err != nil {
		return err
	}
	if len(v.PolicyResources) != 0 || len(v.FoodCorpses) != 0 {
		return contract("unreviewed colony section")
	}
	if err := validateColonyWaste(v.Waste, v.MapSize, v.Context.Identity.GetMapId()); err != nil {
		return err
	}
	if climate := v.FoodClimate; climate != nil {
		if err := pawnsIssues(climate.Issues, climate.ProtoReflect()); err != nil {
			return err
		}
		for _, number := range []*float64{climate.GrowingDays, climate.GrowingDaysRemaining, climate.GrowingDaysUntil, climate.NonGrowingDays} {
			if !combatNumber(number, true) || number != nil && *number > 60 {
				return contract("invalid seasonal crop budget")
			}
		}
		if climate.DayOfYear != nil && (*climate.DayOfYear < 0 || *climate.DayOfYear >= 60) || climate.Season != nil && !validSeason(*climate.Season) {
			return contract("invalid calendar")
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
	if err := validateColonyBlight(v); err != nil {
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
	if err := validateColonyThreat(v.Threat); err != nil {
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
		for i, number := range []*float64{d.RestEffectiveness, d.GrowDays, d.FertilityMin, d.FertilitySensitivity, d.HarvestNutrition, d.NutritionDemandPerDay, d.GrowMinGlow, d.GrowerFertility, d.GlowRadius} {
			if !combatNumber(number, true) {
				return contract("invalid planning definition number %d for %s", i, d.Definition.GetDefName())
			}
		}
		// A generator's native base draw is negative, as are the beauty and
		// cleanliness of natural ground.
		for _, number := range []*float64{d.PowerW, d.Cleanliness, d.Beauty} {
			if !combatNumber(number, false) {
				return contract("invalid planning definition number")
			}
		}
		if !combatNumber(d.Flammability, true) || d.PathCost != nil && (d.GetPathCost() < 0 || d.GetPathCost() > 10000) {
			return contract("invalid planning definition floor facts")
		}
		if len(d.SowTags) > 32 || d.SowTag != nil && validID(d.GetSowTag()) != nil {
			return contract("invalid planning definition sow tags")
		}
		for _, tag := range d.SowTags {
			if validID(tag) != nil {
				return contract("invalid planning definition sow tags")
			}
		}
	}
	if p.Environment != nil {
		if err := validateGrowingEnvironment(p.Environment, size); err != nil {
			return err
		}
	}
	// A native that serves the planning window through
	// observations_get_cells (ReadPlanningWindow, #356) carries no cells
	// here; an older one still lists them.
	if p.Cells != nil {
		return validatePlanningCells(p.Cells, ctx, size)
	}
	return nil
}

// validateGrowingEnvironment bounds the controlled-growing census: every row
// is identified, every cell lies on the map, every number is finite and the
// completeness row counts exactly the rows present.
func validateGrowingEnvironment(e *o.ControlledEnvironment, size *o.MapSize) error {
	if err := pawnsIssues(e.Issues, e.ProtoReflect()); err != nil {
		return err
	}
	if !combatNumber(e.OutdoorTemperatureC, false) {
		return contract("invalid environment temperature")
	}
	if err := colonyCounts(e.Completeness, len(e.Lights)+len(e.Growers)+len(e.Rooms)+len(e.Networks), 1024); err != nil {
		return err
	}
	optionalID := func(v *string) bool { return v == nil || validID(*v) == nil }
	entity := func(ref *o.EntityRef) bool {
		return ref != nil && validID(ref.GetId()) == nil && validID(ref.GetDefName()) == nil && colonyCell(ref.Position, size)
	}
	cells := func(rows []*c.Cell) bool {
		if len(rows) > 256 {
			return false
		}
		for _, row := range rows {
			if !colonyCell(row, size) {
				return false
			}
		}
		return true
	}
	seen := map[string]bool{}
	unique := func(id string) bool {
		if seen[id] {
			return false
		}
		seen[id] = true
		return true
	}
	for _, row := range e.Lights {
		if row == nil || !entity(row.Building) || !unique("light/"+row.Building.GetId()) || !optionalID(row.RoomId) || !optionalID(row.PowerNetId) || !combatNumber(row.PowerW, true) || !cells(row.GrowthCells) {
			return contract("invalid environment light")
		}
		if err := pawnsIssues(row.Issues, row.ProtoReflect()); err != nil {
			return err
		}
	}
	for _, row := range e.Growers {
		if row == nil || !entity(row.Building) || !unique("grower/"+row.Building.GetId()) || !optionalID(row.RoomId) || !optionalID(row.PowerNetId) || !optionalID(row.SowTag) || !optionalID(row.CropDefName) || !combatNumber(row.PowerW, true) || !combatNumber(row.Fertility, true) || !cells(row.PlantCells) {
			return contract("invalid environment grower")
		}
		if err := pawnsIssues(row.Issues, row.ProtoReflect()); err != nil {
			return err
		}
	}
	for _, row := range e.Rooms {
		if row == nil || validID(row.GetRoomId()) != nil || !unique("room/"+row.GetRoomId()) || !combatNumber(row.TemperatureC, false) || row.LitCells != nil && row.CellCount != nil && row.GetLitCells() > row.GetCellCount() {
			return contract("invalid environment room")
		}
	}
	for _, row := range e.Networks {
		if row == nil || validID(row.GetId()) != nil || !unique("network/"+row.GetId()) {
			return contract("invalid environment network")
		}
		for _, number := range []*float64{row.GenerationW, row.SolarW, row.WindW, row.ConsumptionW, row.StoredWattDays, row.CapacityWattDays} {
			if !combatNumber(number, true) {
				return contract("invalid environment network")
			}
		}
	}
	return nil
}

// validSeason accepts the native Season enum names (RimWorld.Season).
func validSeason(name string) bool {
	switch name {
	case "Undefined", "Spring", "Summer", "Fall", "Winter", "PermanentSummer", "PermanentWinter":
		return true
	}
	return false
}

// validateColonyThreat accepts an absent threat section (a native build
// without #395 reports nothing, and the projection stays unknown), an
// unavailable one, or observed facts whose every number is finite and
// non-negative and whose completeness is the single colony row.
func validateColonyThreat(v *o.ThreatSection) error {
	if v == nil {
		return nil
	}
	switch t := v.Outcome.(type) {
	case *o.ThreatSection_Unavailable:
		return validateUnavailable(t.Unavailable)
	case *o.ThreatSection_Observed:
		facts := t.Observed
		if facts == nil {
			return contract("missing threat facts")
		}
		if err := colonyCounts(facts.Completeness, 1, 1); err != nil {
			return err
		}
		if err := pawnsIssues(facts.Issues, facts.ProtoReflect()); err != nil {
			return err
		}
		for _, value := range []*float64{facts.WealthItems, facts.WealthBuildings, facts.WealthPawns, facts.WealthTotal, facts.StorytellerWealth, facts.RaidPoints, facts.AdaptationFactor, facts.DifficultyThreatScale} {
			if !combatNumber(value, true) {
				return contract("invalid colony threat number")
			}
		}
		if facts.ColonistCount != nil && facts.GetColonistCount() > 65536 {
			return contract("colony count exceeds bound")
		}
		return nil
	default:
		return contract("missing threat outcome")
	}
}

// ColonyThreat is the threat section of one colony census as facts: the
// wealth split and the raid points the storyteller would draw now (#395).
// A section native did not observe (absent, unavailable, or a field it
// withheld) leaves the fact unknown, never zero.
type ColonyThreat struct {
	RaidPoints, WealthTotal, WealthItems, WealthBuildings, WealthPawns domain.Fact[float64]
	StorytellerWealth, AdaptationFactor, DifficultyThreatScale         domain.Fact[float64]
}

// ProjectColonyThreat reads the threat section of a validated census.
func ProjectColonyThreat(v *o.ColonyFactsSnapshot) ColonyThreat {
	facts := v.GetThreat().GetObserved()
	if facts == nil {
		return ColonyThreat{}
	}
	number := func(p *float64) domain.Fact[float64] {
		if p == nil || math.IsNaN(*p) || math.IsInf(*p, 0) {
			return domain.Unknown[float64]()
		}
		return domain.Known(*p)
	}
	return ColonyThreat{
		RaidPoints: number(facts.RaidPoints), WealthTotal: number(facts.WealthTotal), WealthItems: number(facts.WealthItems),
		WealthBuildings: number(facts.WealthBuildings), WealthPawns: number(facts.WealthPawns), StorytellerWealth: number(facts.StorytellerWealth),
		AdaptationFactor: number(facts.AdaptationFactor), DifficultyThreatScale: number(facts.DifficultyThreatScale),
	}
}
