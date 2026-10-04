//go:build windows

package main

import (
	"context"
	"sync"
)

var (
	controlsOnce sync.Once
	controlsInst *Controls
)

// controls is the operator-control state machine for the running
// controller (#1989). It is available only while the controller runs and
// not in Observe mode; serve's own 404 for the player routes also hides it.
func (a *app) controls() *Controls {
	controlsOnce.Do(func() {
		controlsInst = NewControls(NewServeClient(a.serveURL), func() bool {
			a.mu.Lock()
			defer a.mu.Unlock()
			return a.ctrl == ctrlRunning && !a.settings.Observe
		})
	})
	return controlsInst
}

// getControls is the page's poll: it starts a background read when one is
// due and answers from the last one.
func (a *app) getControls() ControlsView {
	c := a.controls()
	go c.Poll(context.Background())
	return c.View()
}

// botControl and ackClock run a write in the background; the page sees its
// pending and result state through getControls.
func (a *app) botControl(kind string) { go a.controls().Bot(context.Background(), kind) }
func (a *app) ackClock()              { go a.controls().Acknowledge(context.Background()) }
