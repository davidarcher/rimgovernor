//go:build windows

package main

import "errors"

// colonyApp is the app as the new-colony flow's host.
type colonyApp struct{ a *app }

func (h colonyApp) Observe() bool {
	h.a.mu.Lock()
	defer h.a.mu.Unlock()
	return h.a.settings.Observe
}

func (h colonyApp) CloseGame()                      { h.a.closeGame() }
func (h colonyApp) BaseURL() string                 { return h.a.serveURL() }
func (h colonyApp) Logf(format string, args ...any) { h.a.logf(format, args...) }

// StartController plays on the saved settings minus what would load or
// continue a colony: the new one is the only thing this start is for.
func (h colonyApp) StartController() error {
	h.a.mu.Lock()
	s := h.a.settings
	h.a.mu.Unlock()
	s.LoadSave, s.ContinueState = "", false
	h.a.playWith(&s)
	h.a.mu.Lock()
	defer h.a.mu.Unlock()
	if h.a.ctrl != ctrlRunning {
		if h.a.message == "" {
			return errors.New("it is not running")
		}
		return errors.New(h.a.message)
	}
	return nil
}

// SaveSpec persists the form in the launcher settings.
func (h colonyApp) SaveSpec(spec NewColonySpec) error {
	h.a.mu.Lock()
	s := h.a.settings
	h.a.mu.Unlock()
	s.NewColony = &spec
	return h.a.saveSettings(s)
}
