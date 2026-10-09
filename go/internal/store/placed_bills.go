package store

import (
	"context"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PlacedBill is a native bill a Standard's production_bill action placed:
// the receipt's journaled bill id with the action's bench and mode.
// Standard is the owning standard's row id; Rounds.Need names its concern.
type PlacedBill struct {
	Standard domain.ConcernID
	Bench    string
	Mode     domain.BillMode
}

// PlacedBills names the owner of each of bills (native bill ids on the
// current load's benches) that a production_bill action of a Standard placed,
// retired plans included. A bill the journal never placed is absent.
func (s *Store) PlacedBills(ctx context.Context, current domain.GenerationSnapshot, bills []string) (map[string]PlacedBill, error) {
	if len(bills) == 0 {
		return nil, nil
	}
	wanted := make(map[string]bool, len(bills))
	args := make([]any, 0, len(bills))
	for _, id := range bills {
		wanted[id] = true
		args = append(args, id)
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, `SELECT DISTINCT a.plan_id,m.owner_id FROM actions a JOIN transitions t ON t.action_id=a.id JOIN plan_methods m ON m.plan_id=a.plan_id AND m.kind='standard'
 WHERE a.kind='production_bill' AND json_extract(t.payload,'$.Bill') IN (?`+strings.Repeat(",?", len(args)-1)+`)`, args...)
	if err != nil {
		return nil, err
	}
	type link struct {
		plan  domain.PlanID
		owner domain.ConcernID
	}
	var links []link
	for rows.Next() {
		var v link
		if err = rows.Scan(&v.plan, &v.owner); err != nil {
			rows.Close()
			return nil, err
		}
		links = append(links, v)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return nil, err
	}
	out := map[string]PlacedBill{}
	for _, v := range links {
		plan, err := load(ctx, tx, v.plan)
		if err != nil {
			return nil, err
		}
		for _, progress := range plan.Progress {
			view := progress.View()
			id, known := view.Bill.Value()
			bill, isBill := progress.Action().ProductionBill()
			if !known || !isBill || !wanted[id] || view.Snapshot.Colony != current.Colony || view.Snapshot.Load != current.Load || view.Snapshot.Map != current.Map {
				continue
			}
			out[id] = PlacedBill{Standard: v.owner, Bench: bill.Bench(), Mode: bill.Mode()}
		}
	}
	return out, tx.Commit()
}
