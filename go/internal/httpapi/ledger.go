package httpapi

import (
	"context"
	"net/http"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// LedgerProvider reports the work ledger's latest review for the launcher.
// The ledger is memory in the serving process: the provider projects it live
// and nothing it returns is persisted or fed back to a planner. ok is false
// when the process runs no Rounds.
type LedgerProvider interface {
	WorkLedger(ctx context.Context) (view policy.LedgerView, ok bool)
}

// ledgerPath: GET /api/ledger is the launcher's read-only work ledger view.
// Like /api/routines it issues no native call and writes nothing.
const ledgerPath = "/api/ledger"

func (s *Server) handleLedger(w http.ResponseWriter, r *http.Request) bool {
	if r.URL.Path != ledgerPath {
		return false
	}
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		w.Header().Set("Allow", "GET, HEAD")
		s.failure(w, r, 405, "method_not_allowed", "The work ledger view is read-only")
		return true
	}
	if len(r.RequestURI) > 2048 || r.ContentLength != 0 || len(r.TransferEncoding) != 0 || r.URL.RawQuery != "" || r.URL.ForceQuery {
		s.failure(w, r, 400, "invalid_request", "This read requires a bounded URL, no query and no body")
		return true
	}
	if s.config.Ledger == nil {
		s.failure(w, r, 404, "not_found", "The work ledger is not enabled")
		return true
	}
	view, ok := s.config.Ledger.WorkLedger(r.Context())
	if !ok {
		s.failure(w, r, 404, "not_found", "The work ledger is not enabled")
		return true
	}
	s.write(w, r, 200, view)
	return true
}
