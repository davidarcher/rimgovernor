package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Field placement (#2920). A field node (layout_field_nodes.go) is placed by the
// embedder beside the room groups and written by the tiler as a field zone. It
// is a block like a room's, with three differences, all of them the outdoors:
//
//   - Site. Its mask is the soil the crop grows in (walkable, not rock, fertile
//     to the crop's least), not the core the rooms stand on, so a field lands on
//     soil and a room is free to stand on soil a field does not use: occupancy
//     is shared, and coreObstacles no longer bans field cells once fields are
//     nodes. A cell the survey lacks (fog) is in no mask.
//   - Walls. It has none: it may touch a room's wall ring but not its interior
//     or the ring itself, and may touch another field.
//   - Cost. A field wants soil, so the soil and rock terms drop (the mask is the
//     soil). Its centre and edge terms are the mean per cell, one node's worth:
//     a 400-cell field would otherwise weigh 20-50 times a 12-cell room in the
//     cell terms, and the walk term, the trade between a field and a room, would
//     never win against it. The crop-flow link is priced by the nearest-edge
//     distance (fieldGap), not gapDist, which counts a wall.
//
// No constant is new: the shapes are the squarest rectangle and its turn, as a
// room's, and a field no rectangle of soil fits is unplaceable (resizing on
// demand is the next stage).

// Verdict reasons of field placement.
const (
	ReasonEmbedFieldPlaced = "embed_field_on_soil"
	ReasonEmbedFieldNoSoil = "embed_field_no_soil"
	ReasonTileField        = "tile_field_zone"
)

// EmbedField is a field node with its links to the room groups: the weights of
// its crop-flow edges, split over the groups holding the station at the other end.
type EmbedField struct {
	Node  FieldNode
	Links []GroupLink
}

// EmbeddedField is where a field stands.
type EmbeddedField struct {
	ID       string
	Interior Rectangle
}

// Field is the field's block.
func (e Embedding) Field(id string) (Rectangle, bool) {
	for _, f := range e.Fields {
		if f.ID == id {
			return f.Interior, true
		}
	}
	return Rectangle{}, false
}

// FieldEmbedLinks are the graph's fields with their crop-flow edges moved onto
// the groups. A node held by several groups (its stations split) shares the
// edge's weight in proportion to its stations in each.
func FieldEmbedLinks(g StationGraph, groups []RoomGroup) []EmbedField {
	total := map[string]int{}
	for _, grp := range groups {
		for _, s := range grp.Stations {
			total[s.Node] += s.Count
		}
	}
	var out []EmbedField
	for _, f := range g.Fields {
		weight := map[int]float64{}
		for _, e := range g.Edges {
			var other string
			switch f.ID {
			case e.A:
				other = e.B
			case e.B:
				other = e.A
			default:
				continue
			}
			if total[other] == 0 {
				continue
			}
			for _, grp := range groups {
				for _, s := range grp.Stations {
					if s.Node == other {
						weight[grp.ID] += e.Weight * float64(s.Count) / float64(total[other])
					}
				}
			}
		}
		field := EmbedField{Node: f}
		for to, w := range weight {
			if w > 0 {
				field.Links = append(field.Links, GroupLink{To: to, Weight: w})
			}
		}
		slices.SortFunc(field.Links, func(a, b GroupLink) int { return a.To - b.To })
		out = append(out, field)
	}
	return out
}

// withFields is in with each field appended to the groups as a block of its
// area, and the reverse of its links on the groups it links (clustering's links
// run both ways). The input's groups are not changed.
func (in EmbedInput) withFields() EmbedInput {
	groups := make([]RoomGroup, len(in.Groups), len(in.Groups)+len(in.Fields))
	for i, g := range in.Groups {
		g.Links = slices.Clone(g.Links)
		groups[i] = g
	}
	for _, f := range in.Fields {
		id := len(groups)
		var links []GroupLink
		for _, l := range f.Links {
			if l.To < 0 || l.To >= len(in.Groups) || l.Weight <= 0 {
				continue
			}
			links = append(links, GroupLink{To: l.To, Weight: l.Weight})
			groups[l.To].Links = append(groups[l.To].Links, GroupLink{To: id, Weight: l.Weight})
		}
		groups = append(groups, RoomGroup{ID: id, Area: f.Node.Area, Links: links})
	}
	out := in
	out.Groups = groups
	return out
}

func (e *embedder) isField(i int) bool { return i >= e.nRooms }

// cellsFor are the cells a block of group i may start on: the core for a room,
// the soil of its crop for a field.
func (e *embedder) cellsFor(i int) []domain.Cell {
	if e.isField(i) {
		return e.fieldCells[i-e.nRooms]
	}
	return e.core
}

// fieldMasks prices the soil each field may stand on: a prefix sum per least
// fertility, over the cells the survey holds that the core allows and that are
// not rock.
func (e *embedFixed) fieldMasks(in EmbedInput, scorer planScorer) {
	type mask struct {
		sum   prefix
		cells []domain.Cell
	}
	byFertility := map[float64]mask{}
	for _, f := range in.Fields {
		m, ok := byFertility[f.Node.FertilityMin]
		if !ok {
			soil := map[domain.Cell]bool{}
			for _, c := range in.Survey.Cells {
				if scorer.g.core[c.Cell] && !c.Rock && c.Walkable && c.Fertility >= f.Node.FertilityMin {
					soil[c.Cell] = true
				}
			}
			m.sum = newPrefix(in.Survey.Bounds.Width, in.Survey.Bounds.Height, func(c domain.Cell) int { return b2i(soil[c]) })
			for c := range soil {
				m.cells = append(m.cells, c)
			}
			sortCells(m.cells)
			byFertility[f.Node.FertilityMin] = m
		}
		e.fieldMask = append(e.fieldMask, m.sum)
		e.fieldCells = append(e.fieldCells, m.cells)
	}
}

// fieldFits reports field i at r on its soil and clear of the other blocks: not
// over a room's interior or wall ring, not over another field.
func (e *embedder) fieldFits(i int, r Rectangle, blocks []Rectangle) bool {
	m := e.fieldMask[i-e.nRooms]
	if !m.inside(r) || m.sum(r) != int64(r.Width)*int64(r.Height) {
		return false
	}
	for j, b := range blocks {
		if j == i || b.Width == 0 {
			continue
		}
		if e.isField(j) {
			if axisGap(r.X, r.Width, b.X, b.Width) < 0 && axisGap(r.Z, r.Height, b.Z, b.Height) < 0 {
				return false
			}
		} else if collides(r, b) {
			return false
		}
	}
	return true
}

// fieldCellCost is a field's centre and edge terms, the mean per cell.
func (e *embedder) fieldCellCost(r Rectangle) float64 {
	return float64(-(e.centre.sum(r) + e.edge.sum(r))) / float64(max(r.Width*r.Height, 1))
}

// fieldGap is the walking gap between a field and a block in cells: the cells
// between their nearest edges, floored at 1 (touching, as sharing a wall is
// for rooms), so linkCost's gap-1 is the cells walked.
func fieldGap(a, b Rectangle) int {
	sx, sz := axisGap(a.X, a.Width, b.X, b.Width), axisGap(a.Z, a.Height, b.Z, b.Height)
	return max(max(sx, 0)+max(sz, 0), 1)
}

// gap is the walking gap between group i at r and group j at b.
func (e *embedder) gap(i int, r Rectangle, j int, b Rectangle) int {
	if e.isField(i) || e.isField(j) {
		return fieldGap(r, b)
	}
	return gapDist(r, b)
}

// embedField reads field id's block into the embedding: the field with its
// decision, and its per-node centre and edge terms in the score.
func (r *embedRun) embedField(out *Embedding, id string, b Rectangle) {
	if b.Width == 0 {
		out.Decisions = append(out.Decisions, EmbedDecision{Group: -1, Other: -1, Field: id, Verdict: EmbedUnplaceable, Reason: ReasonEmbedFieldNoSoil})
		return
	}
	cells := float64(max(b.Width*b.Height, 1))
	out.Fields = append(out.Fields, EmbeddedField{ID: id, Interior: b})
	out.Decisions = append(out.Decisions, EmbedDecision{Group: -1, Other: -1, Field: id, Verdict: EmbedPlace, Reason: ReasonEmbedFieldPlaced})
	out.Score.Centre += int(float64(r.e.centre.sum(b)) / cells)
	out.Score.Edge += int(float64(r.e.edge.sum(b)) / cells)
}

// fieldZones adds each placed field to the plan as a field zone, after the
// whole-map zones, and notes it. TileDecision.Field names the field.
func (t *tiler) fieldZones() {
	t.plan.Zones = slices.Clone(t.in.Zones)
	for _, f := range t.in.Embedding.Fields {
		z := fieldZone(f.Interior, t.in.Survey)
		if len(z.Runs) == 0 {
			continue
		}
		t.plan.Zones = append(t.plan.Zones, z)
		t.out.Decisions = append(t.out.Decisions, TileDecision{Group: -1, Other: -1, Field: f.ID, Verdict: TilePlace, Reason: ReasonTileField})
	}
}

// fieldZone is a placed field as a zone: its rows, less the cells the survey
// lacks (fogged cells get no zone).
func fieldZone(r Rectangle, s MapSurvey) LayoutZone {
	known := map[domain.Cell]bool{}
	for _, c := range s.Cells {
		if c.Cell.X >= r.X && c.Cell.X < r.X+r.Width && c.Cell.Z >= r.Z && c.Cell.Z < r.Z+r.Height {
			known[c.Cell] = true
		}
	}
	z := LayoutZone{Kind: ZoneField}
	for row := r.Z; row < r.Z+r.Height; row++ {
		for x := r.X; x < r.X+r.Width; x++ {
			if !known[domain.Cell{X: x, Z: row}] {
				continue
			}
			start := x
			for x+1 < r.X+r.Width && known[domain.Cell{X: x + 1, Z: row}] {
				x++
			}
			z.Runs = append(z.Runs, RowRun{Z: row, X: start, Length: x - start + 1})
		}
	}
	return z
}
