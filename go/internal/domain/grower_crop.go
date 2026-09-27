package domain

import "errors"

// GrowerCrop is an immutable, comparable value: a one-shot patch of the crop
// one exact plant grower sows (Building_PlantGrower.SetPlantDefToGrow on
// the native side), CAS-gated by an already-observed exact snapshot token
// the same way BedMedical gates a bed's medical flag. The token covers the
// grower's current crop; there is no pawn/Job involved -- see
// NativeGrowerCrop.cs and bridge/grower_crop.go.
type GrowerCrop struct {
	thing  string
	crop   string
	before string
}

func NewGrowerCrop(thing, crop, before string) (GrowerCrop, error) {
	if !validID(thing) || !validID(crop) || !validID(before) {
		return GrowerCrop{}, errors.New("invalid grower crop identity")
	}
	return GrowerCrop{thing, crop, before}, nil
}
func (g GrowerCrop) Thing() string       { return g.thing }
func (g GrowerCrop) Crop() string        { return g.crop }
func (g GrowerCrop) BeforeToken() string { return g.before }

func NewGrowerCropAction(id ActionID, g GrowerCrop) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewGrowerCrop(g.thing, g.crop, g.before)
	if err != nil || canonical != g {
		return Action{}, errors.New("invalid grower crop")
	}
	return Action{id: id, kind: GrowerCropAction, growerCrop: g}, nil
}
func (a Action) GrowerCrop() (GrowerCrop, bool) {
	return a.growerCrop, a.kind == GrowerCropAction
}
