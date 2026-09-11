package httpapi

import (
	"context"
	"encoding/json"
	"net/http"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type ClockReview interface {
	Read(context.Context) (store.ClockReviewState, error)
	Acknowledge(context.Context, store.ClockAcknowledgement) (store.ClockReviewState, error)
}

type clockHoldDTO struct {
	Kind    store.ClockHoldKind `json:"kind"`
	From    int64               `json:"fromCursor,string"`
	Through int64               `json:"throughCursor,string"`
}
type clockReviewDTO struct {
	Revision     uint64         `json:"revision,string"`
	Inbox        int64          `json:"inboxCursor,string"`
	Reviewed     int64          `json:"reviewedCursor,string"`
	Acknowledged int64          `json:"acknowledgedCursor,string"`
	Holds        []clockHoldDTO `json:"holds"`
}

func (s *Server) handleClockReview(ctx context.Context, w http.ResponseWriter, r *http.Request, write bool) {
	if s.config.ClockReview == nil {
		s.failure(w, r, 404, "not_found", "Clock supervision is not enabled")
		return
	}
	var value store.ClockReviewState
	var err error
	if write {
		fields, e := buildingRequest(r.Body, "requestId", "expectedRevision", "throughCursor")
		var ack store.ClockAcknowledgement
		var revision, cursor string
		if e == nil {
			e = json.Unmarshal(fields["requestId"], &ack.RequestID)
		}
		if e == nil {
			e = buildingRequestID(ack.RequestID)
		}
		if e == nil {
			e = json.Unmarshal(fields["expectedRevision"], &revision)
		}
		if e == nil {
			ack.ExpectedRevision, e = strconv.ParseUint(revision, 10, 64)
		}
		if e == nil {
			e = json.Unmarshal(fields["throughCursor"], &cursor)
		}
		if e == nil {
			ack.ThroughCursor, e = strconv.ParseInt(cursor, 10, 64)
		}
		if e != nil || revision != strconv.FormatUint(ack.ExpectedRevision, 10) || cursor != strconv.FormatInt(ack.ThroughCursor, 10) || ack.ThroughCursor < 0 {
			s.failure(w, r, 400, "invalid_request", "Provide a request ID and the displayed review revision and cursor")
			return
		}
		value, err = s.config.ClockReview.Acknowledge(ctx, ack)
	} else {
		value, err = s.config.ClockReview.Read(ctx)
	}
	if err != nil {
		status, failure := playerFailure(err)
		s.write(w, r, status, failure)
		return
	}
	out := clockReviewDTO{Revision: value.Revision, Inbox: value.InboxCursor, Reviewed: value.ReviewedCursor, Acknowledged: value.AcknowledgedCursor, Holds: []clockHoldDTO{}}
	for _, hold := range value.Holds {
		out.Holds = append(out.Holds, clockHoldDTO{hold.Kind, hold.FromCursor, hold.ThroughCursor})
	}
	s.write(w, r, 200, out)
}
