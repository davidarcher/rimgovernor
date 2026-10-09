package observation

import (
	"iter"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// MechFleet lifts the pawn table's living mechanitors (a colonist with a
// mechanitor block) and mechs (a mechanoid with a mech block) into the mech
// planners' inputs. A row whose biotech read failed is skipped: its
// facts are unknown, so nothing is planned for it.
func MechFleet(rows iter.Seq[*o.PawnState]) policy.MechFleet {
	var fleet policy.MechFleet
	for row := range rows {
		if row == nil || row.GetDead() {
			continue
		}
		bt, ok := bridge.PawnBiotech(row.Biotech).Value()
		if !ok {
			continue
		}
		id := policy.PawnID(row.GetPawn().GetId())
		cell := rowCell(row.GetPawn().GetPosition())
		if m, known := bt.Mechanitor.Value(); known && m != nil && row.GetColonist() {
			fleet.Mechanitors = append(fleet.Mechanitors, policy.MechanitorInput{ID: id, PawnMechanitor: *m, Cell: cell})
		}
		if m, known := bt.Mech.Value(); known && m != nil && row.GetMechanoid() {
			mech := policy.MechInput{ID: id, Kind: row.GetKindDefName(), PawnMech: *m, Cell: cell, Downed: row.GetDowned(), Drafted: row.GetDrafted()}
			if j := row.Job; j != nil && j.TargetA.GetEntity() != nil {
				mech.Target = policy.PawnID(j.TargetA.GetEntity().GetId())
			}
			fleet.Mechs = append(fleet.Mechs, mech)
		}
	}
	return fleet
}

// RowCell is the pawn row's position.
func RowCell(row *o.PawnState) domain.Fact[domain.Cell] { return rowCell(row.GetPawn().GetPosition()) }

func rowCell(cell *c.Cell) domain.Fact[domain.Cell] {
	if cell == nil || cell.X == nil || cell.Z == nil {
		return domain.Unknown[domain.Cell]()
	}
	return domain.Known(domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
}
