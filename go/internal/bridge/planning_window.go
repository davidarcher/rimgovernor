package bridge

import (
	"context"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// PlanningWindowRadius is the planning window's reach around the colony
// centre: the window is centre +/- radius clipped to the map, the same
// rect the native colony facts read carried as planning.cells.
const PlanningWindowRadius int32 = 22

// planningWindowPage is the most cells one observations_get_cells page
// carries for a rectangle selection; a larger window is read in row bands.
const planningWindowPage = 4096

// PlanningWindowRect is the planning window around center on a map of
// bounds: centre +/- PlanningWindowRadius, clipped to the map.
func PlanningWindowRect(center domain.Cell, bounds policy.Bounds) policy.Rectangle {
	minX, minZ := max(center.X-PlanningWindowRadius, 0), max(center.Z-PlanningWindowRadius, 0)
	maxX, maxZ := min(center.X+PlanningWindowRadius, bounds.Width-1), min(center.Z+PlanningWindowRadius, bounds.Height-1)
	return policy.Rectangle{X: minX, Z: minZ, Width: maxX - minX + 1, Height: maxZ - minZ + 1}
}

// PlanningWindow is one observations_get_cells read of the planning
// window: the site cells inside Region, decoded as the colony facts'
// planning.cells rows are, with the tick the reply described. Fogged cells
// are never listed; Filtered counts them.
type PlanningWindow struct {
	Context  *c.ObservationContext
	Region   policy.Rectangle
	Cells    []policy.SiteCell
	Filtered uint64
}

// planningWindowFields is the field selection the window reads: roof,
// visibility, traversal, zone, room and growth; never terrain, areas,
// things or designations.
func planningWindowFields() *o.CellFields {
	return &o.CellFields{Terrain: proto.Bool(false), Roof: proto.Bool(true), Visibility: proto.Bool(true), Traversal: proto.Bool(true), Zone: proto.Bool(true), Areas: proto.Bool(false), Things: proto.Bool(false), Designations: proto.Bool(false), Room: proto.Bool(true), Growth: proto.Bool(true)}
}

// ReadPlanningWindow reads the planning window rect through
// observations_get_cells, one page per band of at most planningWindowPage
// cells, and returns every band's rows under one region. A native that
// does not serve the planning fields refuses the request (ErrRefused);
// the caller then falls back to whatever window it holds.
func (client *Client) ReadPlanningWindow(ctx context.Context, identity *c.Identity, rect policy.Rectangle) (PlanningWindow, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return PlanningWindow{}, Result{}, err
	}
	if rect.X < 0 || rect.Z < 0 || rect.Width < 1 || rect.Height < 1 {
		return PlanningWindow{}, Result{}, contract("invalid planning window rect")
	}
	rows := max(planningWindowPage/int(rect.Width), 1)
	out := PlanningWindow{Region: rect}
	var last Result
	for z := rect.Z; z < rect.Z+rect.Height; z += int32(rows) {
		band := policy.Rectangle{X: rect.X, Z: z, Width: rect.Width, Height: min(int32(rows), rect.Z+rect.Height-z)}
		snapshot, raw, err := client.readPlanningBand(ctx, identity, band)
		last = raw
		if err != nil {
			return PlanningWindow{}, raw, err
		}
		if out.Context != nil && !proto.Equal(out.Context, snapshot.Context) {
			return PlanningWindow{}, raw, contract("planning window bands differ in context")
		}
		out.Context = snapshot.Context
		cells, filtered := PlanningCells(snapshot)
		out.Cells = append(out.Cells, cells...)
		out.Filtered += filtered
	}
	sort.Slice(out.Cells, func(i, j int) bool {
		a, b := out.Cells[i].Cell, out.Cells[j].Cell
		if a.Z != b.Z {
			return a.Z < b.Z
		}
		return a.X < b.X
	})
	return out, last, nil
}

func (client *Client) readPlanningBand(ctx context.Context, identity *c.Identity, band policy.Rectangle) (*o.CellsSnapshot, Result, error) {
	region := &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(band.X), Z: proto.Int32(band.Z)}, Maximum: &c.Cell{X: proto.Int32(band.X + band.Width - 1), Z: proto.Int32(band.Z + band.Height - 1)}}
	request := &o.GetCellsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Selection: &o.GetCellsRequest_Rectangle{Rectangle: region}, Fields: planningWindowFields(), Page: &c.PageRequest{Limit: proto.Uint32(uint32(band.Width) * uint32(band.Height))}}
	reply := &o.GetCellsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_get_cells", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch value := reply.Outcome.(type) {
	case *o.GetCellsReply_Failure:
		return nil, raw, failure(value.Failure, raw)
	case *o.GetCellsReply_Unavailable:
		return nil, raw, unavailable(value.Unavailable, raw)
	case *o.GetCellsReply_Observed:
		snapshot := value.Observed
		if snapshot == nil {
			return nil, raw, contract("missing cells snapshot")
		}
		if err := ValidateContext(snapshot.Context); err != nil {
			return nil, raw, err
		}
		if !sameIdentity(snapshot.Context.Identity, identity) {
			return nil, raw, contract("planning window identity mismatch")
		}
		if !colonySize(snapshot.MapSize) || !proto.Equal(snapshot.Region, region) {
			return nil, raw, contract("planning window region differs")
		}
		if !proto.Equal(snapshot.AppliedFields, planningWindowFields()) {
			return nil, raw, contract("planning window applied fields differ")
		}
		if err := validatePlanningCells(snapshot, snapshot.Context, snapshot.MapSize); err != nil {
			return nil, raw, err
		}
		if ref := snapshot.MapSnapshot; ref != nil {
			if err := ValidateContext(ref.Context); err != nil {
				return nil, raw, err
			}
			if !proto.Equal(ref.Context, snapshot.Context) || validID(ref.GetEntityId()) != nil || validID(ref.GetToken()) != nil {
				return nil, raw, contract("map snapshot mismatch")
			}
		}
		return snapshot, raw, nil
	default:
		return nil, raw, contract("missing planning window outcome")
	}
}

// validatePlanningCells bounds one planning-cell snapshot, the colony
// facts' planning.cells or a planning window page: a region on the map of
// at most planningWindowPage cells, every listed cell unique and inside it,
// listed plus filtered cells covering the region exactly, and only the
// planning fields on each row.
func validatePlanningCells(v *o.CellsSnapshot, ctx *c.ObservationContext, size *o.MapSize) error {
	if v == nil || !proto.Equal(v.Context, ctx) || !proto.Equal(v.MapSize, size) || v.Region == nil || !colonyCell(v.Region.Minimum, size) || !colonyCell(v.Region.Maximum, size) || v.Region.Minimum.GetX() > v.Region.Maximum.GetX() || v.Region.Minimum.GetZ() > v.Region.Maximum.GetZ() {
		return contract("invalid planning cell scope")
	}
	if err := colonyCounts(v.Completeness, len(v.Cells), planningWindowPage); err != nil {
		return err
	}
	area := uint64(v.Region.Maximum.GetX()-v.Region.Minimum.GetX()+1) * uint64(v.Region.Maximum.GetZ()-v.Region.Minimum.GetZ()+1)
	if area > planningWindowPage || uint64(len(v.Cells))+v.Completeness.GetFiltered() != area {
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

// PlanningCells decodes a validated planning-cell snapshot into site cells
// and the count of cells it omits: fogged rows are skipped, since a fogged
// cell is not evidence that a site is safe, and an emitter that filters
// them out of the listing says so in Completeness.filtered. Roof and zone
// presence follow the applied fields: a declared field with no value is a
// known absence.
func PlanningCells(v *o.CellsSnapshot) ([]policy.SiteCell, uint64) {
	applied := v.GetAppliedFields()
	filtered := v.GetCompleteness().GetFiltered()
	cells := make([]policy.SiteCell, 0, len(v.GetCells()))
	for _, row := range v.GetCells() {
		if row.GetFogged() {
			filtered++
			continue
		}
		cells = append(cells, policy.SiteCell{Cell: domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()}, Walkable: cellFact(row.Walkable), Occupied: cellFact(row.Occupied), Zone: CellPresence(row.ZoneId, row.Issues, "zone_id", applied.GetZone()), Roofed: CellPresence(row.Roof, row.Issues, "roof", applied.GetRoof()), Roof: cellFact(row.Roof), Indoors: cellFact(row.Indoors), SupportsLight: cellFact(row.SupportsLight), Doorway: cellFact(row.Doorway), Fertility: cellFact(row.Fertility), StorageEmpty: cellFact(row.StorageEmpty), ZoneID: cellFact(row.ZoneId)})
	}
	return cells, filtered
}

func cellFact[T any](p *T) domain.Fact[T] {
	if p == nil {
		return domain.Unknown[T]()
	}
	return domain.Known(*p)
}

// CellPresence is whether a named cell feature is present: a value means
// present, a not-applicable issue or an applied field without a value
// means absent, and an unread field stays unknown.
func CellPresence(value *string, issues []*o.ReadIssue, field string, applied bool) domain.Fact[bool] {
	if value != nil {
		return domain.Known(true)
	}
	for _, issue := range issues {
		if issue.GetField() == field {
			if issue.GetUnavailable().GetReason() == c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE {
				return domain.Known(false)
			}
			return domain.Unknown[bool]()
		}
	}
	if applied {
		return domain.Known(false)
	}
	return domain.Unknown[bool]()
}
