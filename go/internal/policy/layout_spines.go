package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Spines and crossings. The generator lays Spine[0] as the main
// east-west hallway (nothing else may assume it); every later segment
// is a north-south crossing laid through it, so the core grows from a line into a + and then an H (a crossing at each end of the main
// hallway). A crossing always runs out on both sides of the main hallway,
// never one, so no branch turns into an L or U. The line beyond each
// hallway's ends stays clear of the other hallways' rooms, so it can still
// grow straight.

// spineMaxLen caps a hallway's length before rooms move on to the next one.
const spineMaxLen int32 = 64

// maxCrossings are the centre crossing and one at each end of the main hallway.
const maxCrossings = 3

func alongX(s SpineSegment) bool { return s.From.Z == s.To.Z }

// onSegment reports r's door opening onto s's hallway.
func onSegment(r PlannedRoom, s SpineSegment) bool {
	lo, hi := s.From, s.To
	if alongX(s) {
		d := r.Door.Z - s.From.Z
		return (d == 2 || d == -2) && r.Door.X >= min(lo.X, hi.X) && r.Door.X <= max(lo.X, hi.X)
	}
	d := r.Door.X - s.From.X
	return (d == 2 || d == -2) && r.Door.Z >= min(lo.Z, hi.Z) && r.Door.Z <= max(lo.Z, hi.Z)
}

// growSpine adds the next crossing: the centre one, then one past each end of
// the main hallway. A crossing is laid through the main hallway at the column
// nearest its try (searching out to reach cells either way) whose full
// cross-section is core ground clear of every room; the main hallway is
// stretched to meet it.
func (g coreGrid) growSpine(spine []SpineSegment, rooms []PlannedRoom) ([]SpineSegment, bool) {
	if len(spine) == 0 || len(spine) > maxCrossings || !alongX(spine[0]) {
		return spine, false
	}
	main := newHallFrame(spine[0])
	half := coreHalf
	clear := func(cx int32) bool {
		for _, s := range spine[1:] {
			if cx-s.From.X < 2*SpineWidth && s.From.X-cx < 2*SpineWidth {
				return false
			}
		}
		band := Rectangle{X: cx - SpineWidth/2, Z: main.line - half, Width: SpineWidth, Height: 2*half + 1}
		for x := band.X; x < band.X+band.Width; x++ {
			for z := band.Z; z < band.Z+band.Height; z++ {
				if !g.core[domain.Cell{X: x, Z: z}] {
					return false
				}
			}
		}
		for _, r := range rooms {
			if rectsOverlap(band, roomWalls(r)) {
				return false
			}
		}
		// The main hallway must reach the crossing over core ground.
		for x := min(cx, main.lo); x <= max(cx, main.hi); x++ {
			for dz := -SpineWidth / 2; dz <= SpineWidth/2; dz++ {
				if !g.core[domain.Cell{X: x, Z: main.line + dz}] {
					return false
				}
			}
		}
		return true
	}
	var tries [][2]int32 // column, reach
	if len(spine) == 1 {
		tries = append(tries, [2]int32{(main.lo + main.hi) / 2, (main.hi-main.lo)/2 + 1})
	}
	tries = append(tries, [2]int32{main.hi + SpineWidth/2 + 1, 0}, [2]int32{main.lo - SpineWidth/2 - 1, 0})
	for _, t := range tries {
		for d := int32(0); d <= t[1]; d++ {
			for _, cx := range []int32{t[0] + d, t[0] - d} {
				if !clear(cx) {
					continue
				}
				stretched := main
				stretched.extend(cx, cx)
				out := append([]SpineSegment{stretched.segment()}, spine[1:]...)
				return append(out, SpineSegment{From: domain.Cell{X: cx, Z: main.line - SpineWidth/2}, To: domain.Cell{X: cx, Z: main.line + SpineWidth/2}}), true
			}
		}
	}
	return spine, false
}

func rectsOverlap(a, b Rectangle) bool {
	return a.X < b.X+b.Width && b.X < a.X+a.Width && a.Z < b.Z+b.Height && b.Z < a.Z+a.Height
}
