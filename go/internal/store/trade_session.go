package store

import (
	"context"
	"database/sql"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TradeSession is the {SessionId, SessionToken} pair OpenTrade's own
// completion evidence produced (bridge/trade.go's tradeOpenEvidence:
// EffectEvidence.Trade.SessionId plus the session snapshot's AfterToken).
// It is durably keyed by the Open action's identity: there is no native RPC
// that can freshly read "the currently open session" (see
// NativeTradeOperations.cs -- SessionId/SessionToken() are session-local C#
// fields populated only by OpenTrade's own Execute), so this record is the
// only way a later same-plan set_lines/accept/end Trade action -- chained to
// Open via domain.ActionDependency -- can address the session Open just
// created. It is genuinely new persistence: no existing generic mechanism
// captures a native-assigned identifier produced mid-plan for a later action
// to consume.
type TradeSession struct {
	SessionID    string
	SessionToken string
}

func initializeTradeSessions(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, "CREATE TABLE trade_sessions(open_action_id TEXT PRIMARY KEY REFERENCES actions(id), session_id TEXT NOT NULL, session_token TEXT NOT NULL) STRICT;")
	return err
}
func checkTradeSessionsSchema(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, "SELECT open_action_id,session_id,session_token FROM trade_sessions LIMIT 0")
	return err
}

// RecordTradeSession durably persists the session identity one OpenTrade
// action's own completion evidence produced. It is idempotent on
// openAction, the same discipline StartCaravanTracking uses for caravanID:
// re-observing the same completion (e.g. after a restart replays
// reconciliation) leaves the existing record untouched, but an openAction
// already recorded with a different session is rejected as conflicting
// evidence.
func (s *Store) RecordTradeSession(ctx context.Context, openAction domain.ActionID, session TradeSession) error {
	if submissionID(string(openAction)) != nil || submissionID(session.SessionID) != nil || submissionID(session.SessionToken) != nil {
		return errors.New("invalid trade session record")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	var existingID, existingToken string
	err = tx.QueryRowContext(ctx, "SELECT session_id,session_token FROM trade_sessions WHERE open_action_id=?", openAction).Scan(&existingID, &existingToken)
	switch {
	case errors.Is(err, sql.ErrNoRows):
		if _, err = tx.ExecContext(ctx, "INSERT INTO trade_sessions(open_action_id,session_id,session_token) VALUES(?,?,?)", openAction, session.SessionID, session.SessionToken); err != nil {
			return err
		}
	case err != nil:
		return err
	default:
		if existingID != session.SessionID || existingToken != session.SessionToken {
			return errors.New("trade session differs from existing record")
		}
	}
	return tx.Commit()
}

// LookupTradeSession returns the session identity recorded for openAction,
// or ok=false if none has been recorded yet (Open not yet observed
// complete). It never guesses or re-derives the session from native.
func (s *Store) LookupTradeSession(ctx context.Context, openAction domain.ActionID) (TradeSession, bool, error) {
	if submissionID(string(openAction)) != nil {
		return TradeSession{}, false, errors.New("invalid trade session lookup")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return TradeSession{}, false, err
	}
	defer tx.Rollback()
	var session TradeSession
	err = tx.QueryRowContext(ctx, "SELECT session_id,session_token FROM trade_sessions WHERE open_action_id=?", openAction).Scan(&session.SessionID, &session.SessionToken)
	if errors.Is(err, sql.ErrNoRows) {
		return TradeSession{}, false, nil
	}
	if err != nil {
		return TradeSession{}, false, err
	}
	if err = tx.Commit(); err != nil {
		return TradeSession{}, false, err
	}
	return session, true, nil
}
