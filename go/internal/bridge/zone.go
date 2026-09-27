package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

func ZoneConfiguration(zone domain.ZoneCreate) *op.CreateZone {
	cells := &op.CellList{}
	for _, cell := range zone.Cells() {
		cells.Cells = append(cells.Cells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
	}
	command := &op.CreateZone{Label: proto.String(zone.Label()), Cells: &op.Cells{Selection: &op.Cells_ExplicitCells{ExplicitCells: cells}}}
	switch zone.Kind() {
	case domain.FishingZone:
		command.Type = op.ZoneType_ZONE_TYPE_FISHING.Enum()
		command.Fishing = &op.FishingSettings{PopulationFloor: proto.Float64(domain.FishingPopulationFloor)}
		if zone.ExtendZoneID() != "" {
			command.ExtendZoneId = proto.String(zone.ExtendZoneID())
		}
	case domain.StockpileZone:
		command.Type = op.ZoneType_ZONE_TYPE_STOCKPILE.Enum()
		command.Stockpile = stockpileSettings(zone)
	default:
		command.Type = op.ZoneType_ZONE_TYPE_GROWING.Enum()
		command.Growing = &op.GrowingSettings{PlantDef: proto.String(zone.Crop()), AllowSow: proto.Bool(true), AllowCut: proto.Bool(true)}
	}
	return command
}
func stockpileSettings(zone domain.ZoneCreate) *op.StockpileSettings {
	return StockpileSettings(zone.Filter(), zone.Priority())
}

// StockpileSettings is the native StockpileSettings for a filter and
// priority: the filter's base is the preset, and a FilterPatch is sent only
// when the filter has selectors or ranges.
func StockpileSettings(filter domain.StockpileFilter, priority domain.StockpilePriority) *op.StockpileSettings {
	var p op.StoragePriority
	switch priority {
	case domain.CriticalPriority:
		p = op.StoragePriority_STORAGE_PRIORITY_CRITICAL
	case domain.ImportantPriority:
		p = op.StoragePriority_STORAGE_PRIORITY_IMPORTANT
	case domain.PreferredPriority:
		p = op.StoragePriority_STORAGE_PRIORITY_PREFERRED
	case domain.NormalPriority:
		p = op.StoragePriority_STORAGE_PRIORITY_NORMAL
	case domain.LowPriority:
		p = op.StoragePriority_STORAGE_PRIORITY_LOW
	}
	var preset op.FilterPreset
	switch filter.Base() {
	case domain.BaseEverything:
		preset = op.FilterPreset_FILTER_PRESET_EVERYTHING
	case domain.BaseNothing:
		preset = op.FilterPreset_FILTER_PRESET_NOTHING
	case domain.BaseFood:
		preset = op.FilterPreset_FILTER_PRESET_FOOD
	case domain.BasePerishables:
		preset = op.FilterPreset_FILTER_PRESET_PERISHABLES
	case domain.BaseNonperishables:
		preset = op.FilterPreset_FILTER_PRESET_NONPERISHABLES
	case domain.BaseOutdoorSafe:
		preset = op.FilterPreset_FILTER_PRESET_OUTDOOR_SAFE
	}
	settings := &op.StockpileSettings{Priority: p.Enum(), Preset: preset.Enum()}
	patch := &op.FilterPatch{Allow: filterSelectors(filter.Allow()), Disallow: filterSelectors(filter.Disallow())}
	if lo, hi, ok := filter.HitPoints(); ok {
		patch.HitPointsMin, patch.HitPointsMax = proto.Float64(lo), proto.Float64(hi)
	}
	if lo, hi, ok := filter.Quality(); ok {
		patch.QualityMin, patch.QualityMax = proto.String(string(lo)), proto.String(string(hi))
	}
	if len(patch.Allow) != 0 || len(patch.Disallow) != 0 || patch.HitPointsMin != nil || patch.QualityMin != nil {
		settings.Filter = patch
	}
	return settings
}

func filterSelectors(rows []domain.FilterSelector) []*op.FilterSelector {
	var out []*op.FilterSelector
	for _, s := range rows {
		switch s.Kind {
		case domain.ThingDefSelector:
			out = append(out, &op.FilterSelector{Definition: &op.FilterSelector_ThingDef{ThingDef: s.Name}})
		case domain.CategoryDefSelector:
			out = append(out, &op.FilterSelector{Definition: &op.FilterSelector_CategoryDef{CategoryDef: s.Name}})
		case domain.SpecialFilterSelector:
			out = append(out, &op.FilterSelector{Definition: &op.FilterSelector_SpecialFilterDef{SpecialFilterDef: s.Name}})
		}
	}
	return out
}

// PreviewZone is a planner's siting preview of one zone; native checks the
// ground live, so no map census token rides along (#992).
func (client *Client) PreviewZone(ctx context.Context, identity *c.Identity, zone domain.ZoneCreate) (*op.PreviewReply, Result, error) {
	if _, err := domain.ReconstructZone(zone); ValidateIdentity(identity) != nil || err != nil {
		return nil, Result{}, contract("invalid zone preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: &op.Operation{Command: &op.Operation_CreateZone{CreateZone: ZoneConfiguration(zone)}}}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown zone preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	// Accepted false is native refusing the ground itself (a littered or
	// occupied cell), an evaluation the caller moves past to its next
	// candidate; a bad configuration arrives as a failure.
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || v.Preparation != nil || buildingContext(v.Context, identity, 0, false) != nil || v.Projected != nil {
		return nil, raw, contract("invalid zone preview evidence")
	}
	return reply, raw, nil
}

// zoneCreateAction is the Actions/Apply create_zone arm of one zone_create:
// the configuration without a census token; native checks the ground live.
func zoneCreateAction(action domain.Action) (*op.Action, error) {
	zone, ok := action.ZoneCreate()
	if !ok {
		return nil, contract("not a zone create action")
	}
	if _, err := domain.ReconstructZone(zone); err != nil {
		return nil, contract("invalid zone create")
	}
	return &op.Action{Intent: &op.Action_CreateZone{CreateZone: ZoneConfiguration(zone)}}, nil
}

// zoneDeleteAction is the Actions/Apply delete_zone arm of one zone_delete.
func zoneDeleteAction(action domain.Action) (*op.Action, error) {
	del, ok := action.ZoneDelete()
	if !ok {
		return nil, contract("not a zone delete action")
	}
	if err := validID(del.Zone()); err != nil {
		return nil, err
	}
	return &op.Action{Intent: &op.Action_DeleteZone{DeleteZone: &op.DeleteZoneIntent{ZoneId: proto.String(del.Zone())}}}, nil
}

// zoneCellsAction is the Actions/Apply zone_cells arm of one zone_cell_edit.
func zoneCellsAction(action domain.Action) (*op.Action, error) {
	e, ok := action.ZoneCellEdit()
	if !ok {
		return nil, contract("not a zone cell edit action")
	}
	if _, err := domain.NewZoneCellEditAction(action.ID(), e); err != nil {
		return nil, contract("invalid zone cell edit")
	}
	mode := op.CellEdit_CELL_EDIT_ADD
	if e.Mode() == domain.RemoveZoneCells {
		mode = op.CellEdit_CELL_EDIT_REMOVE
	}
	var cells []*c.Cell
	for _, cell := range e.Cells() {
		cells = append(cells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
	}
	return &op.Action{Intent: &op.Action_ZoneCells{ZoneCells: &op.ZoneCellsIntent{
		ZoneId: proto.String(e.Zone()), Edit: mode.Enum(),
		Cells: &op.Cells{Selection: &op.Cells_ExplicitCells{ExplicitCells: &op.CellList{Cells: cells}}},
	}}}, nil
}

// stockpileAction is the Actions/Apply stockpile arm of one stockpile_patch,
// on a stockpile zone or a storage building alike.
func stockpileAction(action domain.Action) (*op.Action, error) {
	p, ok := action.StockpilePatch()
	if !ok {
		return nil, contract("not a stockpile patch action")
	}
	if _, err := domain.NewStockpilePatchAction(action.ID(), p); err != nil {
		return nil, contract("invalid stockpile patch")
	}
	return &op.Action{Intent: &op.Action_Stockpile{Stockpile: &op.StockpileIntent{
		TargetId: proto.String(p.Target()), Settings: StockpileSettings(p.Filter(), p.Priority()),
	}}}, nil
}
