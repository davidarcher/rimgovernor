package stats

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"slices"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/bridge/stateval"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name: "stats/evaluator-parity",
		Scope: "The Go stat evaluator (#2636) agrees with the game's own EvaluateStat on a sample of definition requests: the same shown flag " +
			"and the same float32 value for every (def, stuff, stat) it can evaluate; a stat with a class not ported yet is skipped, never defaulted.",
		Start: cases.LabStart(),
		// Boot, one catalog read and at most evaluatorParityCalls small reads on a kept process.
		Budget: 5 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: evaluatorParity,
	})
}

// evaluatorParityCalls caps the native reads of one run.
const evaluatorParityCalls = 400

func evaluatorParity(ctx context.Context, s cases.Session) error {
	client := s.Harness().Client
	catalog, err := cases.Catalog(ctx, client, s.Identity())
	if err != nil {
		return err
	}
	data, err := json.Marshal(s.Identity())
	if err != nil {
		return err
	}
	id := &c.Identity{}
	if err := protojson.Unmarshal(data, id); err != nil {
		return err
	}
	var stats []string
	for name := range catalog.Defs[(&d.StatDef{}).ProtoReflect().Descriptor().FullName()] {
		stats = append(stats, name)
	}
	slices.Sort(stats)
	subjects := map[[2]string]bool{}
	for _, row := range evaluateStatSample {
		subjects[[2]string{row[0], row[1]}] = true
	}
	eval := stateval.New(catalog, stateval.Env{ActiveMods: stateval.ModsOf(catalog), ScenarioFactors: map[string]float32{}})
	type pair struct {
		def, stuff, stat string
		want             stateval.Result
	}
	var pairs []pair
	skipped := 0
	for subject := range subjects {
		for _, stat := range stats {
			got, err := eval.Evaluate(stat, stateval.ThingSubject(subject[0], subject[1]))
			var nm *bridge.NotMirrored
			switch {
			case errors.As(err, &nm):
				skipped++
			case err != nil:
				return fmt.Errorf("go evaluator %v %s: %w", subject, stat, err)
			default:
				pairs = append(pairs, pair{subject[0], subject[1], stat, got})
			}
		}
	}
	slices.SortFunc(pairs, func(a, b pair) int {
		return slices.Compare([]string{a.def, a.stuff, a.stat}, []string{b.def, b.stuff, b.stat})
	})
	// An even stride over the sorted pairs keeps the sample deterministic.
	stride := max(1, len(pairs)/evaluatorParityCalls)
	compared := 0
	for i := 0; i < len(pairs); i += stride {
		p := pairs[i]
		got, _, err := client.EvaluateStat(ctx, id, p.stat, bridge.StatDefSubject(p.def, p.stuff, nil))
		if err != nil {
			return fmt.Errorf("EvaluateStat %s/%s/%s: %w", p.def, p.stuff, p.stat, err)
		}
		if got.Shown != p.want.Shown {
			return fmt.Errorf("%s/%s/%s: game shown %v, Go %v", p.def, p.stuff, p.stat, got.Shown, p.want.Shown)
		}
		if math.Float32bits(float32(got.Value)) != math.Float32bits(p.want.Value) {
			return fmt.Errorf("%s/%s/%s: game value %v, Go %v", p.def, p.stuff, p.stat, got.Value, p.want.Value)
		}
		compared++
	}
	if compared == 0 {
		return fmt.Errorf("the Go evaluator answered no sampled request (%d skipped as not mirrored)", skipped)
	}
	s.Report()["pairs_compared"] = compared
	s.Report()["pairs_not_mirrored"] = skipped
	return nil
}
