package mood

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
)

func init() {
	cases.Register(cases.Case{
		Name: "mood/ledger",
		Scope: "The mood ledger routes thoughts by owner (#2549, #2538): a colonist carrying SleptOutside (owner MaintainHousing, " +
			"-4) and MyOrganHarvested (no owner, -6) shows both in the moodLedger of GET /api/player/colony, the first with its " +
			"owner and the second alone in the unowned bucket, each with its pawn count and mood lost. Nightly tier. The native " +
			"side proves only that both memories are present on the pawn; the claim is the controller's own output.",
		Start:       cases.Lab{Colonists: 3},
		Serve:       &cases.ServeSpec{Families: epicFamilies(routinefamily.Mood), Prefix: "mood-ledger"},
		RequiredOps: []string{thoughtsOp},
		Budget:      6 * time.Minute,
		Crew:        cases.Crew{Size: 3}, Run: runLedger,
	})
}

const (
	thoughtsOp   = "test/mood_thoughts"
	ownedThought = "SleptOutside"
	ownedLoss    = 4.0
	owner        = "MaintainHousing"
	freeThought  = "MyOrganHarvested"
	freeLoss     = 6.0
)

func runLedger(ctx context.Context, s cases.Session) error {
	report := s.Report()
	var put map[string]any
	for _, t := range []struct {
		def  string
		loss float64
	}{{ownedThought, ownedLoss}, {freeThought, freeLoss}} {
		var err error
		if put, err = fixtureCall(ctx, s.Harness(), "inject-"+t.def, thoughtsOp, map[string]any{"pawn": "first", "thought": t.def, "loss": t.loss}); err != nil {
			return err
		}
		if rows := na.AsSlice(put["pawns"]); len(rows) != 1 {
			return fmt.Errorf("inject %s: expected one pawn: %#v", t.def, put)
		}
	}
	// Both memories are on the pawn natively, at their requested size (the
	// last reply lists every memory the pawn holds).
	report["injected"] = put
	have := map[string]float64{}
	for _, raw := range na.AsSlice(put["pawns"]) {
		row, _ := na.AsMap(raw)
		for _, m := range na.AsSlice(row["memories"]) {
			memory, _ := na.AsMap(m)
			have[na.AsString(memory["def"])] = na.AsNumber(memory["offset"])
		}
	}
	if !near(have[ownedThought], -ownedLoss, .05) || !near(have[freeThought], -freeLoss, .05) {
		return fmt.Errorf("native memories are not the injected ones: %v", have)
	}

	d := &drive{s: s}
	if err := d.start(ctx); err != nil {
		return err
	}
	defer d.service.Stop()
	var colony map[string]any
	if err := d.wait(ctx, 4*time.Minute, func(ctx context.Context) (string, bool, error) {
		got, err := d.service.Get("GET", "/api/player/colony")
		if err != nil {
			return "", false, err
		}
		colony = got
		ledger, ok := na.AsMap(got["moodLedger"])
		if !ok {
			return "no ledger yet", false, nil
		}
		sources := na.AsSlice(ledger["sources"])
		return fmt.Sprint(len(sources)), find(sources, freeThought) != nil && find(sources, ownedThought) != nil, nil
	}); err != nil {
		return err
	}
	ledger, _ := na.AsMap(colony["moodLedger"])
	report["mood_ledger"] = ledger
	sources, unowned := na.AsSlice(ledger["sources"]), na.AsSlice(ledger["unowned"])

	owned, free := find(sources, ownedThought), find(sources, freeThought)
	if owners := na.AsSlice(owned["owners"]); len(owners) != 1 || na.AsString(owners[0]) != owner {
		return fmt.Errorf("%s owners = %#v, want [%s]", ownedThought, owned["owners"], owner)
	}
	if owners := na.AsSlice(free["owners"]); len(owners) != 0 {
		return fmt.Errorf("%s owners = %#v, want none", freeThought, free["owners"])
	}
	for _, c := range []struct {
		name string
		row  map[string]any
		loss float64
	}{{ownedThought, owned, ownedLoss}, {freeThought, free, freeLoss}} {
		if na.AsNumber(c.row["pawns"]) != 1 || !near(abs(na.AsNumber(c.row["lost"])), c.loss, .05) {
			return fmt.Errorf("%s = %#v, want one pawn losing %.1f", c.name, c.row, c.loss)
		}
	}
	if find(unowned, freeThought) == nil {
		return fmt.Errorf("%s missing from the unowned bucket: %#v", freeThought, unowned)
	}
	if find(unowned, ownedThought) != nil {
		return fmt.Errorf("%s (owner %s) landed in the unowned bucket: %#v", ownedThought, owner, unowned)
	}
	if indexOf(sources, freeThought) > indexOf(sources, ownedThought) {
		return fmt.Errorf("sources are not ranked by mood lost: %#v", sources)
	}
	return nil
}

func abs(v float64) float64 {
	if v < 0 {
		return -v
	}
	return v
}

// find is the ledger row for def, nil when absent.
func find(rows []any, def string) map[string]any {
	if i := indexOf(rows, def); i >= 0 {
		row, _ := na.AsMap(rows[i])
		return row
	}
	return nil
}

func indexOf(rows []any, def string) int {
	for i, raw := range rows {
		if row, _ := na.AsMap(raw); na.AsString(row["def"]) == def {
			return i
		}
	}
	return -1
}
