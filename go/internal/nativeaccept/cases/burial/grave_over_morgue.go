package burial

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const (
	burialStage = "test/burial_stage"
	burialRead  = "test/burial_read"
	// burialWindow bounds the vanilla haul: the grave is a few cells from the
	// morgue and the hauler is idle.
	burialWindow = 2 * na.TicksPerDay
)

func init() {
	cases.Register(cases.Case{
		Name: "burial/grave_over_morgue",
		Scope: "Issue #2196 grave-versus-zone ranking: a morgue zone at policy.MorguePriority holding a colonist and a stranger corpse " +
			"beside a free grave. Vanilla hauling carries the colonist corpse into the grave, whose storage priority outranks the morgue, " +
			"and leaves the stranger, whom the grave refuses, in the morgue. A native read of vanilla stockpile ranking and hauling; " +
			"no Go snapshot covers it.",
		Start:       cases.Fixture{Op: burialStage, Args: map[string]any{"priority": string(domain.NormalPriority)}, On: cases.LabStart()},
		RequiredOps: []string{burialStage, burialRead},
		QuietWorld:  true,
		Budget:      5 * time.Minute,
		Crew:        cases.Crew{Size: 3}, Run: run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	prepared := s.Prepared()
	colonist, stranger := na.AsString(prepared["colonistCorpse"]), na.AsString(prepared["strangerCorpse"])
	if colonist == "" || stranger == "" || na.AsString(prepared["grave"]) == "" {
		return fmt.Errorf("prepare: missing fixture ids: %#v", prepared)
	}
	if policy.MorguePriority != domain.NormalPriority {
		return fmt.Errorf("the case stages the morgue at %s but policy.MorguePriority is %s", domain.NormalPriority, policy.MorguePriority)
	}
	read := func(label string) (colonistInGrave, strangerInZone bool, err error) {
		reply, err := h.Call(ctx, label, burialRead, map[string]any{"ids": colonist + "," + stranger})
		if err != nil {
			return false, false, err
		}
		for _, raw := range na.AsSlice(reply["things"]) {
			row, _ := na.AsMap(raw)
			inGrave, _ := na.AsBool(row["inGrave"])
			inZone, _ := na.AsBool(row["zone"])
			switch na.AsString(row["id"]) {
			case colonist:
				colonistInGrave = inGrave
			case stranger:
				if inGrave {
					return false, false, fmt.Errorf("%s: the stranger corpse was interred in a colonist grave: %#v", label, row)
				}
				strangerInZone = inZone
			}
		}
		return colonistInGrave, strangerInZone, nil
	}
	for advanced := 0; ; advanced += 300 {
		inGrave, _, err := read(fmt.Sprintf("burial-read-%d", advanced))
		if err != nil {
			return err
		}
		if inGrave {
			report["burial_ticks"] = advanced
			break
		}
		if advanced >= burialWindow {
			return fmt.Errorf("the colonist corpse was not hauled into the grave within %d ticks: the morgue zone outranks the grave", advanced)
		}
		if _, err := s.Advance(ctx, 300); err != nil {
			return err
		}
	}
	// The hauler had every chance at the stranger too: it stays in the morgue.
	if _, err := s.Advance(ctx, 600); err != nil {
		return err
	}
	_, strangerInZone, err := read("burial-read-final")
	if err != nil {
		return err
	}
	if !strangerInZone {
		return fmt.Errorf("the stranger corpse left the morgue zone")
	}
	report["stranger_stays_in_morgue"] = true
	return nil
}
