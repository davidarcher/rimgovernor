package food

import (
	"context"
	"fmt"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const (
	ledgerPrepareOp = "test/ledger_prepare"
	ledgerObserveOp = "test/ledger_observe"
)

func init() {
	cases.Register(cases.Case{Name: "food/ledger",
		Scope:        "Native read contract and end-to-end signal (a Go snapshot test cannot cover native hooks): the colony facts delivery ledger's fish and rice counters equal the fixture's independent tally of Notify_Fished catches and rice yields, and a gathered cow yields a milk counter; counters never decrease within an epoch.",
		Start:        cases.Fixture{On: cases.DebugStart{Size: na.DebugStart{MapSize: 150, PlanetCoverage: 0.05, Biomes: "TropicalRainforest", Seed: "fishing-426"}}, Op: ledgerPrepareOp},
		NoCheckpoint: true, RequiredOps: []string{ledgerObserveOp}, Budget: 10 * time.Minute, Crew: cases.Crew{Size: 3},
		Reason: "an Odyssey coast needs the fishing case's 150-tile start; a few native days of fishing, harvest and milking on a programmatic coast with a mature rice field",
		Run:    runLedger})
}

func runLedger(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	tally := func(label string) (map[string]any, error) { return h.Call(ctx, label, ledgerObserveOp, nil) }
	_, err := na.RunUntil(ctx, h, "ledger-deliveries", 2500, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		v, err := tally("ledger-progress")
		if err != nil {
			return "", false, err
		}
		s.Report()["ledger_tally"] = v
		return na.Signature(v["tick"]), na.AsNumber(v["catches"]) > 0 && na.AsNumber(v["riceYield"]) > 0 && na.AsNumber(v["gathers"]) > 0, nil
	})
	if err != nil {
		return err
	}
	before, err := tally("ledger-before")
	if err != nil {
		return err
	}
	ledger, err := readLedger(ctx, s, "ledger-facts")
	if err != nil {
		return err
	}
	after, err := tally("ledger-after")
	if err != nil {
		return err
	}
	s.Report()["ledger"] = map[string]any{"before": before, "after": after, "ledger": ledger.raw}
	if ledger.epoch == "" {
		return fmt.Errorf("ledger epoch missing: %v", ledger.raw)
	}
	// The ledger may have counted a delivery between the two tallies; it is never outside them.
	for _, c := range []struct {
		name, kind, def, key string
	}{{"fish", "FISH", "", "catches"}, {"rice", "CROP", "RawRice", "riceYield"}} {
		units := ledger.units(c.kind, c.def)
		low, high := na.AsNumber(before[c.key]), na.AsNumber(after[c.key])
		if units < low || units > high || units <= 0 {
			return fmt.Errorf("%s ledger units %v are outside the independent tally [%v, %v]", c.name, units, low, high)
		}
	}
	if milk := ledger.units("ANIMAL_PRODUCT", "Milk"); milk <= 0 {
		return fmt.Errorf("no milk counted for a gathered cow: %v", ledger.raw)
	}
	second, err := readLedger(ctx, s, "ledger-facts-again")
	if err != nil {
		return err
	}
	if second.epoch != ledger.epoch {
		return fmt.Errorf("epoch changed without a load: %q -> %q", ledger.epoch, second.epoch)
	}
	for kind, rows := range ledger.rows {
		for key, units := range rows {
			if second.rows[kind][key] < units {
				return fmt.Errorf("counter %s/%s decreased: %v -> %v", kind, key, units, second.rows[kind][key])
			}
		}
	}
	return nil
}

type ledgerRead struct {
	epoch string
	raw   map[string]any
	// rows is units by source kind, then "source|def".
	rows map[string]map[string]float64
}

// units totals a kind's rows, restricted to one def when def is set.
func (l ledgerRead) units(kind, def string) float64 {
	total := 0.0
	for key, units := range l.rows[kind] {
		if def == "" || strings.HasSuffix(key, "|"+def) {
			total += units
		}
	}
	return total
}

func readLedger(ctx context.Context, s cases.Session, label string) (ledgerRead, error) {
	reply, err := s.Harness().Wire(ctx, label, "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}})
	if err != nil {
		return ledgerRead{}, err
	}
	_, facts, err := na.Outcome(reply, "observed")
	if err != nil {
		return ledgerRead{}, err
	}
	section, _ := na.AsMap(facts["deliveryLedger"])
	observed, _ := na.AsMap(section["observed"])
	if observed == nil {
		return ledgerRead{}, fmt.Errorf("delivery ledger is not observed: %v", section)
	}
	read := ledgerRead{raw: observed, rows: map[string]map[string]float64{}}
	read.epoch, _ = observed["epoch"].(string)
	for _, raw := range na.AsSlice(observed["rows"]) {
		row, _ := na.AsMap(raw)
		kind := strings.TrimPrefix(fmt.Sprint(row["sourceKind"]), "DELIVERY_SOURCE_KIND_")
		if read.rows[kind] == nil {
			read.rows[kind] = map[string]float64{}
		}
		read.rows[kind][fmt.Sprint(row["sourceId"])+"|"+fmt.Sprint(row["defName"])] += na.AsNumber(row["units"])
	}
	return read, nil
}
