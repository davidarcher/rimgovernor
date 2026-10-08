package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

// CreatedZone is the native identity and actual footprint of one component
// created by a stockpile rectangle. Existing player zones are never claims.
type CreatedZone struct {
	ID    string
	Cells []Cell
}

// CreatedZones keeps progress immutable and comparable.
type CreatedZones struct{ data string }

func (z CreatedZones) Rows() []CreatedZone {
	var rows []CreatedZone
	_ = json.Unmarshal([]byte(z.data), &rows)
	return rows
}

func (p Progress) RecordStockpileReceipt(attempt AttemptID, receipt Receipt, rows []CreatedZone) (Progress, error) {
	z, ok := p.action.ZoneCreate()
	if !ok || z.Kind() != StockpileZone || receipt != ReceiptAccepted || len(rows) == 0 {
		return p, errors.New("stockpile identities require an applied rectangle placement")
	}
	placements, err := NewCreatedZones(z, rows)
	if err != nil {
		return p, err
	}
	next, err := p.recordReceipt(attempt, receipt)
	if err != nil {
		return p, err
	}
	next.view.Stockpiles = Known(placements)
	return next, nil
}

func NewCreatedZones(z ZoneCreate, rows []CreatedZone) (CreatedZones, error) {
	if z.Kind() != StockpileZone || len(rows) == 0 {
		return CreatedZones{}, errors.New("missing stockpile placement")
	}
	ids := map[string]bool{}
	cells := map[Cell]bool{}
	allowed := map[Cell]bool{}
	for _, c := range z.Cells() {
		allowed[c] = true
	}
	canonical := make([]CreatedZone, 0, len(rows))
	for _, row := range rows {
		if !validID(row.ID) || ids[row.ID] {
			return CreatedZones{}, errors.New("invalid created stockpile identity")
		}
		ids[row.ID] = true
		packed, err := canonicalConnectedCells(row.Cells)
		if err != nil {
			return CreatedZones{}, err
		}
		var footprint []Cell
		_ = json.Unmarshal([]byte(packed), &footprint)
		for _, c := range footprint {
			if !allowed[c] || cells[c] {
				return CreatedZones{}, errors.New("created stockpile footprint outside rectangle or overlapping")
			}
			cells[c] = true
		}
		canonical = append(canonical, CreatedZone{ID: row.ID, Cells: footprint})
	}
	sort.Slice(canonical, func(i, j int) bool { return canonical[i].ID < canonical[j].ID })
	data, _ := json.Marshal(canonical)
	return CreatedZones{data: string(data)}, nil
}
