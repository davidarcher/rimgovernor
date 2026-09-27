package nativeaccept

import (
	"fmt"

	_ "modernc.org/sqlite"
)

func explicitFalse(v any) bool {
	b, ok := AsBool(v)
	return ok && !b
}

// RequireHealthyColonists asserts a fresh observations_list_pawns reply
// (filter colonist=true) is a complete, fully-matched, fully-readable census
// with none dead, downed, bleeding, or needing tend: a missing health fact
// is never treated as healthy, only an explicit false is. Pawns the colonist
// filter dropped (animals, prisoners) are expected, not a defect.
func RequireHealthyColonists(reply map[string]any) error {
	_, observed, err := Outcome(reply, "observed")
	if err != nil {
		return err
	}
	pawns := AsSlice(observed["pawns"])
	if len(pawns) == 0 {
		return fmt.Errorf("no colonists returned")
	}
	for _, raw := range pawns {
		pawn, _ := AsMap(raw)
		health, _ := AsMap(pawn["health"])
		var blocked []string
		for _, field := range []struct {
			name  string
			value any
		}{
			{"dead", pawn["dead"]}, {"downed", pawn["downed"]},
			{"bleeding", health["bleeding"]}, {"needsTend", health["needsTend"]},
		} {
			if !explicitFalse(field.value) {
				blocked = append(blocked, field.name)
			}
		}
		if len(blocked) > 0 {
			pawnRef, _ := AsMap(pawn["pawn"])
			return fmt.Errorf("healthy-clock fixture prerequisite failed for %s: %v", AsString(pawnRef["id"]), blocked)
		}
	}
	return nil
}
