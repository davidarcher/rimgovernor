package wall

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{Name: "wall/deconstruct", Scope: "DECONSTRUCT Designate designates colony targets, adopts player designations and applies again on owned work; revoking authority releases only owned work; a native pawn demolishes an adopted target.",
		Start: cases.Fixture{Op: "test/deconstruct_prepare", On: cases.LabStart()}, Budget: 3 * time.Minute, Crew: cases.Crew{Size: 3}, Run: runDeconstruct})
}

func runDeconstruct(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	identity := s.Identity()
	ids := na.AsSlice(s.Prepared()["ids"])
	if len(ids) != 6 {
		return fmt.Errorf("expected six fixture targets: %#v", s.Prepared())
	}
	mutate := func(label string, i int, action string) (map[string]any, error) {
		return h.Call(ctx, label, "test/deconstruct_target", map[string]any{"target": ids[i], "action": action})
	}
	grant, err := na.GrantAuto(ctx, h.WireFunc(), "designate-authority", identity)
	if err != nil {
		return err
	}
	for _, i := range []int{0, 2, 3, 4} {
		if _, err := applyDeconstruct(ctx, s, fmt.Sprintf("designate-%d", i), ids[i]); err != nil {
			return err
		}
	}
	if _, err := mutate("player-replacement", 2, "replace"); err != nil {
		return err
	}
	if _, err := mutate("external-disappearance", 4, "vanish"); err != nil {
		return err
	}
	// Owned work applies again under a new key.
	if _, err := applyDeconstruct(ctx, s, "designate-3-again", ids[3]); err != nil {
		return err
	}
	if _, err := na.RevokeManual(ctx, h.WireFunc(), "release", identity, grant); err != nil {
		return err
	}
	for _, i := range []int{0, 1, 2, 3} {
		row, err := mutate(fmt.Sprintf("after-release-%d", i), i, "inspect")
		if err != nil {
			return err
		}
		designated, _ := na.AsBool(row["designated"])
		present, _ := na.AsBool(row["present"])
		if !present || designated != (i == 1 || i == 2) {
			return fmt.Errorf("release altered player work or missed own designation: %#v", row)
		}
	}
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "adopt-authority", identity); err != nil {
		return err
	}
	if _, err := mutate("colony-target", 5, "colony"); err != nil {
		return err
	}
	if _, err := mutate("player-designation-to-adopt", 5, "replace"); err != nil {
		return err
	}
	first, err := applyDeconstruct(ctx, s, "adopt-and-finish", ids[5])
	if err != nil {
		return err
	}
	again, err := applyDeconstruct(ctx, s, "adopt-and-finish", ids[5])
	if err != nil {
		return err
	}
	if !na.DeepEqual(first, again) {
		return fmt.Errorf("resent key changed its result: %#v then %#v", first, again)
	}
	for advanced := 0; advanced < 6000; advanced += 300 {
		if _, err = s.Advance(ctx, 300); err != nil {
			return err
		}
		row, err := mutate(fmt.Sprintf("job-%d", advanced), 5, "inspect")
		if err != nil {
			return err
		}
		if present, _ := na.AsBool(row["present"]); !present {
			s.Report()["deconstruction_result"] = first
			s.Report()["demolished_after_ticks"] = advanced + 300
			return nil
		}
	}
	return fmt.Errorf("native deconstruction did not finish within 6000 ticks")
}
