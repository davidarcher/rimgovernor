package snapshot

import (
	"fmt"
	"os"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Firebreak is one MaintainFirebreak review as recorded: the ring
// request it planned from, the dwell clock it carried in, and the plant cut
// census and open wooden ruins it read over the plan. Request.Ground is
// cell-keyed, which the codec cannot carry, so it is recorded as Ground.
type Firebreak struct {
	Recorded string
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Request  policy.FirebreakRequest
	Ground   []FirebreakGround
	Dwell    []FirebreakDwell
	// Standing are the cut cells the plant cut census reported a plant on;
	// Open the planned wooden ruins with no deconstruction designated.
	Standing []domain.Cell
	Open     []domain.Cell
}

type FirebreakGround struct {
	Cell   domain.Cell
	Ground domain.Fact[policy.FirebreakGround]
}

type FirebreakDwell struct {
	Cell  domain.Cell
	Since domain.Tick
}

// NewFirebreak records request, dwell and the census over the plan.
func NewFirebreak(request policy.FirebreakRequest, dwell map[domain.Cell]domain.Tick, standing, open []domain.Cell) Firebreak {
	f := Firebreak{Tick: request.Now, Request: request, Standing: standing, Open: open}
	f.Request.Ground = nil
	for c, g := range request.Ground {
		f.Ground = append(f.Ground, FirebreakGround{c, g})
	}
	for c, t := range dwell {
		f.Dwell = append(f.Dwell, FirebreakDwell{c, t})
	}
	return f
}

// Replay plans the ring from the recorded request and dwell and returns
// the plan and the work the recorded census leaves owed.
func (f Firebreak) Replay() (policy.FirebreakPlan, policy.FirebreakWork, error) {
	r := f.Request
	r.Ground = map[domain.Cell]domain.Fact[policy.FirebreakGround]{}
	for _, g := range f.Ground {
		r.Ground[g.Cell] = g.Ground
	}
	dwell := map[domain.Cell]domain.Tick{}
	for _, d := range f.Dwell {
		dwell[d.Cell] = d.Since
	}
	fact, _, err := policy.PlanFirebreak(r, dwell)
	if err != nil {
		return policy.FirebreakPlan{}, policy.FirebreakWork{}, err
	}
	plan, known := fact.Value()
	if !known {
		return policy.FirebreakPlan{}, policy.FirebreakWork{}, fmt.Errorf("snapshot: firebreak plan unknown")
	}
	return plan, policy.FirebreakOwed(plan, cellSet(f.Standing), cellSet(f.Open)), nil
}

func cellSet(cells []domain.Cell) map[domain.Cell]bool {
	out := make(map[domain.Cell]bool, len(cells))
	for _, c := range cells {
		out[c] = true
	}
	return out
}

// RecordFirebreak writes f into dir as firebreak-<tick>.json.
func RecordFirebreak(dir string, f Firebreak) error {
	data, err := Encode(f)
	if err != nil {
		return err
	}
	if err = os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	return os.WriteFile(filepath.Join(dir, fmt.Sprintf("firebreak-%d.json", f.Tick)), data, 0o644)
}

// LoadFirebreak reads a recorded firebreak review.
func LoadFirebreak(path string) (Firebreak, error) {
	data, err := readFile(path)
	if err != nil {
		return Firebreak{}, err
	}
	var f Firebreak
	if err = Decode(data, &f); err != nil {
		return Firebreak{}, fmt.Errorf("%s: %w", path, err)
	}
	return f, nil
}
