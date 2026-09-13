package clock

import (
	"context"
	"database/sql"
	"errors"
	"math"
	"path/filepath"
	"strings"
	"unicode/utf8"
)

func validateClockWindow(intent Intent) error {
	w := intent.Window
	if w == nil {
		return nil
	}
	if intent.Command.Start == nil || w.Snapshot != intent.Snapshot || w.Tick < 0 || w.MaxTicks == 0 || w.MaxTicks > 1800000 || w.MaxTicks != intent.Command.Start.MaxTicks || int64(w.Tick) > math.MaxInt64-int64(w.MaxTicks) || w.CapturedCursor < 0 {
		return errors.New("invalid finite clock window admission")
	}
	if !utf8.ValidString(w.Profile) || len(w.Profile) > 4096 || strings.ContainsRune(w.Profile, 0) || !filepath.IsAbs(w.Profile) || filepath.Clean(w.Profile) != w.Profile {
		return errors.New("clock window requires a canonical profile path")
	}
	return nil
}
func checkClockWindowProfile(ctx context.Context, tx *sql.Tx, w *WindowAdmission) error {
	if w == nil {
		return nil
	}
	var profile string
	if err := tx.QueryRowContext(ctx, "SELECT profile FROM clock_inbox WHERE singleton=1").Scan(&profile); err != nil {
		return err
	}
	if !sameClockProfile(profile, w.Profile) {
		return ErrConflict
	}
	return nil
}

// The ingestion/review watermark and dispatch share one transaction. Callers
// cannot bypass this check by using the ordinary clock dispatch method.
func checkClockWindowDispatch(ctx context.Context, tx *sql.Tx, w *WindowAdmission) error {
	if w == nil {
		return nil
	}
	replay, err := loadClockReview(ctx, tx, w.Profile)
	if err != nil {
		return err
	}
	state := clockReviewState(replay.head, replay.inbox, replay.inbox.State.Cursor)
	if state.Revision != w.ReviewRevision || state.InboxCursor != w.CapturedCursor || state.ReviewedCursor != w.CapturedCursor || len(state.Holds) != 0 {
		return ErrConflict
	}
	return nil
}
