package bridge

import (
	"context"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// Native bounds of the two defense-layout reads: one site census answers at
// most this many cells (the 1 MiB reply ceiling), and one lines-of-fire read
// takes at most this many cells on each side.
const (
	maxDefenseSiteCells  = 2048
	maxLinesOfFireCells  = 64
	maxDefenseSiteExtent = 4096
)

// CellRect is one inclusive cell rectangle, the unit both defense reads and
// their callers' tiling work in.
type CellRect struct{ Min, Max domain.Cell }

// Cells is the inclusive cell count, zero for an inverted rectangle.
func (r CellRect) Cells() int64 {
	if r.Max.X < r.Min.X || r.Max.Z < r.Min.Z {
		return 0
	}
	return int64(r.Max.X-r.Min.X+1) * int64(r.Max.Z-r.Min.Z+1)
}

// DefenseCell is one observed cell of a defense site census. Unknown (fogged)
// cells carry only Fogged=true; every other fact is the game's own: CoverFill
// and BlocksSight are what CoverUtility/GenSight consult, EdgeReachable is
// native ground reachability to a map edge without opening doors (the walk-in
// raid approach), and NaturalRock/Door/PlayerOwned describe the edifice.
type DefenseCell struct {
	Cell                            domain.Cell
	Fogged                          bool
	Terrain, EdificeDefName         string
	Walkable, Passable, BlocksSight bool
	CoverFill                       float64
	PlayerOwned, NaturalRock, Door  bool
	EdgeReachable, HomeArea         bool
	// Cover names the thing whose fill CoverFill reports, when the census
	// can identify one the game's designators could remove.
	Cover *DefenseCover
}

// DefenseCover is the clearance identity of one cover thing: what it is,
// which designation removes it, whether one already stands, and the snapshot
// token ClearCover compares at apply.
// Designation is the native designation def the cover's kind takes, the
// one a CoverClearance action carries; empty for an unknown kind.
func (c DefenseCover) Designation() string {
	switch c.Kind {
	case o.CoverKind_COVER_KIND_PLANT:
		return domain.CoverClearanceCutPlant
	case o.CoverKind_COVER_KIND_CHUNK:
		return domain.CoverClearanceHaul
	case o.CoverKind_COVER_KIND_MINEABLE:
		return domain.CoverClearanceMine
	case o.CoverKind_COVER_KIND_BUILDING:
		return domain.CoverClearanceDeconstruct
	}
	return ""
}

type DefenseCover struct {
	ThingID, DefName string
	Kind             o.CoverKind
	Token            string
	Designated       bool
}

// RaidTrack is one hostile lord the map has seen since load: its first
// pawn's spawn cell (Ground when on the map edge) and a bounded trail of one
// of its pawns, sampled by the native RaidArrivalState component.
type RaidTrack struct {
	LordID, FactionDef  string
	SpawnTick, LastTick domain.Tick
	Spawn               domain.Cell
	Ground              bool
	Trail               []domain.Cell
}

// DefenseSite is one complete rectangle census with the context the caller
// validates against its acting generation. CoverThreshold is the fill above
// which the game's shooting model grants a block chance.
type DefenseSite struct {
	Context        *c.ObservationContext
	Width, Height  uint32
	Region         CellRect
	Cells          []DefenseCell
	CoverThreshold float64
	Raids          []RaidTrack
}

const maxRaidTracks, maxRaidTrail = 32, 128

// LineOfFire is the native shooting-model evidence for one (firing, approach)
// pair: GenSight line of sight and CoverUtility's overall block chance for a
// target at To shot from From (TargetCover) and the reverse (ShooterCover).
// A fogged endpoint leaves Known false with only the distance set.
type LineOfFire struct {
	From, To                  domain.Cell
	Known, LineOfSight        bool
	TargetCover, ShooterCover float64
	Distance                  float64
}

type LinesOfFire struct {
	Context *c.ObservationContext
	Lines   []LineOfFire
}

// ReadDefenseSite reads one inclusive rectangle of at most 2048 cells. Callers
// tile larger regions; the read never samples or truncates.
func (client *Client) ReadDefenseSite(ctx context.Context, identity *c.Identity, region CellRect) (DefenseSite, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return DefenseSite{}, Result{}, err
	}
	if err := validateDefenseRegion(region); err != nil {
		return DefenseSite{}, Result{}, err
	}
	request := &o.DefenseSiteRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)},
		Region: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(region.Min.X), Z: proto.Int32(region.Min.Z)}, Maximum: &c.Cell{X: proto.Int32(region.Max.X), Z: proto.Int32(region.Max.Z)}}}
	reply := &o.DefenseSiteReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_defense_site", request, reply)
	if err != nil {
		return DefenseSite{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return DefenseSite{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.DefenseSiteReply_Failure:
		return DefenseSite{}, raw, failure(v.Failure, raw)
	case *o.DefenseSiteReply_Unavailable:
		return DefenseSite{}, raw, unavailable(v.Unavailable, raw)
	case *o.DefenseSiteReply_Observed:
		site, err := validateDefenseSite(v.Observed, identity, region)
		if err != nil {
			return DefenseSite{}, raw, err
		}
		return site, raw, nil
	default:
		return DefenseSite{}, raw, contract("defense site outcome missing")
	}
}

// ReadLinesOfFire reads every (firing, approach) pair for at most 64 unique
// cells on each side.
func (client *Client) ReadLinesOfFire(ctx context.Context, identity *c.Identity, firing, approach []domain.Cell) (LinesOfFire, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return LinesOfFire{}, Result{}, err
	}
	for _, cells := range [][]domain.Cell{firing, approach} {
		if err := validateLineCells(cells); err != nil {
			return LinesOfFire{}, Result{}, err
		}
	}
	request := &o.LinesOfFireRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
	for _, cell := range firing {
		request.FiringCells = append(request.FiringCells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
	}
	for _, cell := range approach {
		request.ApproachCells = append(request.ApproachCells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
	}
	reply := &o.LinesOfFireReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_lines_of_fire", request, reply)
	if err != nil {
		return LinesOfFire{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return LinesOfFire{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.LinesOfFireReply_Failure:
		return LinesOfFire{}, raw, failure(v.Failure, raw)
	case *o.LinesOfFireReply_Unavailable:
		return LinesOfFire{}, raw, unavailable(v.Unavailable, raw)
	case *o.LinesOfFireReply_Observed:
		lines, err := validateLinesOfFire(v.Observed, identity, firing, approach)
		if err != nil {
			return LinesOfFire{}, raw, err
		}
		return LinesOfFire{Context: v.Observed.Context, Lines: lines}, raw, nil
	default:
		return LinesOfFire{}, raw, contract("lines of fire outcome missing")
	}
}

func validateDefenseRegion(region CellRect) error {
	if region.Min.X < 0 || region.Min.Z < 0 || region.Max.X < region.Min.X || region.Max.Z < region.Min.Z || region.Max.X >= maxDefenseSiteExtent || region.Max.Z >= maxDefenseSiteExtent {
		return contract("invalid defense site region")
	}
	if region.Cells() > maxDefenseSiteCells {
		return contract("defense site region exceeds %d cells", maxDefenseSiteCells)
	}
	return nil
}
func validateLineCells(cells []domain.Cell) error {
	if len(cells) < 1 || len(cells) > maxLinesOfFireCells {
		return contract("lines of fire cells outside 1..%d", maxLinesOfFireCells)
	}
	seen := map[domain.Cell]bool{}
	for _, cell := range cells {
		if cell.X < 0 || cell.Z < 0 || seen[cell] {
			return contract("invalid or duplicate line of fire cell")
		}
		seen[cell] = true
	}
	return nil
}
func completeCount(counts *o.Completeness, n int) bool {
	return counts != nil && counts.Page != nil && counts.Page.Complete != nil && counts.Page.GetComplete() && counts.Matched != nil && counts.Returned != nil && counts.Unreadable != nil &&
		counts.GetUnreadable() == 0 && counts.GetReturned() == uint64(n) && counts.GetMatched() == uint64(n)
}
func protoCell(v *c.Cell) (domain.Cell, bool) {
	if v == nil || v.X == nil || v.Z == nil {
		return domain.Cell{}, false
	}
	return domain.Cell{X: v.GetX(), Z: v.GetZ()}, true
}
func fraction(v *float64) bool {
	return v != nil && !math.IsNaN(*v) && !math.IsInf(*v, 0) && *v >= 0 && *v <= 1
}

func validateDefenseSite(v *o.DefenseSiteSnapshot, identity *c.Identity, region CellRect) (DefenseSite, error) {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return DefenseSite{}, contract("invalid defense site context")
	}
	if v.MapSize == nil || v.MapSize.Width == nil || v.MapSize.Height == nil || v.GetMapSize().GetWidth() == 0 || v.GetMapSize().GetHeight() == 0 {
		return DefenseSite{}, contract("defense site map size missing")
	}
	min, mk := protoCell(v.GetRegion().GetMinimum())
	max, xk := protoCell(v.GetRegion().GetMaximum())
	if !mk || !xk || min != region.Min || max != region.Max {
		return DefenseSite{}, contract("defense site region changed")
	}
	want := int(region.Max.X-region.Min.X+1) * int(region.Max.Z-region.Min.Z+1)
	if len(v.Cells) != want || !completeCount(v.Completeness, want) {
		return DefenseSite{}, contract("incomplete defense site census")
	}
	site := DefenseSite{Context: v.Context, Width: v.MapSize.GetWidth(), Height: v.MapSize.GetHeight(), Region: region, Cells: make([]DefenseCell, 0, want)}
	seen := make(map[domain.Cell]bool, want)
	for _, row := range v.Cells {
		if row == nil {
			return DefenseSite{}, contract("missing defense cell")
		}
		cell, ok := protoCell(row.Cell)
		if !ok || seen[cell] || cell.X < region.Min.X || cell.X > region.Max.X || cell.Z < region.Min.Z || cell.Z > region.Max.Z || int64(cell.X) >= int64(site.Width) || int64(cell.Z) >= int64(site.Height) {
			return DefenseSite{}, contract("defense cell outside region or duplicated")
		}
		seen[cell] = true
		if row.Fogged == nil {
			return DefenseSite{}, contract("defense cell visibility missing")
		}
		out := DefenseCell{Cell: cell, Fogged: row.GetFogged()}
		if out.Fogged {
			// Unknown geometry carries no facts; a fogged cell claiming
			// traversal or cover would be an invented observation.
			if row.Walkable != nil || row.Passable != nil || row.CoverFill != nil || row.EdgeReachable != nil || row.EdificeDefName != nil {
				return DefenseSite{}, contract("fogged defense cell carries facts")
			}
			site.Cells = append(site.Cells, out)
			continue
		}
		if row.Terrain == nil || validID(row.GetTerrain()) != nil || row.Walkable == nil || row.Passable == nil || row.BlocksSight == nil || row.HomeArea == nil || row.EdgeReachable == nil || row.PlayerOwned == nil || row.NaturalRock == nil || row.Door == nil || !fraction(row.CoverFill) {
			return DefenseSite{}, contract("defense cell facts missing or invalid")
		}
		if row.EdificeDefName != nil && validID(row.GetEdificeDefName()) != nil {
			return DefenseSite{}, contract("invalid defense cell edifice")
		}
		if row.EdificeDefName == nil && (row.GetPlayerOwned() || row.GetNaturalRock() || row.GetDoor()) {
			return DefenseSite{}, contract("edifice facts without an edifice")
		}
		if row.GetWalkable() && !row.GetPassable() || row.GetEdgeReachable() && !row.GetPassable() {
			return DefenseSite{}, contract("contradictory defense cell traversal")
		}
		out.Terrain, out.EdificeDefName = row.GetTerrain(), row.GetEdificeDefName()
		out.Walkable, out.Passable, out.BlocksSight = row.GetWalkable(), row.GetPassable(), row.GetBlocksSight()
		out.CoverFill = row.GetCoverFill()
		out.PlayerOwned, out.NaturalRock, out.Door = row.GetPlayerOwned(), row.GetNaturalRock(), row.GetDoor()
		out.EdgeReachable, out.HomeArea = row.GetEdgeReachable(), row.GetHomeArea()
		if row.CoverThingId != nil || row.CoverDefName != nil || row.CoverKind != nil || row.CoverToken != nil || row.CoverDesignated != nil {
			if validID(row.GetCoverThingId()) != nil || validID(row.GetCoverDefName()) != nil || validID(row.GetCoverToken()) != nil || row.CoverDesignated == nil || row.GetCoverKind() == o.CoverKind_COVER_KIND_UNSPECIFIED || row.GetCoverFill() <= 0 {
				return DefenseSite{}, contract("defense cell cover identity incomplete")
			}
			out.Cover = &DefenseCover{ThingID: row.GetCoverThingId(), DefName: row.GetCoverDefName(), Kind: row.GetCoverKind(), Token: row.GetCoverToken(), Designated: row.GetCoverDesignated()}
		}
		site.Cells = append(site.Cells, out)
	}
	if v.CoverThreshold == nil || !fraction(v.CoverThreshold) || v.GetCoverThreshold() >= 1 {
		return DefenseSite{}, contract("defense site cover threshold missing")
	}
	site.CoverThreshold = v.GetCoverThreshold()
	if len(v.Raids) > maxRaidTracks {
		return DefenseSite{}, contract("defense site raid tracks exceed bound")
	}
	lords := map[string]bool{}
	for _, row := range v.Raids {
		spawn, ok := protoCell(row.GetSpawn())
		if row == nil || !ok || validID(row.GetLordId()) != nil || lords[row.GetLordId()] || validID(row.GetFactionDef()) != nil || row.SpawnTick == nil || row.LastTick == nil || row.Ground == nil ||
			row.GetSpawnTick() < 0 || row.GetLastTick() < row.GetSpawnTick() || len(row.Trail) > maxRaidTrail || int64(spawn.X) >= int64(site.Width) || int64(spawn.Z) >= int64(site.Height) {
			return DefenseSite{}, contract("invalid defense site raid track")
		}
		lords[row.GetLordId()] = true
		track := RaidTrack{LordID: row.GetLordId(), FactionDef: row.GetFactionDef(), SpawnTick: domain.Tick(row.GetSpawnTick()), LastTick: domain.Tick(row.GetLastTick()), Spawn: spawn, Ground: row.GetGround()}
		for _, at := range row.Trail {
			cell, ok := protoCell(at)
			if !ok || int64(cell.X) >= int64(site.Width) || int64(cell.Z) >= int64(site.Height) {
				return DefenseSite{}, contract("invalid defense site raid trail")
			}
			track.Trail = append(track.Trail, cell)
		}
		site.Raids = append(site.Raids, track)
	}
	return site, nil
}

func validateLinesOfFire(v *o.LinesOfFireSnapshot, identity *c.Identity, firing, approach []domain.Cell) ([]LineOfFire, error) {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return nil, contract("invalid lines of fire context")
	}
	want := len(firing) * len(approach)
	if len(v.Lines) != want || !completeCount(v.Completeness, want) {
		return nil, contract("incomplete lines of fire")
	}
	wantFrom := make(map[domain.Cell]bool, len(firing))
	for _, cell := range firing {
		wantFrom[cell] = true
	}
	wantTo := make(map[domain.Cell]bool, len(approach))
	for _, cell := range approach {
		wantTo[cell] = true
	}
	seen := make(map[[2]domain.Cell]bool, want)
	lines := make([]LineOfFire, 0, want)
	for _, row := range v.Lines {
		if row == nil {
			return nil, contract("missing line of fire")
		}
		from, fk := protoCell(row.From)
		to, tk := protoCell(row.To)
		if !fk || !tk || !wantFrom[from] || !wantTo[to] || seen[[2]domain.Cell{from, to}] {
			return nil, contract("line of fire pair unexpected or duplicated")
		}
		seen[[2]domain.Cell{from, to}] = true
		if row.Distance == nil || math.IsNaN(row.GetDistance()) || math.IsInf(row.GetDistance(), 0) || row.GetDistance() < 0 {
			return nil, contract("invalid line of fire distance")
		}
		line := LineOfFire{From: from, To: to, Distance: row.GetDistance()}
		known := row.LineOfSight != nil
		if known != (row.TargetCover != nil) || known != (row.ShooterCover != nil) {
			return nil, contract("partial line of fire facts")
		}
		if known {
			if !fraction(row.TargetCover) || !fraction(row.ShooterCover) {
				return nil, contract("invalid line of fire cover")
			}
			line.Known, line.LineOfSight = true, row.GetLineOfSight()
			line.TargetCover, line.ShooterCover = row.GetTargetCover(), row.GetShooterCover()
		} else if len(row.Issues) == 0 {
			return nil, contract("unknown line of fire without issue")
		}
		lines = append(lines, line)
	}
	return lines, nil
}
