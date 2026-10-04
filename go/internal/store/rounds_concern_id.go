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

// mintRoundsStandardID names a routine need's goal by world and need, so every
// review revision in one world reuses the row (#1021). A live binding is
// reused before minting, so a mint happens only after an invalidation (tick
// rewind, need dropping out); the generation advances past every existing
// row, so a terminal row is never reopened.
func mintRoundsStandardID(ctx context.Context, tx *sql.Tx, w World, need domain.ConcernID) (domain.ConcernID, error) {
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%s/%d", w.Colony, w.Load, w.Map)))
	for gen := 0; gen < 1<<20; gen++ {
		id := domain.ConcernID(fmt.Sprintf("routine-%x-%s-%d", digest[:8], need, gen))
		if _, err := loadStandard(ctx, tx, id); errors.Is(err, ErrNotFound) {
			return id, nil
		} else if err != nil {
			return "", err
		}
	}
	return "", ErrCapacity
}

// mintRoundsProjectID is mintRoundsStandardID for a Project need: the
// generation advances past every existing row, so a finished Project's
// replacement after a regression is a new row.
func mintRoundsProjectID(ctx context.Context, tx *sql.Tx, w World, need domain.ConcernID) (domain.ProjectID, error) {
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

// roundsStandardOwns reports whether id has the routine-<world>-<need>-<gen> shape.
func roundsStandardOwns(id, need domain.ConcernID) bool {
	return ownsRoundsID(string(id), "routine-", need)
}

// roundsProjectOwns reports whether id has the project-<world>-<need>-<gen>
// shape a Rounds pass mints (a player's is project-player-...).
func roundsProjectOwns(id domain.ProjectID, need domain.ConcernID) bool {
	return ownsRoundsID(string(id), projectIDPrefix, need)
}

func ownsRoundsID(id, prefix string, need domain.ConcernID) bool {
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
