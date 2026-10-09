package domain

import "errors"

// GrowerCrop is an immutable, comparable value: a one-shot patch of the crop
// one exact plant grower sows (Building_PlantGrower.SetPlantDefToGrow on
// the native side; a BuildingPatchIntent); there is no pawn/Job involved -- see
// NativeGrowerCrop.cs and bridge/grower_crop.go.
type GrowerCrop struct {
	thing string
	crop  string
}

func NewGrowerCrop(thing, crop string) (GrowerCrop, error) {
	if !validID(thing) || !validID(crop) {
		return GrowerCrop{}, errors.New("invalid grower crop identity")
	}
	return GrowerCrop{thing, crop}, nil
}
func (g GrowerCrop) Thing() string { return g.thing }
func (g GrowerCrop) Crop() string  { return g.crop }

func NewGrowerCropAction(id ActionID, g GrowerCrop) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewGrowerCrop(g.thing, g.crop)
	if err != nil || canonical != g {
		return Action{}, errors.New("invalid grower crop")
	}
	return Action{id: id, kind: GrowerCropAction, growerCrop: g}, nil
}
func (a Action) GrowerCrop() (GrowerCrop, bool) {
	return a.growerCrop, a.kind == GrowerCropAction
}
