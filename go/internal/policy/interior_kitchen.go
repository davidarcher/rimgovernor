package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The kitchen template (#805): stoves in one row against a wall, each
// worker cell on open floor in front of it. A kitchen's second door is read
// as the door to its freezer (a kitchen is no thoroughfare, #780), so the
// row lines the wall holding that door, anchored to the corner beyond it:
// the cook steps from the freezer straight to the stoves. A kitchen with
// only its entrance lines the back wall, centred. The frame takes the door
// first in cell order as the entrance, so of two doors either may read as
// the freezer's; v2 layout kitchens have one door (freezer beside it on
// the spine) and get the back row. Butchering never comes
// here: SeparationProtectedCells keeps butcher placement out of kitchens.

// KitchenStoveDefinition is the stove the template plans.
const KitchenStoveDefinition = "FueledStove"

var (
	kitchenStoveSize        = domain.Cell{X: 3, Z: 1}
	kitchenStoveInteraction = domain.Cell{X: 0, Z: -1}
)

// kitchenMaxStoves bounds the row; two stoves feed a mid-sized colony.
const kitchenMaxStoves = 2

func init() {
	RegisterInteriorTemplate(RoomRoleKitchen, InteriorTemplate{Name: "kitchen", Plan: planKitchen})
}

func planKitchen(f InteriorFrame) ([]InteriorPiece, bool) {
	for _, d := range f.Doors {
		if pieces, ok := kitchenRowBeside(f, d); ok {
			return pieces, true
		}
	}
	return kitchenBackRow(f)
}

func kitchenStove(i int, rot domain.Rotation, corner domain.Cell) InteriorPiece {
	p := NewInteriorPiece("stove."+string(rune('1'+i)), KitchenStoveDefinition, kitchenStoveSize, rot, corner)
	off := kitchenStoveInteraction
	p.InteractionOffset, p.Row = &off, "stoves"
	return p
}

// kitchenBackRow centres the stoves on the back wall, facing the entrance.
// The back row must leave a free row in front of it.
func kitchenBackRow(f InteriorFrame) ([]InteriorPiece, bool) {
	if f.Depth < 3 {
		return nil, false
	}
	n := min(RowCapacity(f.Width, kitchenStoveSize.X, 0), kitchenMaxStoves)
	starts, ok := RowStarts(f.Width, kitchenStoveSize.X, 0, max(n, 1), RowCentred)
	if !ok {
		return nil, false
	}
	var out []InteriorPiece
	for i, u := range starts {
		out = append(out, kitchenStove(i, domain.North, domain.Cell{X: u, Z: f.Depth - 1}))
	}
	return out, true
}

// kitchenRowBeside lines the wall holding a non-entrance door with stoves,
// on the longer side of the door and anchored to that side's corner.
func kitchenRowBeside(f InteriorFrame, d domain.Cell) ([]InteriorPiece, bool) {
	var rot domain.Rotation
	var along, at, length int32 // door position along the wall, the row's fixed coordinate, the wall length
	vertical := true
	switch {
	case d.X == f.Width:
		rot, along, at, length = domain.East, d.Z, f.Width-1, f.Depth
	case d.X == -1:
		rot, along, at, length = domain.West, d.Z, 0, f.Depth
	case d.Z == f.Depth:
		rot, along, at, length, vertical = domain.North, d.X, f.Depth-1, f.Width, false
	default:
		return nil, false // the front wall: that door is another way in
	}
	// The row needs a free line in front of it for the worker cells.
	if vertical && f.Width < 3 || !vertical && f.Depth < 3 {
		return nil, false
	}
	below, above := along, length-along-1
	span := kitchenStoveSize.X
	var start int32
	var n int32
	if above >= below {
		n = min(above/span, kitchenMaxStoves)
		start = length - n*span
	} else {
		n = min(below/span, kitchenMaxStoves)
		start = 0
	}
	if n < 1 || vertical && at == f.Entrance && start == 0 {
		return nil, false // the row would stand inside the entrance
	}
	var out []InteriorPiece
	for i := int32(0); i < n; i++ {
		c := domain.Cell{X: start + i*span, Z: at}
		if vertical {
			c = domain.Cell{X: at, Z: start + i*span}
		}
		out = append(out, kitchenStove(int(i), rot, c))
	}
	return out, true
}
