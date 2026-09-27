package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"
)

// Settings are the launcher's persisted choices (.rimgovernor/launcher.json),
// each mapped onto a `rimgovernor serve` flag by ServeArgs.
type Settings struct {
	// Observe runs serve --observe (no control, no writes).
	Observe bool `json:"observe"`
	// AutoStart is --resume: run the bot on every native load.
	AutoStart bool `json:"autoStart"`
	// ContinueState reuses the newest .rimgovernor/go/state-*.sqlite.
	ContinueState bool   `json:"continueState"`
	ChatModel     string `json:"chatModel"`
	ChatBaseURL   string `json:"chatBaseURL"`

	LayoutOverlay bool `json:"layoutOverlay"`

	Debug     bool   `json:"debug"`
	ExtraArgs string `json:"extraArgs"`
}

// DefaultSettings match serve's own defaults.
func DefaultSettings() Settings {
	return Settings{
		ContinueState: true,
		LayoutOverlay: true,
	}
}

// LoadSettings reads path over the defaults; a missing file is the defaults.
func LoadSettings(path string) (Settings, error) {
	s := DefaultSettings()
	data, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return s, nil
	}
	if err != nil {
		return s, err
	}
	if err := json.Unmarshal(data, &s); err != nil {
		return DefaultSettings(), fmt.Errorf("%s: %w", path, err)
	}
	return s, nil
}

// SaveSettings writes s to path.
func SaveSettings(path string, s Settings) error {
	data, err := json.MarshalIndent(s, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
		return err
	}
	return os.WriteFile(path, append(data, '\n'), 0644)
}

// Validate refuses settings serve would refuse, before a start.
func (s Settings) Validate() error {
	if _, err := SplitArgs(s.ExtraArgs); err != nil {
		return err
	}
	return nil
}

// Paths are the resolved files serve runs against.
type Paths struct {
	Profile, GABS, Config, Game, State, Assets string
}

// ServeArgs is the serve command line for s over p. Flags left at serve's
// default are omitted; observe mode passes none of the play flags, which
// serve refuses (or ignores) under --observe.
func ServeArgs(s Settings, p Paths, port int) ([]string, error) {
	if err := s.Validate(); err != nil {
		return nil, err
	}
	args := []string{"serve"}
	if s.Observe {
		args = append(args, "--observe")
	} else {
		args = append(args, "--profile", p.Profile)
	}
	args = append(args, "--gabs", p.GABS, "--config", p.Config, "--game", p.Game, "--state", p.State, "--assets", p.Assets,
		"--listen", "127.0.0.1:"+strconv.Itoa(port))
	if !s.Observe {
		if s.AutoStart {
			args = append(args, "--resume")
		}
		if m := strings.TrimSpace(s.ChatModel); m != "" {
			args = append(args, "--chat-model", m)
			if u := strings.TrimSpace(s.ChatBaseURL); u != "" {
				args = append(args, "--chat-base-url", u)
			}
		}
		for _, f := range []struct {
			on   bool
			name string
		}{} {
			if f.on {
				args = append(args, f.name)
			}
		}
		if !s.LayoutOverlay {
			args = append(args, "--layout-overlay=false")
		}
	}
	if s.Debug {
		args = append(args, "--debug")
	}
	extra, _ := SplitArgs(s.ExtraArgs)
	return append(args, extra...), nil
}

// SplitArgs splits free text on whitespace; double quotes group.
func SplitArgs(text string) ([]string, error) {
	var out []string
	var cur strings.Builder
	quoted, has := false, false
	for _, r := range text {
		switch {
		case r == '"':
			quoted, has = !quoted, true
		case !quoted && (r == ' ' || r == '\t' || r == '\n' || r == '\r'):
			if has {
				out = append(out, cur.String())
				cur.Reset()
				has = false
			}
		default:
			cur.WriteRune(r)
			has = true
		}
	}
	if quoted {
		return nil, errors.New("extra serve arguments have an unclosed quote")
	}
	if has {
		out = append(out, cur.String())
	}
	return out, nil
}

// StatePath is the state database to serve: the newest state-*.sqlite in
// dir when continuing and one exists, else a fresh stamped name.
func StatePath(dir string, continueState bool, now time.Time) string {
	if continueState {
		matches, _ := filepath.Glob(filepath.Join(dir, "state-*.sqlite"))
		if len(matches) > 0 {
			sort.Strings(matches)
			return matches[len(matches)-1]
		}
	}
	return filepath.Join(dir, "state-"+now.Format("20060102-150405")+".sqlite")
}

// ConfiguredGame is the one game id GABS's config.json lists.
func ConfiguredGame(configJSON []byte) (string, error) {
	var c struct {
		Games map[string]json.RawMessage `json:"games"`
	}
	if err := json.Unmarshal(configJSON, &c); err != nil {
		return "", err
	}
	if len(c.Games) != 1 {
		return "", fmt.Errorf("the GABS configuration lists %d games; expected one", len(c.Games))
	}
	for id := range c.Games {
		return id, nil
	}
	return "", nil
}
