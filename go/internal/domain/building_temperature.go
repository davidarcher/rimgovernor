package domain

import (
	"errors"
	"math"
)

const minTemperatureCelsius = -273.15
const maxTemperatureCelsius = 1000

// BuildingTemperature is an immutable, comparable value: a one-shot patch of
// a temperature-controlled building's target setpoint (CompTempControl on
// the native side; a BuildingPatchIntent since #940). There
// is no pawn/Job involved -- see NativeBuildingTemperature.cs and
// bridge/building_temperature_target.go for the native and read-side halves.
type BuildingTemperature struct {
	thing   string
	celsius float64
}

func NewBuildingTemperature(thing string, celsius float64) (BuildingTemperature, error) {
	if !validID(thing) {
		return BuildingTemperature{}, errors.New("invalid building temperature identity")
	}
	if math.IsNaN(celsius) || math.IsInf(celsius, 0) || celsius < minTemperatureCelsius || celsius > maxTemperatureCelsius {
		return BuildingTemperature{}, errors.New("building temperature out of range")
	}
	return BuildingTemperature{thing, celsius}, nil
}
func (b BuildingTemperature) Thing() string    { return b.thing }
func (b BuildingTemperature) Celsius() float64 { return b.celsius }

func NewBuildingTemperatureAction(id ActionID, b BuildingTemperature) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewBuildingTemperature(b.thing, b.celsius)
	if err != nil || canonical != b {
		return Action{}, errors.New("invalid building temperature")
	}
	return Action{id: id, kind: BuildingTemperatureAction, buildingTemperature: b}, nil
}
func (a Action) BuildingTemperature() (BuildingTemperature, bool) {
	return a.buildingTemperature, a.kind == BuildingTemperatureAction
}
