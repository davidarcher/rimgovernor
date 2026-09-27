package policy

import (
	"bytes"
	"encoding/json"
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// rescueView is holdView after formation with c downed at (3,30): a is a
// shield-belted, unarmed doctor at (1,30), b an armed rifleman next to c.
func rescueView(t *testing.T) (CombatView, CombatMemory) {
	t.Helper()
	view := holdView()
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	view.Tick = 200
	view.Defenders[2].Downed = domain.Known(true)
	view.Pawns[0].ShieldBelt, view.Pawns[0].MedicalSkill = true, 8
	view.Pawns[1].Weapon, view.Pawns[1].MedicalSkill = "Gun_BoltActionRifle", 2
	view.Pawns[2].Downed = true
	return view, memory
}

var downedC = StopEvent{Kind: StopDowned, Pawn: "c"}

// rescueStop runs one stop, answering a rescue_path ask with route and any
// other ask with no proposals, as the caller does.
func rescueStop(t *testing.T, view CombatView, stop StopEvent, memory CombatMemory, route []RouteCell) ([]CombatOrder, CombatMemory) {
	t.Helper()
	orders, ask, next := DecideCombat(view, GeometryReply{}, stop, memory)
	if ask == nil {
		return orders, next
	}
	reply := GeometryReply{Answered: true}
	if ask.Propose == RoleRescuePath {
		if ask.Pawn == "" || ask.To != (domain.Cell{X: 3, Z: 30}) || len(ask.Hostiles) == 0 {
			t.Fatalf("rescue_path ask = %+v", ask)
		}
		reply.Role, reply.Route = RoleRescuePath, route
	}
	orders, again, next := DecideCombat(view, reply, stop, memory)
	if again != nil {
		t.Fatal("a second geometry round trip in one stop")
	}
	return orders, next
}

func ofKind(orders []CombatOrder, kind CombatOrderKind) []CombatOrder {
	var out []CombatOrder
	for _, o := range orders {
		if o.Kind == kind {
			out = append(out, o)
		}
	}
	return out
}

// safeRoute walks from (1,30) to c without crossing a line of fire.
var safeRoute = []RouteCell{{Cell: domain.Cell{X: 1, Z: 31}}, {Cell: domain.Cell{X: 2, Z: 31}}}

// fireRoute crosses a door at (2,29) into a cell under fire.
var fireRoute = []RouteCell{{Cell: domain.Cell{X: 2, Z: 29}, Door: true}, {Cell: domain.Cell{X: 3, Z: 29}, LineOfFire: true}}

func TestDecideCombatRescuerPrefersShieldedDoctor(t *testing.T) {
	for _, tc := range []struct {
		name  string
		setup func(*CombatView)
		want  domain.PawnID
	}{
		{"shielded doctor over nearer rifleman", func(*CombatView) {}, "a"},
		{"no belt: nearest", func(v *CombatView) { v.Pawns[0].ShieldBelt = false }, "b"},
		{"belt but no doctor: nearest", func(v *CombatView) { v.Pawns[0].MedicalSkill = 3 }, "b"},
		{"doctor not orderable: nearest", func(v *CombatView) { v.Orderable = []domain.PawnID{"b", "c"} }, "b"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			view, memory := rescueView(t)
			tc.setup(&view)
			orders, next := rescueStop(t, view, downedC, memory, safeRoute)
			rescues := ofKind(orders, OrderRescue)
			if len(rescues) != 1 || rescues[0].Pawn != tc.want || rescues[0].Target != "c" || rescues[0].Reason != ReasonRescue {
				t.Fatalf("rescue orders = %+v, want %s rescues c", rescues, tc.want)
			}
			for _, o := range orders {
				if o.Pawn == tc.want && o.Kind != OrderRescue {
					t.Fatalf("the rescuer also got its role's order %+v", o)
				}
			}
			if next.Rescue == nil || next.Rescue.Rescuer != tc.want || next.Rescue.Ordered != view.Tick {
				t.Fatalf("rescue memory = %+v", next.Rescue)
			}
		})
	}
}

func TestDecideCombatRescuePathAvoidsLineOfFire(t *testing.T) {
	view, memory := rescueView(t)
	for _, tc := range []struct {
		name  string
		route []RouteCell
	}{
		{"fire on the route", []RouteCell{{Cell: domain.Cell{X: 2, Z: 31}, LineOfFire: true}}},
		{"no path", nil},
		{"route cut at the cap", []RouteCell{{Cell: domain.Cell{X: 1, Z: 31}}}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			orders, next := rescueStop(t, view, downedC, memory, tc.route)
			if len(ofKind(orders, OrderRescue)) != 0 || len(ofKind(orders, OrderDoor)) != 0 {
				t.Fatalf("orders = %+v, want no rescue", orders)
			}
			if next.Rescue == nil || next.Rescue.Patient != "c" || next.Rescue.Rescuer != "" {
				t.Fatalf("rescue memory = %+v, want a pending rescue of c", next.Rescue)
			}
			// The next stop's safe route rescues.
			view.Tick++
			orders, _ = rescueStop(t, view, StopEvent{}, next, safeRoute)
			if r := ofKind(orders, OrderRescue); len(r) != 1 || r[0].Pawn != "a" {
				t.Fatalf("after a safe route: %+v", orders)
			}
		})
	}
}

// doorSequence is the three-stop rescue through a door: forbid, rescue,
// allow.
func doorSequence(t *testing.T) [][]CombatOrder {
	view, memory := rescueView(t)
	first, memory := rescueStop(t, view, downedC, memory, fireRoute)
	view.Tick = 300
	second, memory := rescueStop(t, view, StopEvent{}, memory, safeRoute)
	view.Tick = 400
	view.Pawns[0].Job = rescueJob
	running, memory := rescueStop(t, view, StopEvent{}, memory, nil)
	view.Tick = 500
	view.Pawns[0].Job = "Wait_Combat"
	last, memory := rescueStop(t, view, StopEvent{}, memory, nil)
	if memory.Rescue != nil {
		t.Fatalf("rescue still open: %+v", memory.Rescue)
	}
	return [][]CombatOrder{first, second, running, last}
}

func TestDecideCombatForbidsFightDoorsDuringRescue(t *testing.T) {
	stops := doorSequence(t)
	door := domain.Cell{X: 2, Z: 29}
	if d := ofKind(stops[0], OrderDoor); len(d) != 1 || d[0].Cell != door || d[0].Door != DoorForbid || d[0].Pawn != "" || len(ofKind(stops[0], OrderRescue)) != 0 {
		t.Fatalf("stop 1 = %+v, want the door forbidden and no rescue", stops[0])
	}
	if len(ofKind(stops[1], OrderRescue)) != 1 || len(ofKind(stops[1], OrderDoor)) != 0 {
		t.Fatalf("stop 2 = %+v, want the rescue", stops[1])
	}
	if len(ofKind(stops[2], OrderDoor)) != 0 || len(ofKind(stops[2], OrderRescue)) != 0 {
		t.Fatalf("stop 3 = %+v, want the rescue left running", stops[2])
	}
	if d := ofKind(stops[3], OrderDoor); len(d) != 1 || d[0].Cell != door || d[0].Door != DoorAllow {
		t.Fatalf("stop 4 = %+v, want the door allowed", stops[3])
	}
}

func TestDecideCombatRescueEndsWhenPatientUp(t *testing.T) {
	view, memory := rescueView(t)
	_, memory = rescueStop(t, view, downedC, memory, fireRoute)
	view.Tick = 300
	view.Pawns[2].Downed = false
	orders, next := rescueStop(t, view, StopEvent{}, memory, nil)
	if d := ofKind(orders, OrderDoor); len(d) != 1 || d[0].Door != DoorAllow || next.Rescue != nil {
		t.Fatalf("orders = %+v rescue = %+v, want the door allowed and the rescue closed", orders, next.Rescue)
	}
}

// TestDecideCombatRescueSnapshot pins the door sequence's orders to
// testdata/combat-rescue-doors.json; RIMGOVERNOR_UPDATE_SNAPSHOT=1 rewrites it.
func TestDecideCombatRescueSnapshot(t *testing.T) {
	got, err := json.MarshalIndent(doorSequence(t), "", " ")
	if err != nil {
		t.Fatal(err)
	}
	got = append(got, '\n')
	const path = "testdata/combat-rescue-doors.json"
	if os.Getenv("RIMGOVERNOR_UPDATE_SNAPSHOT") == "1" {
		if err := os.WriteFile(path, got, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	want, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(bytes.ReplaceAll(want, []byte("\r\n"), []byte("\n")), got) {
		t.Fatalf("door sequence changed:\n%s\nwant:\n%s", got, want)
	}
}
