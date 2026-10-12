package snapshot

import (
	"fmt"
	"os"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Layout decision points the defensive layout planner records.
const (
	// LayoutPropose is the chokepoint choice: Request carries the lines of
	// fire the proposal verified, replayed by policy.DefenseLayouts.
	LayoutPropose = "propose"
	// LayoutTurrets is the turret tier probe: Request and Geometry,
	// replayed by policy.DefenseTurrets.
	LayoutTurrets = "turrets"
	// LayoutCover is the cover census: Request, Record and Site, replayed
	// by policy.DefenseApproachesFor over the record's layout.
	LayoutCover = "cover"
)

// NativeLine is one native line-of-sight answer (observations_read_lines_of_fire)
// recorded beside the request it was read against, from before the planner
// computed sight in Go. It is evidence only: the planner no longer reads or
// records lines, and the policy's line of sight is checked against these.
type NativeLine struct {
	From, To    domain.Cell
	LineOfSight domain.Fact[bool]
}

// Layout is one decision of the defensive layout planner as recorded: the
// policy request it built from the native defense-site and combat-pawn
// reads, and the stored layout it decided against.
type Layout struct {
	Recorded string
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Point    string
	Request  policy.DefenseRequest
	Geometry policy.DefenseGeometry
	Record   *store.DefenseLayoutRecord
	// Site is the defense-site read's cells, which the cover selection
	// consults beside the request (LayoutCover only).
	Site []bridge.DefenseCell
	// NativeLines are the native answers recorded before the Go line of
	// sight replaced the read; only the oldest recordings carry them.
	NativeLines []NativeLine `json:",omitempty"`
}

// RecordLayout writes l into dir as layout-<point>-<tick>.json.
func RecordLayout(dir string, l Layout) error {
	data, err := Encode(l)
	if err != nil {
		return err
	}
	if err = os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	return os.WriteFile(filepath.Join(dir, fmt.Sprintf("layout-%s-%d.json", l.Point, l.Tick)), data, 0o644)
}

// LoadLayout reads a recorded layout decision.
func LoadLayout(path string) (Layout, error) {
	data, err := readFile(path)
	if err != nil {
		return Layout{}, err
	}
	var l Layout
	if err = Decode(data, &l); err != nil {
		return Layout{}, fmt.Errorf("%s: %w", path, err)
	}
	return l, nil
}
