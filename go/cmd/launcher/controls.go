package main

import (
	"context"
	"crypto/rand"
	"fmt"
	"strconv"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
)

// The launcher's operator controls (#1989): bot Resume and Pause, and
// clock-hold Acknowledge. Controls owns the request ids and the unresolved
// intents so the page only calls Bot and Acknowledge and renders the
// ControlsView; a retry after an uncertain outcome reuses the same
// requestId, never a new one.

// ControlsView is what the page renders. Available is false when the
// controller is not running, runs in Observe mode, or serves no player routes.
type ControlsView struct {
	Available bool `json:"available"`
	// Enabled is whether the bot is running, valid when StateKnown.
	Enabled    bool `json:"enabled"`
	StateKnown bool `json:"stateKnown"`

	CanResume      bool   `json:"canResume"`
	ResumeRetry    bool   `json:"resumeRetry"` // an unresolved Resume: the button retries it
	CanPause       bool   `json:"canPause"`
	PauseRetry     bool   `json:"pauseRetry"`
	BotBusy        bool   `json:"botBusy"`
	BotNote        string `json:"botNote"`
	BotNoteIsBad   bool   `json:"botNoteIsBad"`
	ClockOpen      bool   `json:"clockOpen"`
	Holds          int    `json:"holds"`
	Gap            bool   `json:"gap"`
	CanAcknowledge bool   `json:"canAcknowledge"`
	AckRetry       bool   `json:"ackRetry"`
	AckBusy        bool   `json:"ackBusy"`
	AckNote        string `json:"ackNote"`
	AckNoteIsBad   bool   `json:"ackNoteIsBad"`
}

type Controls struct {
	client    *ServeClient
	available func() bool // controller running and not in Observe mode
	newID     func() string

	mu      sync.Mutex
	fetched time.Time
	polling bool
	state   Reading[httpapi.State]
	clock   Reading[ClockView]
	control Reading[ControlView]

	intents map[string]*botIntent // "resume" / "pause": unresolved writes
	botBusy bool
	botNote string
	botBad  bool
	ack     *ClockAck // unresolved acknowledgement
	ackBusy bool
	ackNote string
	ackBad  bool
}

type botIntent struct {
	RequestID string
	Expected  httpapi.Identity
}

func NewControls(client *ServeClient, available func() bool) *Controls {
	return &Controls{client: client, available: available, newID: newRequestID, intents: map[string]*botIntent{}}
}

func newRequestID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		panic(err)
	}
	return fmt.Sprintf("launcher-%x", b)
}

// Poll re-reads the three feeds when the last read is older than
// ControlEvery. The page's own refresh drives it (no timer here); a call
// while another read is under way returns at once.
func (c *Controls) Poll(ctx context.Context) {
	if !c.available() {
		return
	}
	c.mu.Lock()
	if c.polling || time.Since(c.fetched) < ControlEvery {
		c.mu.Unlock()
		return
	}
	c.polling = true
	c.mu.Unlock()
	st, cl, ct := c.client.State(ctx), c.client.Clock(ctx), c.client.Control(ctx)
	c.mu.Lock()
	c.state, c.clock, c.control = st, cl, ct
	c.fetched, c.polling = time.Now(), false
	c.mu.Unlock()
}

// View derives the buttons' enablement from the last Poll.
func (c *Controls) View() ControlsView {
	if !c.available() {
		return ControlsView{}
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.control.NotServed || c.control.Value == nil && c.control.Error == "" {
		return ControlsView{}
	}
	v := ControlsView{Available: true, BotBusy: c.botBusy, BotNote: c.botNote, BotNoteIsBad: c.botBad, AckBusy: c.ackBusy, AckNote: c.ackNote, AckNoteIsBad: c.ackBad}
	fresh := c.freshWorld()
	ctlFresh := c.control.Value != nil && !c.control.Stale && c.control.Value.Error == nil
	if ctlFresh {
		v.StateKnown = true
		v.Enabled = c.control.Value.State.Enabled
	}
	v.ResumeRetry, v.PauseRetry = c.intents["resume"] != nil, c.intents["pause"] != nil
	v.CanResume = !c.botBusy && fresh && (ctlFresh || v.ResumeRetry)
	v.CanPause = !c.botBusy && c.identity() != nil
	if cl := c.clock.Value; cl != nil {
		v.Holds = len(cl.Holds)
		for _, h := range cl.Holds {
			v.Gap = v.Gap || h.Kind == "gap"
		}
		v.ClockOpen = len(cl.Holds) > 0 || cursorAfter(cl.Reviewed, cl.Acknowledged)
	}
	v.AckRetry = c.ack != nil
	v.CanAcknowledge = !c.ackBusy && (c.ack != nil || c.clock.Value != nil && !c.clock.Stale && v.ClockOpen)
	return v
}

// cursorAfter is a > b for decimal uint64 strings; an unparseable pair is
// not "after".
func cursorAfter(a, b string) bool {
	x, e1 := strconv.ParseUint(a, 10, 64)
	y, e2 := strconv.ParseUint(b, 10, 64)
	return e1 == nil && e2 == nil && x > y
}

// freshWorld is the dashboard's rule: a current, connected, non-stale
// observation that knows the world. Callers hold c.mu.
func (c *Controls) freshWorld() bool {
	s := c.state.Value
	return s != nil && !c.state.Stale && s.Connected && !s.Game.Stale && s.Identity != nil
}

func (c *Controls) identity() *httpapi.Identity {
	if s := c.state.Value; s != nil {
		return s.Identity
	}
	return nil
}

// Bot sends Resume or Pause (kind is "resume" or "pause"), or retries the
// unresolved write of that kind with its original requestId. It returns the
// refreshed view.
func (c *Controls) Bot(ctx context.Context, kind string) ControlsView {
	if kind != "resume" && kind != "pause" {
		return c.View()
	}
	c.mu.Lock()
	id := c.identity()
	if c.botBusy || id == nil || kind == "resume" && !c.freshWorld() {
		c.mu.Unlock()
		return c.View()
	}
	world := *id
	intent := c.intents[kind]
	retry := intent != nil && intent.Expected == world
	if !retry {
		intent = &botIntent{RequestID: c.newID(), Expected: world}
		c.intents[kind] = intent
	}
	c.botBusy, c.botNote, c.botBad = true, "", false
	c.mu.Unlock()

	var res WriteResult
	if retry {
		// The first attempt may have landed: reading the journal is
		// cheaper and safer than a second POST.
		if res = c.client.ReconcileControl(ctx, intent.RequestID); res.Outcome == WriteUncertain {
			res = c.send(ctx, kind, intent)
		}
	} else {
		res = c.send(ctx, kind, intent)
	}

	c.mu.Lock()
	c.botBusy = false
	c.botNote, c.botBad = describeBot(kind, res)
	if res.Outcome != WriteUncertain {
		delete(c.intents, kind)
		if kind == "pause" && res.Outcome == WriteAccepted {
			delete(c.intents, "resume") // a later Pause supersedes an unresolved Resume
		}
	}
	c.fetched = time.Time{} // re-read the readings next view
	c.mu.Unlock()
	return c.View()
}

func (c *Controls) send(ctx context.Context, kind string, in *botIntent) WriteResult {
	if kind == "resume" {
		return c.client.Resume(ctx, in.RequestID, in.Expected)
	}
	return c.client.Pause(ctx, in.RequestID, in.Expected)
}

func describeBot(kind string, res WriteResult) (string, bool) {
	switch res.Outcome {
	case WriteAccepted:
		phase := ""
		if res.Control != nil && res.Control.Record != nil {
			phase = res.Control.Record.Phase
		}
		switch phase {
		case "running":
			return "Bot resumed.", false
		case "paused":
			return "Bot paused.", false
		case "refused":
			return "The controller refused this " + kind + ".", true
		}
		return "Bot " + kind + " accepted.", false
	case WriteRejected:
		return kind + " rejected: " + orStatus(res), true
	}
	return kind + " outcome unknown (" + orStatus(res) + "). Retry to check it; the same request id is reused.", true
}

func orStatus(res WriteResult) string {
	switch {
	case res.Detail != "":
		return res.Detail
	case res.Status != 0:
		return "HTTP " + strconv.Itoa(res.Status)
	}
	return "no answer"
}

// Acknowledge sends the clock-hold acknowledgement for the last clock
// reading's revision and reviewed cursor, or retries the unresolved one with
// its original requestId.
func (c *Controls) Acknowledge(ctx context.Context) ControlsView {
	c.mu.Lock()
	if c.ackBusy {
		c.mu.Unlock()
		return c.View()
	}
	ack := c.ack
	if ack == nil {
		cl := c.clock.Value
		if cl == nil || c.clock.Stale || len(cl.Holds) == 0 && !cursorAfter(cl.Reviewed, cl.Acknowledged) {
			c.mu.Unlock()
			return c.View()
		}
		ack = &ClockAck{RequestID: c.newID(), ExpectedRevision: cl.Revision, ThroughCursor: cl.Reviewed}
		c.ack = ack
	}
	a := *ack
	c.ackBusy, c.ackNote, c.ackBad = true, "", false
	c.mu.Unlock()

	res := c.client.AcknowledgeClock(ctx, a)

	c.mu.Lock()
	c.ackBusy = false
	switch res.Outcome {
	case WriteAccepted:
		c.ack, c.ackNote, c.ackBad = nil, "Acknowledged. This does not resume the clock or the bot.", false
	case WriteRejected:
		c.ack, c.ackNote, c.ackBad = nil, "Acknowledge rejected: "+orStatus(res), true
	default:
		c.ackNote, c.ackBad = "Acknowledge outcome unknown ("+orStatus(res)+"). Retry sends the same request.", true
	}
	c.fetched = time.Time{}
	c.mu.Unlock()
	return c.View()
}
