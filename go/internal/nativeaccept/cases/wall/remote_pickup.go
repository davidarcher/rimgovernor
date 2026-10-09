package wall

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:  "wall/remote_pickup",
		Scope: "A construction delivery whose source stack is remote (nearer storage than the site) takes one full carry load and collects a same-def stack inside the n*D/capacity radius but not one outside it; surplus is dropped beside the frame and every item is counted once (#2517). A local delivery with no storage keeps vanilla's exact-need pickup. A Go snapshot cannot prove the Harmony postfix on WorkGiver_ConstructDeliverResources.ResourceDeliverJobFor, pawn carrying or vanilla's surplus drop.",
		Start: cases.LabStart(), RequiredOps: []string{"test/remote_pickup"},
		Budget: 4 * time.Minute, Crew: cases.Crew{Size: 1},
		Run: runRemotePickup,
	})
}

type pickupStack struct{ x, z, count int }

func runRemotePickup(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "remote-pickup-authority", s.Identity()); err != nil {
		return err
	}
	call := func(label, action string) (map[string]any, error) {
		row, err := h.Call(ctx, label, "test/remote_pickup", map[string]any{"action": action})
		if err != nil {
			return nil, err
		}
		if ok, _ := na.AsBool(row["success"]); !ok {
			return nil, fmt.Errorf("remote_pickup %s refused: %#v", action, row)
		}
		return row, nil
	}
	settle := func(phase string, row map[string]any) (map[string]any, error) {
		for ticks := 0; ticks <= 9000; ticks += 300 {
			if na.AsNumber(row["delivered"]) == na.AsNumber(row["cost"]) && na.AsNumber(row["carrying"]) == 0 {
				s.Report()[phase] = row
				return row, nil
			}
			if _, err := s.Advance(ctx, 300); err != nil {
				return nil, err
			}
			var err error
			if row, err = call(fmt.Sprintf("%s-%d", phase, ticks), "audit"); err != nil {
				return nil, err
			}
		}
		return nil, fmt.Errorf("%s delivery did not finish within 9000 ticks: %#v", phase, row)
	}

	row, err := call("stage-remote", "remote")
	if err != nil {
		return err
	}
	capacity := int(na.AsNumber(row["capacity"]))
	// The second stack (20 items, 8 cells from the first, D=40) is worth the
	// detour while 8 < 20*40/capacity; the third (14 cells) is not. The
	// geometry only separates them for a capacity between 58 and 99.
	if capacity < 58 || capacity > 99 {
		return fmt.Errorf("lab carry capacity %d does not separate the staged stacks", capacity)
	}
	if row, err = settle("remote", row); err != nil {
		return err
	}
	remote, centre := pickupStacks(row), pickupCell(row["centre"])
	cost := int(na.AsNumber(row["cost"]))
	surplus := 0
	for _, st := range remote {
		if pickupAbs(st.x-centre[0]) <= 3 && pickupAbs(st.z-centre[1]) <= 3 {
			surplus += st.count
		}
	}
	if pickupAt(remote, centre[0]+40, centre[1]) != 0 || pickupAt(remote, centre[0]+40, centre[1]+8) != 0 {
		return fmt.Errorf("remote pickup left the source or the in-radius stack behind: %#v", remote)
	}
	if got := pickupAt(remote, centre[0]+40, centre[1]-14); got != 20 {
		return fmt.Errorf("stack outside the radius must stay untouched, has %d: %#v", got, remote)
	}
	if want := 15 + 20 - cost; surplus != want {
		return fmt.Errorf("surplus beside the frame is %d, want %d from one full-load trip: %#v", surplus, want, remote)
	}
	if sum := pickupTotal(remote) + int(na.AsNumber(row["delivered"])); sum != 55 {
		return fmt.Errorf("items are not counted exactly once: %d of 55 (%#v)", sum, remote)
	}

	if row, err = call("stage-local", "local"); err != nil {
		return err
	}
	if row, err = settle("local", row); err != nil {
		return err
	}
	local, site := pickupStacks(row), pickupCell(row["site"])
	if pickupAt(local, site[0]+4, site[1]) != 30-cost || pickupAt(local, site[0]+4, site[1]+4) != 20 || pickupTotal(local) != 50-cost {
		return fmt.Errorf("local delivery must take exactly the frame's need from the first stack: %#v", local)
	}
	return nil
}

func pickupStacks(row map[string]any) []pickupStack {
	var out []pickupStack
	list, _ := row["loose"].([]any)
	for _, item := range list {
		m, _ := item.(map[string]any)
		out = append(out, pickupStack{int(na.AsNumber(m["x"])), int(na.AsNumber(m["z"])), int(na.AsNumber(m["count"]))})
	}
	return out
}

func pickupCell(v any) [2]int {
	m, _ := v.(map[string]any)
	return [2]int{int(na.AsNumber(m["x"])), int(na.AsNumber(m["z"]))}
}

func pickupAt(list []pickupStack, x, z int) int {
	for _, st := range list {
		if st.x == x && st.z == z {
			return st.count
		}
	}
	return 0
}

func pickupTotal(list []pickupStack) int {
	sum := 0
	for _, st := range list {
		sum += st.count
	}
	return sum
}

func pickupAbs(v int) int {
	if v < 0 {
		return -v
	}
	return v
}
