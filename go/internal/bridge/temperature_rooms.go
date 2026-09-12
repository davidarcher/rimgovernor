package bridge

import (
	"context"
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ReadTemperatureRooms reads complete indoor room geometry and contents. It
// grants no room ownership; eligible player beds are joined by the projection.
func (client *Client) ReadTemperatureRooms(ctx context.Context, identity *c.Identity) (*o.ListRoomsReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	request := &o.ListRoomsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, IncludeOutdoors: proto.Bool(false), IncludeBoundary: proto.Bool(false), IncludeCells: proto.Bool(true), Page: &c.PageRequest{Limit: proto.Uint32(256)}}
	reply := &o.ListRoomsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_rooms", request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ListRoomsReply_Failure:
		err = failure(v.Failure, raw)
	case *o.ListRoomsReply_Unavailable:
		err = unavailable(v.Unavailable, raw)
	case *o.ListRoomsReply_Observed:
		err = ValidateTemperatureRooms(v.Observed, request.Scope.ExpectedIdentity)
	default:
		err = contract("room read outcome missing")
	}
	return reply, raw, err
}

// ValidateTemperatureRooms checks every fact consumed by temperature planning.
// Other typed room details (beauty, wealth, labels) convey no thermal authority.
func ValidateTemperatureRooms(v *o.RoomsSnapshot, identity *c.Identity) error {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return contract("invalid room context")
	}
	if err := buildingUnknown(v); err != nil {
		return err
	}
	if err := colonyCounts(v.Completeness, len(v.Rooms), 256); err != nil {
		return err
	}
	size := &o.MapSize{Width: proto.Uint32(4096), Height: proto.Uint32(4096)}
	rooms, beds, cells := map[string]bool{}, map[string]bool{}, map[[2]int32]bool{}
	for _, room := range v.Rooms {
		if room == nil || validID(room.GetId()) != nil || rooms[room.GetId()] || room.ProperRoom == nil || room.Doorway == nil || room.PsychologicallyOutdoors == nil || room.Outdoors == nil || room.TouchesMapEdge == nil || room.OpenRoofCount == nil || room.CellCount == nil || room.GetCellCount() == 0 || room.GetCellCount() != uint32(len(room.Cells)) || room.GetOpenRoofCount() > room.GetCellCount() || room.GetDoorway() || room.GetPsychologicallyOutdoors() {
			return contract("invalid indoor room")
		}
		rooms[room.GetId()] = true
		if room.TemperatureC != nil && (math.IsNaN(room.GetTemperatureC()) || math.IsInf(room.GetTemperatureC(), 0)) {
			return contract("invalid room temperature")
		}
		if err := pawnsIssues(room.Issues, room.ProtoReflect()); err != nil {
			return err
		}
		if err := colonyCounts(room.CellsCompleteness, len(room.Cells), 4096); err != nil {
			return err
		}
		if err := colonyCounts(room.ContentsCompleteness, len(room.Contents), 256); err != nil {
			return err
		}
		if err := colonyQuantities(room.Contents); err != nil {
			return err
		}
		for _, q := range room.Contents {
			if q.Units == nil {
				return contract("missing room content count")
			}
		}
		local := map[[2]int32]bool{}
		minX, minZ, maxX, maxZ := int32(4096), int32(4096), int32(-1), int32(-1)
		for _, cell := range room.Cells {
			if !colonyCell(cell, size) {
				return contract("invalid room cell")
			}
			key := [2]int32{cell.GetX(), cell.GetZ()}
			if cells[key] {
				return contract("overlapping room cells")
			}
			cells[key], local[key] = true, true
			minX, minZ, maxX, maxZ = min(minX, key[0]), min(minZ, key[1]), max(maxX, key[0]), max(maxZ, key[1])
		}
		if len(cells) > 65536 || !colonyCell(room.Center, size) || !local[[2]int32{room.Center.GetX(), room.Center.GetZ()}] || room.Extents == nil || !colonyCell(room.Extents.Minimum, size) || !colonyCell(room.Extents.Maximum, size) || room.Extents.Minimum.GetX() != minX || room.Extents.Minimum.GetZ() != minZ || room.Extents.Maximum.GetX() != maxX || room.Extents.Maximum.GetZ() != maxZ {
			return contract("inconsistent room geometry")
		}
		if len(room.Beds) > 256 {
			return contract("room bed census exceeds bound")
		}
		for _, bed := range room.Beds {
			if bed == nil || bed.Building == nil || pawnsEntity(bed.Building, v.Context) != nil || bed.Building.DefName == nil || bed.Building.MapId == nil || !colonyCell(bed.Building.Position, size) || !local[[2]int32{bed.Building.Position.GetX(), bed.Building.Position.GetZ()}] || beds[bed.Building.GetId()] || bed.GetStatus() != "built" {
				return contract("invalid room bed")
			}
			beds[bed.Building.GetId()] = true
		}
	}
	return nil
}
