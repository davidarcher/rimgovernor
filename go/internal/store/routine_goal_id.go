package store

import (
	"context"
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"errors"
	"fmt"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// mintRoutineGoalID names a routine need's goal by world and need, so every
// review revision in one world reuses the row (#1021). A live binding is
// reused before minting, so a mint happens only after an invalidation (tick
// rewind, need dropping out); the generation advances past every existing
// row, so a terminal row is never reopened.
func mintRoutineGoalID(ctx context.Context, tx *sql.Tx, w World, need domain.GoalID) (domain.GoalID, error) {
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%s/%d", w.Colony, w.Load, w.Map)))
	for gen := 0; gen < 1<<20; gen++ {
		id := domain.GoalID(fmt.Sprintf("routine-%x-%s-%d", digest[:8], need, gen))
		if _, err := loadGoal(ctx, tx, id); errors.Is(err, ErrNotFound) {
			return id, nil
		} else if err != nil {
			return "", err
		}
	}
	return "", ErrCapacity
}

// mintRoutineProjectID is mintRoutineGoalID for a Project need: the
// generation advances past every existing row, so a finished Project's
// replacement after a regression is a new row.
func mintRoutineProjectID(ctx context.Context, tx *sql.Tx, w World, need domain.GoalID) (domain.ProjectID, error) {
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%s/%d", w.Colony, w.Load, w.Map)))
	for gen := 0; gen < 1<<20; gen++ {
		id := domain.ProjectID(fmt.Sprintf("project-%x-%s-%d", digest[:8], need, gen))
		if _, err := loadProject(ctx, tx, id); errors.Is(err, ErrNotFound) {
			return id, nil
		} else if err != nil {
			return "", err
		}
	}
	return "", ErrCapacity
}

// routineGoalOwns reports whether id has the routine-<world>-<need>-<gen> shape.
func routineGoalOwns(id, need domain.GoalID) bool {
	return ownsRoutineID(string(id), "routine-", need)
}

// routineProjectOwns reports whether id has the project-<world>-<need>-<gen>
// shape a routine review mints (a player's is project-player-...).
func routineProjectOwns(id domain.ProjectID, need domain.GoalID) bool {
	return ownsRoutineID(string(id), projectIDPrefix, need)
}

func ownsRoutineID(id, prefix string, need domain.GoalID) bool {
	rest, ok := strings.CutPrefix(id, prefix)
	if !ok || len(rest) < 17 || rest[16] != '-' {
		return false
	}
	if _, err := hex.DecodeString(rest[:16]); err != nil {
		return false
	}
	gen, ok := strings.CutPrefix(rest[17:], string(need)+"-")
	if !ok || gen == "" {
		return false
	}
	n, err := strconv.ParseUint(gen, 10, 32)
	return err == nil && strconv.FormatUint(n, 10) == gen
}
