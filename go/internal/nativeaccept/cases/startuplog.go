package cases

import (
	"fmt"
	"os"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// CheckStartupLog is the closing check most cases end on: the game's
// startup log under the session's profile carries no error and matches
// the profile's headless/windowed mode (na.CheckStartupLog).
func CheckStartupLog(s Session) error {
	cfg := s.Config()
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), cfg.Headless)
}
