package store

import (
	"context"
	"encoding/json"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func initializeLedgerRows(ctx context.Context, tx *Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE ledger_rows(section TEXT NOT NULL CHECK(section<>''), position INTEGER NOT NULL CHECK(position>=0), tick INTEGER NOT NULL CHECK(tick>=0), cells TEXT NOT NULL CHECK(json_valid(cells)), PRIMARY KEY(section,position)) STRICT;`)
	return err
}

func checkLedgerRowsSchema(ctx context.Context, tx *Tx) error {
	_, err := tx.ExecContext(ctx, "SELECT section,position,tick,cells FROM ledger_rows LIMIT 0")
	return err
}

// ReplaceLedgerRows makes the table hold exactly the rows of one ledger review
// at tick, in one transaction. The table is a derived copy for Grafana
// (docs/developers/contracts/persistence-contracts.md): nothing reads it back.
func (s *Store) ReplaceLedgerRows(ctx context.Context, tick int64, rows []policy.LedgerRow) error {
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err = tx.ExecContext(ctx, "DELETE FROM ledger_rows"); err != nil {
		return err
	}
	for _, r := range rows {
		cells, err := json.Marshal(r.Cells)
		if err != nil {
			return err
		}
		if _, err = tx.ExecContext(ctx, "INSERT INTO ledger_rows(section,position,tick,cells) VALUES(?,?,?,?)", r.Section, r.Position, tick, string(cells)); err != nil {
			return err
		}
	}
	return tx.Commit()
}

// StoredLedgerRow is a ledger_rows row as stored.
type StoredLedgerRow struct {
	Section  string
	Position int
	Tick     int64
	Cells    map[string]any
}

// LedgerRows is every stored row, by section then position. The controller
// never reads the table back; this is for tests and tools.
func (s *Store) LedgerRows(ctx context.Context) ([]StoredLedgerRow, error) {
	rows, err := s.db.QueryContext(ctx, "SELECT section,position,tick,cells FROM ledger_rows ORDER BY section,position")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []StoredLedgerRow
	for rows.Next() {
		var r StoredLedgerRow
		var cells string
		if err = rows.Scan(&r.Section, &r.Position, &r.Tick, &cells); err != nil {
			return nil, err
		}
		if err = json.Unmarshal([]byte(cells), &r.Cells); err != nil {
			return nil, err
		}
		out = append(out, r)
	}
	return out, rows.Err()
}
