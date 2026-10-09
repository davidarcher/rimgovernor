package snapshot

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Defense is one RoundsDefensePlanner step as recorded: the native
// replies the step read (the emergency census, the combat pawn rows and,
// for a hostile building, the lines of fire), the layout record it held
// the line against, and what it decided. A threat response is not a
// rounds, so the routine snapshot does not carry these inputs.
type Defense struct {
	Recorded string
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	// Layout is the stored defensive layout the step read; nil when the
	// colony has none.
	Layout    *store.DefenseLayoutRecord
	Emergency policy.EmergencyFacts
	// EmergencyContext, CombatPawns and LinesContext are protojson: the
	// emergency read's ObservationContext, the ListPawnsReply of the combat
	// read and the lines-of-fire read's context. CombatPawns is absent when
	// the step stopped before the combat read.
	EmergencyContext json.RawMessage
	CombatPawns      json.RawMessage
	LinesContext     json.RawMessage
	Lines            []bridge.LineOfFire
	// Reason and Plan are the step's result; Method is the method the
	// admitted plan was committed under.
	Reason string
	Plan   domain.PlanID
	Method domain.MethodID
}

// RecordDefense writes d into dir as defense-<tick>-<reason>.json; a later
// step at the same tick with the same result replaces it.
func RecordDefense(dir string, d Defense) error {
	data, err := Encode(d)
	if err != nil {
		return err
	}
	if err = os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	return os.WriteFile(filepath.Join(dir, fmt.Sprintf("defense-%d-%s.json", d.Tick, d.Reason)), data, 0o644)
}

// LoadDefense reads a recorded defense step.
func LoadDefense(path string) (Defense, error) {
	data, err := readFile(path)
	if err != nil {
		return Defense{}, err
	}
	var d Defense
	if err = Decode(data, &d); err != nil {
		return Defense{}, fmt.Errorf("%s: %w", path, err)
	}
	return d, nil
}
