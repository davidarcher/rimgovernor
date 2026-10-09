package mood

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
)

func init() {
	cases.Register(cases.Case{
		Name: "mood/headroom",
		Scope: "EnsureMood enters and recovers on the headroom margins (#2535, #2549): with one colonist's mood held natively " +
			"0.07 above its native minor-break threshold (inside the 0.10 entry margin, above the old entry at the threshold), the " +
			"journal opens its mood incident; lifted natively to 0.22 (past the 0.15 recovery margin) and the service restarted, " +
			"the same occurrence closes. Nightly tier. Needs are frozen so the margin alone decides. The stay-open band between " +
			"0.10 and 0.15 needs mood history that a restart drops, so policy/mood_margin_test.go pins it. Native because the " +
			"threshold and the held level are RimWorld's.",
		Start:       cases.Lab{Colonists: 3},
		Serve:       &cases.ServeSpec{Families: epicFamilies(routinefamily.Mood), Prefix: "mood-headroom"},
		RequiredOps: []string{headroomOp},
		Keep:        []string{"Mood"},
		Budget:      10 * time.Minute,
		Crew:        cases.Crew{Size: 3}, Run: runHeadroom,
	})
}

const headroomOp = "test/mood_headroom"

func runHeadroom(ctx context.Context, s cases.Session) error {
	report := s.Report()
	pin := func(h *na.Harness, label, pawn string, headroom float64) (string, error) {
		pinned, err := fixtureCall(ctx, h, label, headroomOp, map[string]any{"pawn": pawn, "headroom": headroom})
		if err != nil {
			return "", err
		}
		if got := na.AsNumber(pinned["headroom"]); !near(got, headroom, 0.01) {
			return "", fmt.Errorf("%s: mood held %.3f above the threshold, want %.3f: %#v", label, got, headroom, pinned)
		}
		report[label] = pinned
		return na.AsString(pinned["pawn"]), nil
	}
	pawn, err := pin(s.Harness(), "pin-inside-entry", "", .07)
	if err != nil {
		return err
	}
	d := &drive{s: s}
	// incident is the newest EnsureMood occurrence of the pinned colonist.
	incident := func(ctx context.Context) (domain.Incident, bool, error) {
		_, world, err := d.world(ctx)
		if err != nil {
			return domain.Incident{}, false, err
		}
		history, err := d.journal.IncidentHistory(ctx, world, policy.EnsureMood)
		if err != nil {
			return domain.Incident{}, false, err
		}
		var found domain.Incident
		ok := false
		for _, h := range history {
			if string(h.Incident.Subject) == pawn {
				found, ok = h.Incident, true
			}
		}
		return found, ok, nil
	}

	// Inside the entry margin: the incident opens.
	if err = d.start(ctx); err != nil {
		return err
	}
	if err = d.wait(ctx, 4*time.Minute, func(ctx context.Context) (string, bool, error) {
		r, _, err := d.world(ctx)
		if err != nil {
			return "", false, err
		}
		got, ok, err := incident(ctx)
		return na.Signature(r.Tick, r.Revision, ok), ok && !got.Closed, err
	}); err != nil {
		return fmt.Errorf("entry: %w", err)
	}
	opened, _, _ := incident(ctx)
	report["incident_opened"] = map[string]any{"id": opened.ID, "started": opened.Started, "trigger": opened.Trigger}

	// Above the recovery margin: it closes.
	h, err := d.native(ctx, "recover")
	if err != nil {
		return err
	}
	if _, err = pin(h, "pin-above-recovery", pawn, .22); err != nil {
		return err
	}
	if err = d.start(ctx); err != nil {
		return err
	}
	if err = d.wait(ctx, 4*time.Minute, func(ctx context.Context) (string, bool, error) {
		r, _, err := d.world(ctx)
		if err != nil {
			return "", false, err
		}
		got, ok, err := incident(ctx)
		return na.Signature(r.Tick, r.Revision, got.Closed), ok && got.Closed, err
	}); err != nil {
		return fmt.Errorf("recovery: %w", err)
	}
	closed, _, _ := incident(ctx)
	report["incident_closed"] = map[string]any{"id": closed.ID, "started": closed.Started, "ended": closed.Ended}
	if closed.ID != opened.ID {
		return fmt.Errorf("recovery closed a different occurrence: opened %s, closed %s", opened.ID, closed.ID)
	}
	d.service.Stop()
	return nil
}
