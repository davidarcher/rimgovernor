package store

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// mintRoundsStandardID names a routine need's Standard by world and need, so
// every review revision in one world reuses the row. A live binding is
// reused before minting, so a mint happens only after an invalidation (tick
// rewind, need dropping out); the generation advances past every existing
// row, so a terminal row is never reopened. A Once Standard that regresses
// stays on its row and starts a new Episode instead.
func mintRoundsStandardID(ctx context.Context, tx *Tx, w World, need domain.ConcernID) (domain.StandardID, error) {
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%s/%d", w.Colony, w.Load, w.Map)))
	for gen := 0; gen < 1<<20; gen++ {
		id := domain.StandardID(fmt.Sprintf("routine-%x-%s-%d", digest[:8], need, gen))
		if _, err := loadStandard(ctx, tx, id); errors.Is(err, ErrNotFound) {
			return id, nil
		} else if err != nil {
			return "", err
		}
	}
	return "", ErrCapacity
}
