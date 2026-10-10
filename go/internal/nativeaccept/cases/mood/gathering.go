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
		Name: "mood/gathering",
		Scope: "HoldGatherings starts exactly one party after a colony-wide mood dip (#2549, #2548): five Core-only colonists " +
			"each carrying a 9-point negative memory (at least the 8 points AttendedParty repays) and a built PartySpot, no " +
			"AttendedParty memory anywhere. The journal records one completed gathering action and no second; a native read " +
			"counts exactly one running lord job of the Party GatheringDef whose spot is the PartySpot, read while the party runs. " +
			"Native because only RimWorld's lord manager shows that a gathering exists.",
		Start:       cases.Lab{Colonists: 5},
		Serve:       &cases.ServeSpec{Families: epicFamilies(routinefamily.Gathering), Prefix: "mood-gathering"},
		RequiredOps: []string{thoughtsOp, partySpotOp, lordsOp},
		Budget:      8 * time.Minute,
		Crew:        cases.Crew{Size: 5}, Run: runGathering,
	})
}

const (
	partySpotOp = "test/party_spot"
	lordsOp     = "test/gathering_lords"
	dipLoss     = 9.0
)

func runGathering(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	spot, err := fixtureCall(ctx, h, "party-spot", partySpotOp, nil)
	if err != nil {
		return err
	}
	report["party_spot"] = spot
	dip, err := fixtureCall(ctx, h, "dip", thoughtsOp, map[string]any{"pawn": "", "thought": freeThought, "loss": dipLoss})
	if err != nil {
		return err
	}
	report["dip"] = dip
	for _, raw := range na.AsSlice(dip["pawns"]) {
		row, _ := na.AsMap(raw)
		found := false
		for _, m := range na.AsSlice(row["memories"]) {
			memory, _ := na.AsMap(m)
			found = found || na.AsString(memory["def"]) == freeThought && near(na.AsNumber(memory["offset"]), -dipLoss, .05)
		}
		if !found {
			return fmt.Errorf("a colonist lacks the %.0f-point dip: %#v", dipLoss, row)
		}
	}
	// The preconditions the case needs, read natively before the controller
	// starts: a refused gathering then names its gate, never the planner.
	before, err := fixtureCall(ctx, h, "lords-before", lordsOp, map[string]any{"def": policy.PartyGatheringDef})
	if err != nil {
		return err
	}
	report["lords_before"] = before
	if na.AsNumber(before["colonists"]) < policy.GatheringMinColonists || na.AsNumber(before["spots"]) != 1 ||
		na.AsNumber(before["lords"]) != 0 || na.AsNumber(before["attendedMemories"]) != 0 {
		return fmt.Errorf("gathering fixture is not set: %#v", before)
	}
	if ok, _ := na.AsBool(before["canExecute"]); !ok {
		return fmt.Errorf("the game cannot hold a Party now (hour %v, game conditions ok: %v): %#v", before["hour"], before["acceptable"], before)
	}

	d := &drive{s: s}
	if err = d.start(ctx); err != nil {
		return err
	}
	// gatherings are the gathering plans the journal holds, by completion.
	gatherings := func(ctx context.Context) (completed, other int, err error) {
		plans, err := d.journal.PlanHistoryWithMethods(ctx, 256, "gathering-*")
		if err != nil {
			return 0, 0, err
		}
		for _, p := range plans {
			for _, progress := range p.Progress {
				if _, ok := progress.Action().Gathering(); !ok {
					continue
				}
				if progress.View().Stage == domain.Completed {
					completed++
				} else {
					other++
				}
			}
		}
		return completed, other, nil
	}
	if err = d.wait(ctx, 5*time.Minute, func(ctx context.Context) (string, bool, error) {
		completed, other, err := gatherings(ctx)
		return fmt.Sprint(completed, other), completed > 0, err
	}); err != nil {
		return err
	}
	// The journal holds one gathering action and no second. The native read
	// follows at once, while the party runs: its lord job ends on its own
	// duration, so a later read finds none, and stopping the service closes
	// the journal.
	if completed, other, err := gatherings(ctx); err != nil {
		return err
	} else if completed+other != 1 {
		return fmt.Errorf("%d completed and %d other gathering actions, want exactly one", completed, other)
	}
	// One lord job of the Party def, on the PartySpot.
	h, err = d.native(ctx, "lords")
	if err != nil {
		return err
	}
	after, err := fixtureCall(ctx, h, "lords-after", lordsOp, map[string]any{"def": policy.PartyGatheringDef})
	if err != nil {
		return err
	}
	report["lords_after"] = after
	if na.AsNumber(after["lords"]) != 1 {
		return fmt.Errorf("expected exactly one running Party lord job, got %v: %#v", after["lords"], after)
	}
	if na.AsNumber(after["spotX"]) != na.AsNumber(spot["x"]) || na.AsNumber(after["spotZ"]) != na.AsNumber(spot["z"]) {
		return fmt.Errorf("the party is not on the PartySpot %v,%v: %#v", spot["x"], spot["z"], after)
	}
	if na.AsString(after["organizer"]) == "" {
		return fmt.Errorf("the party has no organizer: %#v", after)
	}
	return nil
}
