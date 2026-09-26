package domain

import "errors"

// ZoneDelete is an immutable, comparable value: a one-shot deletion of one
// exact zone (native DeleteZone, #611), CAS-gated by the zone's
// own already-observed snapshot token the same way ClaimBuilding gates a
// building patch. The token covers the zone's cells and settings; it is a
// stale-read guard only (#719): a refused delete re-reads the zone and
// retries on the fresh token, whoever edited it.
type ZoneDelete struct {
	zone   string
	before string
}

func NewZoneDelete(zone, before string) (ZoneDelete, error) {
	if !validID(zone) || !validID(before) {
		return ZoneDelete{}, errors.New("invalid zone delete identity")
	}
	return ZoneDelete{zone, before}, nil
}
func (d ZoneDelete) Zone() string        { return d.zone }
func (d ZoneDelete) BeforeToken() string { return d.before }

func NewZoneDeleteAction(id ActionID, d ZoneDelete) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewZoneDelete(d.zone, d.before)
	if err != nil || canonical != d {
		return Action{}, errors.New("invalid zone delete")
	}
	return Action{id: id, kind: ZoneDeleteAction, zoneDelete: d}, nil
}
func (a Action) ZoneDelete() (ZoneDelete, bool) {
	return a.zoneDelete, a.kind == ZoneDeleteAction
}
