package policy

import (
	"errors"
	"fmt"
	"math"
)

// Containment strength follows the decompiled Assembly-CSharp
// StatWorker_ContainmentStrength calculation:
//
//	strength = holder stat base + facility statOffsets
//	         + (lighting + wallHp + doorHp + floor, each *= 0.9 per other
//	           holder in or beside the room; + roof) * holder containmentFactor
//
// and zero when the room is missing, touches the map edge or has no map. The
// inputs are the game's defs (wall and door MaxHitPoints, the holder's
// containmentFactor, the terrain stat, facility offsets and their limits);
// the constants below are code constants of that worker, which no def or
// native fact carries, so they are named here once and nowhere else.
const (
	// containmentLightingFactor scales the room's mean ground glow.
	containmentLightingFactor = 10.0
	// containmentDoorHPDivisor divides the doors' mean hit points.
	containmentDoorHPDivisor = 5.0
	// containmentOtherHolderFactor multiplies the lighting, wall, door and
	// floor terms once per other holder in or beside the room.
	containmentOtherHolderFactor = 0.9
	// containmentOpenRoofPenalty is the roof term of an indoor room with any
	// open roof cell; a fully roofed room has a zero roof term.
	containmentOpenRoofPenalty = -30.0
)

// containmentWallCurve is the worker's SimpleCurve from the mean hit points
// of the room's wall edifices to the wall term: (0,0), (1000,100),
// (10000,150), clamped at both ends.
var containmentWallCurve = [...][2]float64{{0, 0}, {1000, 100}, {10000, 150}}

func containmentWallTerm(hitPoints float64) float64 {
	c := containmentWallCurve
	if hitPoints <= c[0][0] {
		return c[0][1]
	}
	for i := 1; i < len(c); i++ {
		if hitPoints <= c[i][0] {
			t := (hitPoints - c[i-1][0]) / (c[i][0] - c[i-1][0])
			return c[i-1][1] + t*(c[i][1]-c[i-1][1])
		}
	}
	return c[len(c)-1][1]
}

// ContainmentFacility is one facility def that offsets the holder's
// containment strength (CompProperties_Facility statOffsets on
// ContainmentStrength, linkable to the holder through its
// CompProperties_AffectedByFacilities). The game counts at most
// MaxSimultaneous of one def and only those within MaxDistance; both are the
// def's fields as they are, with no sentinel read into them.
type ContainmentFacility struct {
	Def             string
	Offset          float64
	MaxDistance     float64
	MaxSimultaneous int
}

// ContainmentDefs are the static inputs of the prediction, read from the
// definition catalog (bridge.DefinitionCatalog.ContainmentDefs): the holder
// the planner places, the wall and door the shell builds, the terrain stat
// of a plain floor and the facilities that link to the holder.
type ContainmentDefs struct {
	Holder string
	// HolderFactor is the holder's CompProperties_EntityHolder
	// containmentFactor and HolderBase its ContainmentStrength stat base
	// (the def's own base, else the stat def's default).
	HolderFactor, HolderBase float64
	// WallHP and DoorHP are the MaxHitPoints stat of the shell's wall and
	// door as the planner builds them (def and stuff).
	WallHP, DoorHP float64
	// FloorStrength is the terrain ContainmentStrength stat of the floor the
	// room stands on: the stat's default base for plain terrain.
	FloorStrength float64
	// Floor is the terrain def with the greatest ContainmentStrength stat above
	// the plain floor's, the strength a floored room's floor term takes;
	// its Def is empty when the catalog has none.
	Floor      ContainmentFloor
	Facilities []ContainmentFacility
}

// ContainmentFloor is a terrain that raises containment strength: the def and
// its ContainmentStrength stat value as the catalog's stat table shows it. Its
// cost and availability are the planning definition's (FloorDefinition).
type ContainmentFloor struct {
	Def      string
	Strength float64
}

// ContainmentRoom is the room-dependent input of a prediction.
type ContainmentRoom struct {
	// MeanGlow is the room's mean ground glow, never negative; the planner
	// has no light plan, so it passes 0, the least a room can have.
	MeanGlow float64
	// OpenRoof is an indoor room with an unroofed cell.
	OpenRoof bool
	// OtherHolders counts the other holders in or beside the room.
	OtherHolders int
	// Floored is a room whose every tile is laid with the defs' Floor.
	Floored bool
	// Facilities is how many of each facility def (by name) stand within the
	// def's MaxDistance of the holder.
	Facilities map[string]int
}

// Predict is the holder's containment strength in the room. A definition the
// formula cannot be evaluated from is an error, never a default.
func (d ContainmentDefs) Predict(room ContainmentRoom) (float64, error) {
	switch {
	case d.Holder == "":
		return 0, errors.New("no holder def is set")
	case d.WallHP <= 0 || d.DoorHP <= 0:
		return 0, fmt.Errorf("the wall (%v hit points) or door (%v) has no hit points", d.WallHP, d.DoorHP)
	case room.MeanGlow < 0 || room.OtherHolders < 0:
		return 0, errors.New("a room has no negative glow or holder count")
	}
	floor := d.FloorStrength
	if room.Floored {
		if d.Floor.Def == "" {
			return 0, errors.New("the catalog has no floor that adds containment strength")
		}
		floor = d.Floor.Strength
	}
	terms := room.MeanGlow*containmentLightingFactor + containmentWallTerm(d.WallHP) + d.DoorHP/containmentDoorHPDivisor + floor
	terms *= math.Pow(containmentOtherHolderFactor, float64(room.OtherHolders))
	if room.OpenRoof {
		terms += containmentOpenRoofPenalty
	}
	strength := d.HolderBase + terms*d.HolderFactor
	for _, f := range d.Facilities {
		n := room.Facilities[f.Def]
		n = min(n, f.MaxSimultaneous)
		strength += f.Offset * float64(n)
	}
	return strength, nil
}
