package domain

import "errors"

// ZoneDelete is an immutable, comparable value: a one-shot deletion of one
// exact zone (native DeleteZone, #611); native checks the zone
// when the intent applies.
type ZoneDelete struct {
	zone string
}

func NewZoneDelete(zone string) (ZoneDelete, error) {
	if !validID(zone) {
		return ZoneDelete{}, errors.New("invalid zone delete identity")
	}
	return ZoneDelete{zone}, nil
}
func (d ZoneDelete) Zone() string { return d.zone }

func NewZoneDeleteAction(id ActionID, d ZoneDelete) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewZoneDelete(d.zone)
	if err != nil || canonical != d {
		return Action{}, errors.New("invalid zone delete")
	}
	return Action{id: id, kind: ZoneDeleteAction, zoneDelete: d}, nil
}
func (a Action) ZoneDelete() (ZoneDelete, bool) {
	return a.zoneDelete, a.kind == ZoneDeleteAction
}
