package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// ReadBareCells reads the things over cells' bounding rect and reports
// which cells hold no plant (a thing row with a growth value): unsown or
// harvested soil a shrinking growing zone may give up (#1309). A fogged
// cell is not bare.
func (client *Client) ReadBareCells(ctx context.Context, identity *c.Identity, cells []domain.Cell) (map[domain.Cell]bool, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if len(cells) == 0 {
		return map[domain.Cell]bool{}, Result{}, nil
	}
	minX, minZ, maxX, maxZ := cells[0].X, cells[0].Z, cells[0].X, cells[0].Z
	for _, cell := range cells[1:] {
		minX, minZ, maxX, maxZ = min(minX, cell.X), min(minZ, cell.Z), max(maxX, cell.X), max(maxZ, cell.Z)
	}
	read, raw, err := client.readCells(ctx, identity, policy.Rectangle{X: minX, Z: minZ, Width: maxX - minX + 1, Height: maxZ - minZ + 1}, false, true)
	if err != nil {
		return nil, raw, err
	}
	planted := map[domain.Cell]bool{}
	for _, row := range read.Things {
		if row.Growth != nil {
			p := row.GetThing().GetPosition()
			planted[domain.Cell{X: p.GetX(), Z: p.GetZ()}] = true
		}
	}
	held := map[domain.Cell]bool{}
	for _, cell := range read.Grid.Cells() {
		held[cell.Cell] = true
	}
	out := map[domain.Cell]bool{}
	for _, cell := range cells {
		if held[cell] && !planted[cell] {
			out[cell] = true
		}
	}
	return out, raw, nil
}
