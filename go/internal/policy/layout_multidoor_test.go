package policy

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func twoDoorRoom() LayoutRoom {
	return LayoutRoom{
		Role:     ModuleWorkshop,
		Interior: Rectangle{X: 20, Z: 30, Width: 7, Height: 5},
		Door:     domain.Cell{X: 23, Z: 29},
		DoorRot:  domain.South,
		Doors:    []Door{{Cell: domain.Cell{X: 23, Z: 35}, Rot: domain.North}},
	}
}

func TestTwoDoorRoomShellEmitsBothDoorsAndSurvivesClearance(t *testing.T) {
	room := twoDoorRoom()
	shell, err := room.Footprint()
	if err != nil {
		t.Fatal(err)
	}
	placements := shell.Placements("Wall", "Door", "WoodLog")
	doors := map[domain.Cell]domain.Rotation{}
	for _, b := range placements {
		if b.Definition() == "Door" {
			doors[b.Cell()] = b.Rotation()
		}
	}
	if len(doors) != 2 || doors[room.Door] != domain.South || doors[room.Doors[0].Cell] != domain.North {
		t.Fatalf("doors %v", doors)
	}
	if len(placements) != len(shell.Walls()) {
		t.Fatalf("%d placements for %d wall cells", len(placements), len(shell.Walls()))
	}
	plan := LayoutPlan{Rooms: []LayoutRoom{room}}
	planned := PlannedDoors(plan)
	if !planned[room.Door] || !planned[room.Doors[0].Cell] || len(planned) != 2 {
		t.Fatalf("planned doors %v", planned)
	}
	ring := plan.ShellDoors(room)
	if len(ring) != 2 || ring[0] != room.Door || ring[1] != room.Doors[0].Cell {
		t.Fatalf("shell doors %v", ring)
	}
}

func TestTransposeRoomRoundTripsExtraDoors(t *testing.T) {
	room := twoDoorRoom()
	back := transposeRoom(transposeRoom(room))
	if !back.Same(room) {
		t.Fatalf("round trip %+v != %+v", back, room)
	}
	once := transposeRoom(room)
	if once.Doors[0].Cell != (domain.Cell{X: 35, Z: 23}) || once.Doors[0].Rot != domain.East || room.Doors[0].Rot != domain.North {
		t.Fatalf("transposed %+v (source %+v)", once.Doors, room.Doors)
	}
}

func TestSingleDoorRoomJSONHasNoDoorsKey(t *testing.T) {
	room := twoDoorRoom()
	room.Doors = nil
	raw, err := json.Marshal(room)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(raw), "Doors") {
		t.Fatal(string(raw))
	}
	var back LayoutRoom
	if err := json.Unmarshal(raw, &back); err != nil || !back.Same(room) || back.Doors != nil {
		t.Fatal(err, back)
	}
}
