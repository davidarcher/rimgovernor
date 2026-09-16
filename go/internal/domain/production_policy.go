package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

// ProductionPolicyAction is intent to push one native SetProductionPolicy
// floors/stopped replacement toward policy.ProductionFloors's
// operator-declared reserve/stopped-resource configuration, giving that
// tested pure primitive its first real caller.
// The native write replaces the whole map-scoped ProductionPolicyState in one
// call (contracts/proto/operations.proto's SetProductionPolicy comment), so
// this action only ever carries the two rows this vertical actually wants to
// change (Floors, Stopped); the Commitments and Drills rows another system
// may independently own are read fresh and resent verbatim immediately
// before dispatch (buildingruntime's productionPolicyBoundary), never stored
// here, so a stale plan can never clobber them.
const ProductionPolicyAction ActionKind = "production_policy"

// ResourceFloor is one native resource definition's positive reserve floor
// (policy.ProductionFloors already strips zero reserves,
// so a floor here is always strictly positive).
type ResourceFloor struct {
	Resource string
	Floor    int64
}

// ProductionPolicy is an immutable, comparable value: like WorkAssignment,
// its private canonical JSON encoding retains a bounded typed list without
// exposing mutable slices on the closed Action variant.
type ProductionPolicy struct {
	floors  string
	stopped string
}

func NewProductionPolicy(floors []ResourceFloor, stopped []string) (ProductionPolicy, error) {
	if len(floors) > 256 || len(stopped) > 256 {
		return ProductionPolicy{}, errors.New("production policy exceeds bound")
	}
	rows := append([]ResourceFloor(nil), floors...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].Resource < rows[j].Resource })
	for i, row := range rows {
		if !validID(row.Resource) || row.Floor <= 0 || row.Floor > 10000 || (i > 0 && rows[i-1].Resource == row.Resource) {
			return ProductionPolicy{}, errors.New("invalid production floor")
		}
	}
	stoppedRows := append([]string(nil), stopped...)
	sort.Strings(stoppedRows)
	for i, name := range stoppedRows {
		if !validID(name) || (i > 0 && stoppedRows[i-1] == name) {
			return ProductionPolicy{}, errors.New("invalid stopped resource")
		}
	}
	floorsData, err := json.Marshal(rows)
	if err != nil {
		return ProductionPolicy{}, err
	}
	stoppedData, err := json.Marshal(stoppedRows)
	if err != nil {
		return ProductionPolicy{}, err
	}
	if len(floorsData) > 16384 || len(stoppedData) > 16384 {
		return ProductionPolicy{}, errors.New("production policy exceeds storage bound")
	}
	return ProductionPolicy{string(floorsData), string(stoppedData)}, nil
}
func (p ProductionPolicy) Floors() []ResourceFloor {
	var rows []ResourceFloor
	_ = json.Unmarshal([]byte(p.floors), &rows)
	return rows
}
func (p ProductionPolicy) Stopped() []string {
	var rows []string
	_ = json.Unmarshal([]byte(p.stopped), &rows)
	return rows
}
func NewProductionPolicyAction(id ActionID, v ProductionPolicy) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewProductionPolicy(v.Floors(), v.Stopped())
	if err != nil || canonical != v {
		return Action{}, errors.New("invalid production policy action")
	}
	return Action{id: id, kind: ProductionPolicyAction, productionPolicy: v}, nil
}
func (a Action) ProductionPolicy() (ProductionPolicy, bool) {
	return a.productionPolicy, a.kind == ProductionPolicyAction
}
