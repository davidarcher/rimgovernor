package bridge

import (
	"encoding/binary"
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ExpandCompactCells validates the compact representation before expanding it
// into ordinary planning rows. Existing row validation still owns scope,
// completeness, delta ticks and individual fact invariants.
func ExpandCompactCells(v *o.CellsSnapshot) error {
	p := v.GetCompact()
	if p == nil {
		return nil
	}
	bad := func() error { return contract("invalid compact planning cells") }
	if len(v.Cells) != 0 || !proto.Equal(v.AppliedFields, planningWindowFields()) || v.Region == nil ||
		!colonyCell(v.Region.Minimum, v.MapSize) || !colonyCell(v.Region.Maximum, v.MapSize) {
		return bad()
	}
	width := int64(v.Region.Maximum.GetX()) - int64(v.Region.Minimum.GetX()) + 1
	height := int64(v.Region.Maximum.GetZ()) - int64(v.Region.Minimum.GetZ()) + 1
	if width < 1 || height < 1 || width*height > planningWindowPage || int64(len(p.Rows)) != height ||
		len(p.Strings) > int(width*height)*3 || len(p.Glow) > int(width*height) || len(p.Fertility) > int(width*height) {
		return bad()
	}
	for _, s := range p.Strings {
		if validID(s) != nil {
			return bad()
		}
	}
	for _, n := range p.Glow {
		if math.IsNaN(n) || math.IsInf(n, 0) || n < 0 || n > 1 {
			return bad()
		}
	}
	for _, n := range p.Fertility {
		if math.IsNaN(n) || math.IsInf(n, 0) || n <= 0 {
			return bad()
		}
	}
	cells := make([]*o.CellState, 0, int(width*height))
	fertility, unchanged := 0, uint32(0)
	for z, data := range p.Rows {
		for x := int64(0); x < width; x++ {
			if len(data) < 2 {
				return bad()
			}
			flags := binary.LittleEndian.Uint16(data)
			data = data[2:]
			if flags&1 != 0 {
				if flags != 1 {
					return bad()
				}
				unchanged++
				continue
			}
			row := &o.CellState{Cell: &c.Cell{X: proto.Int32(v.Region.Minimum.GetX() + int32(x)), Z: proto.Int32(v.Region.Minimum.GetZ() + int32(z))}}
			if flags&2 != 0 {
				if flags != 2 {
					return bad()
				}
				row.Fogged = proto.Bool(true)
				cells = append(cells, row)
				continue
			}
			row.Walkable = proto.Bool(flags&4 != 0)
			row.Passable = proto.Bool(flags&8 != 0)
			row.Occupied = proto.Bool(flags&16 != 0)
			row.Doorway = proto.Bool(flags&32 != 0)
			row.SupportsLight = proto.Bool(flags&64 != 0)
			row.StorageEmpty = proto.Bool(flags&128 != 0)
			row.Indoors = proto.Bool(flags&256 != 0)
			row.Polluted = proto.Bool(flags&512 != 0)
			row.NaturalRock = proto.Bool(flags&0x4000 != 0)
			for i, target := range []**string{&row.Roof, &row.ZoneId, &row.RoomId} {
				if flags&(2048<<i) == 0 {
					continue
				}
				index, n := binary.Uvarint(data)
				if n <= 0 || index >= uint64(len(p.Strings)) {
					return bad()
				}
				data = data[n:]
				*target = proto.String(p.Strings[index])
			}
			row.Ruin = proto.Bool(false)
			if flags&0x8000 != 0 {
				edifice, n := binary.Uvarint(data)
				if n <= 0 || edifice > uint64(len(p.Strings)) {
					return bad()
				}
				data = data[n:]
				if edifice == 0 {
					row.Ruin = proto.Bool(true)
				} else {
					row.PlayerEdifice = proto.String(p.Strings[edifice-1])
				}
			}
			index, n := binary.Uvarint(data)
			if n <= 0 || index >= uint64(len(p.Glow)) {
				return bad()
			}
			data = data[n:]
			row.Glow = proto.Float64(p.Glow[index])
			if flags&1024 != 0 {
				if fertility >= len(p.Fertility) {
					return bad()
				}
				row.Fertility = proto.Float64(p.Fertility[fertility])
				fertility++
			}
			cells = append(cells, row)
		}
		if len(data) != 0 {
			return bad()
		}
	}
	if fertility != len(p.Fertility) || unchanged != v.GetUnchanged() || v.GetCompleteness().GetFiltered() != 0 {
		return bad()
	}
	v.Cells = cells
	v.Compact = nil
	return nil
}
