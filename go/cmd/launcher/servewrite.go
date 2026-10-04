package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
)

// The launcher's write calls to serve's player API (#1989): bot pause and
// resume, and clock-hold acknowledge. Every write carries the per-process
// X-RimGovernor-Player token, which changes on each controller restart, so a
// 403 drops the cached token and retries once with a fresh one (a 403 means
// the write was not admitted, so the same requestId is safe to reuse).

// Write outcomes. Rejected is definite: the controller did not admit the
// request. Uncertain is anything else that failed (503, timeout, a dropped
// connection, an unreadable answer): the request may have taken effect, so
// the caller retries with the same requestId or reconciles first.
const (
	WriteAccepted  = "accepted"
	WriteRejected  = "rejected"
	WriteUncertain = "uncertain"
)

// WriteResult is one write's classified answer. Control or Clock carries the
// decoded body when there was one.
type WriteResult struct {
	Outcome string
	Status  int
	Code    string
	Detail  string
	Control *ControlView
	Clock   *ClockView
}

// ClockAck is the body of POST /api/player/clock/acknowledge; the revision
// and cursor are decimal strings read from the last GET /api/player/clock.
type ClockAck struct {
	RequestID        string `json:"requestId"`
	ExpectedRevision string `json:"expectedRevision"`
	ThroughCursor    string `json:"throughCursor"`
}

// controlBody is the body of POST /api/player/control/{pause,resume}.
type controlBody struct {
	RequestID string           `json:"requestId"`
	Expected  httpapi.Identity `json:"expected"`
}

type tokenCache struct {
	mu    sync.Mutex
	token string
}

// Pause and Resume send one control write.
func (c *ServeClient) Pause(ctx context.Context, requestID string, expected httpapi.Identity) WriteResult {
	return c.write(ctx, "/api/player/control/pause", controlBody{requestID, expected}, false)
}
func (c *ServeClient) Resume(ctx context.Context, requestID string, expected httpapi.Identity) WriteResult {
	return c.write(ctx, "/api/player/control/resume", controlBody{requestID, expected}, false)
}

// AcknowledgeClock sends one clock-hold acknowledgement.
func (c *ServeClient) AcknowledgeClock(ctx context.Context, ack ClockAck) WriteResult {
	return c.write(ctx, "/api/player/clock/acknowledge", ack, true)
}

// ReconcileControl reads the current control record and reports whether it
// is the one requestId named. serve has no lookup by request id (the route
// rejects a query), but records are journaled in order and the current one
// is the newest, so a match settles that request and a different current
// record proves nothing (Uncertain, Control set).
func (c *ServeClient) ReconcileControl(ctx context.Context, requestID string) WriteResult {
	r := c.Control(ctx)
	switch {
	case r.NotServed:
		return WriteResult{Outcome: WriteRejected, Status: http.StatusNotFound, Code: "not_found", Detail: "Player control is not served"}
	case r.Value == nil:
		return WriteResult{Outcome: WriteUncertain, Detail: r.Error}
	case r.Stale:
		return WriteResult{Outcome: WriteUncertain, Control: r.Value, Detail: r.Error}
	}
	v := r.Value
	if v.Record == nil || v.Record.RequestID != requestID {
		return WriteResult{Outcome: WriteUncertain, Control: v, Detail: "The controller's current record is not this request"}
	}
	return classifyControlRecord(v)
}

// classifyControlRecord settles a request from its record's phase: pending
// and uncertain are unresolved, the rest are final answers.
func classifyControlRecord(v *ControlView) WriteResult {
	switch v.Record.Phase {
	case "pending", "uncertain":
		return WriteResult{Outcome: WriteUncertain, Control: v, Code: "uncertain", Detail: "Control outcome is unresolved"}
	}
	return WriteResult{Outcome: WriteAccepted, Status: http.StatusOK, Control: v}
}

func (c *ServeClient) sessionToken(ctx context.Context, refetch bool) (string, bool, error) {
	c.tok.mu.Lock()
	defer c.tok.mu.Unlock()
	if refetch {
		c.tok.token = ""
	}
	if c.tok.token != "" {
		return c.tok.token, true, nil
	}
	session, notServed, err := fetch[struct {
		Token string `json:"token"`
		Mode  string `json:"mode"`
	}](ctx, c, "/api/player/session", false)
	switch {
	case err != nil:
		return "", true, err
	case notServed:
		return "", false, nil
	case session.Mode != "explicit-player" || session.Token == "":
		return "", true, errors.New("unrecognized player session")
	}
	c.tok.token = session.Token
	return session.Token, true, nil
}

// Session reports whether serve offers the player routes at all; Observe
// mode and a read-only service answer 404.
func (c *ServeClient) Session(ctx context.Context) (served bool, err error) {
	_, served, err = c.sessionToken(ctx, false)
	return served, err
}

func (c *ServeClient) write(ctx context.Context, path string, body any, clock bool) WriteResult {
	payload, err := json.Marshal(body)
	if err != nil {
		return WriteResult{Outcome: WriteRejected, Detail: err.Error()}
	}
	refetch := false
	for attempt := 0; ; attempt++ {
		token, served, err := c.sessionToken(ctx, refetch)
		if err != nil {
			return WriteResult{Outcome: WriteUncertain, Detail: err.Error()}
		}
		if !served {
			return WriteResult{Outcome: WriteRejected, Status: http.StatusNotFound, Code: "not_found", Detail: "Player control is not served"}
		}
		res := c.post(ctx, path, token, payload, clock)
		if res.Status == http.StatusForbidden && attempt == 0 {
			refetch = true
			continue
		}
		return res
	}
}

func (c *ServeClient) post(ctx context.Context, path, token string, payload []byte, clock bool) WriteResult {
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, strings.TrimRight(c.base(), "/")+path, bytes.NewReader(payload))
	if err != nil {
		return WriteResult{Outcome: WriteRejected, Detail: err.Error()}
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("X-RimGovernor-Player", token)
	resp, err := c.http.Do(req)
	if err != nil {
		return WriteResult{Outcome: WriteUncertain, Detail: err.Error()}
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(io.LimitReader(resp.Body, serveBodyLimit+1))
	res := WriteResult{Status: resp.StatusCode}
	if err != nil || len(data) > serveBodyLimit {
		res.Outcome, res.Detail = WriteUncertain, "unreadable response"
		return res
	}
	var control ControlView
	var failure httpapi.Failure
	if clock {
		if resp.StatusCode == http.StatusOK {
			var v ClockView
			if json.Unmarshal(data, &v) == nil {
				res.Clock = &v
			}
		} else {
			_ = json.Unmarshal(data, &failure)
		}
	} else if json.Unmarshal(data, &control) == nil {
		res.Control = &control
		if control.Error != nil {
			failure = *control.Error
		}
	}
	if failure.Code == "" && resp.StatusCode != http.StatusOK {
		_ = json.Unmarshal(data, &failure) // a route-level refusal is a bare failure
	}
	res.Code, res.Detail = failure.Code, failure.Detail
	switch {
	case resp.StatusCode == http.StatusOK && (clock && res.Clock != nil || !clock && res.Control != nil):
		res.Outcome = WriteAccepted
		if !clock && res.Control.Record != nil {
			res = classifyControlRecordInto(res)
		}
	case definiteStatus(resp.StatusCode):
		res.Outcome = WriteRejected
	default:
		res.Outcome = WriteUncertain
		if res.Detail == "" {
			res.Detail = http.StatusText(resp.StatusCode)
		}
	}
	return res
}

// classifyControlRecordInto keeps the HTTP status of an accepted write while
// letting a pending or uncertain record mark it unresolved.
func classifyControlRecordInto(res WriteResult) WriteResult {
	if c := classifyControlRecord(res.Control); c.Outcome == WriteUncertain {
		res.Outcome, res.Code, res.Detail = c.Outcome, c.Code, c.Detail
	}
	return res
}

// definiteStatus is the set of answers that prove the request was not
// admitted: bad request, bad token, conflict, and the route refusing it
// (not served, wrong content type, read-only service).
func definiteStatus(status int) bool {
	switch status {
	case http.StatusBadRequest, http.StatusForbidden, http.StatusConflict, http.StatusNotFound, http.StatusUnsupportedMediaType, http.StatusNotImplemented:
		return true
	}
	return false
}

// bareFailure reports whether a body is a plain {code, detail} failure
// rather than a DTO that carries its own error.
func bareFailure(body []byte) bool {
	var f httpapi.Failure
	return json.Unmarshal(body, &f) == nil && f.Code != ""
}
