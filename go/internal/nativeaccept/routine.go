package nativeaccept

import (
	"fmt"
)

// HTTPGet issues one Go-service HTTP request and returns its decoded JSON body.
type HTTPGet func(method, path string) (map[string]any, error)

// AssertRoutineRunning asserts the Go-owned supervised clock is still in automate
// mode: a
// non-automate state is always an interruption, and the player clock is fetched
// only to enrich that error, never to justify continuing.
func AssertRoutineRunning(http HTTPGet) error {
	state, err := http("GET", "/api/state")
	if err != nil {
		return err
	}
	if AsString(state["mode"]) == "automate" {
		return nil
	}
	clock, err := http("GET", "/api/player/clock")
	if err != nil {
		return err
	}
	return fmt.Errorf("routine acceptance interrupted: state=%v, clock=%v", state, clock)
}
