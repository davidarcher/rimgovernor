package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Settings are the launcher's persisted choices (.rimgovernor/launcher.json),
// each mapped onto a `rimgovernor serve` flag by ServeArgs.
type Settings struct {
	// Observe runs serve --observe (no control, no writes).
	Observe bool `json:"observe"`
	// ContinueState reuses the newest .rimgovernor/go/state-*.sqlite when
	// the bot was running there (see StatePath).
	ContinueState bool `json:"continueState"`

	// LoadSave is a save name (no .rws) to load once the controller is up;
	// "" leaves the game at its main menu (or the continued colony's save).
	LoadSave string `json:"loadSave"`

	LayoutOverlay bool `json:"layoutOverlay"`

	ExtraArgs string `json:"extraArgs"`

	// NewColony is the last New colony form (#2025); nil until one is
	// generated, then the form opens on it.
	NewColony *NewColonySpec `json:"newColony,omitempty"`
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
	Profile, Config, Game, State string
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
	args = append(args, "--config", p.Config, "--game", p.Game, "--state", p.State,
		"--listen", "127.0.0.1:"+strconv.Itoa(port))
	if !s.Observe {
		args = append(args, "--resume") // the bot always runs; there is no launcher Resume
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

// StatePath is the state database to serve and a plain line saying why:
// the newest state-*.sqlite in dir when continuing and the last session
// left the bot running there (a live control record, #1132), else a fresh
// stamped name. The controller must be stopped: live opens the database.
func StatePath(dir string, continueState bool, now time.Time, live func(path string) (bool, error)) (string, string) {
	fresh := filepath.Join(dir, "state-"+now.Format("20060102-150405")+".sqlite")
	if !continueState {
		return fresh, "starting fresh state " + filepath.Base(fresh) + " (continue is off)"
	}
	matches, _ := filepath.Glob(filepath.Join(dir, "state-*.sqlite"))
	if len(matches) == 0 {
		return fresh, "starting fresh state " + filepath.Base(fresh) + " (no earlier state)"
	}
	sort.Strings(matches)
	newest := matches[len(matches)-1]
	ok, err := live(newest)
	switch {
	case err != nil:
		return fresh, fmt.Sprintf("starting fresh state %s (could not read %s: %v)", filepath.Base(fresh), filepath.Base(newest), err)
	case !ok:
		return fresh, fmt.Sprintf("starting fresh state %s (the bot was not running in %s)", filepath.Base(fresh), filepath.Base(newest))
	}
	return newest, "continuing state " + filepath.Base(newest) + " (the bot was running)"
}

// LiveControl reports whether the state database at path last recorded
// the bot running (a resume that took effect).
func LiveControl(path string) (bool, error) {
	r, err := currentControl(path)
	return r.Request.Kind == store.ResumeControl && r.Phase == store.RunningControl, err
}

// ControlColony is the colony id on the state database's current control
// record, "" when it has none (#1143).
func ControlColony(path string) (string, error) {
	r, err := currentControl(path)
	return string(r.Request.World.Colony), err
}

func currentControl(path string) (store.ControlRecord, error) {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	s, err := store.Open(ctx, path)
	if err != nil {
		return store.ControlRecord{}, err
	}
	defer s.Close()
	r, err := s.CurrentControl(ctx)
	if err != nil {
		r = store.ControlRecord{}
	}
	if errors.Is(err, store.ErrNotFound) {
		err = nil
	}
	return r, err
}

// ListSaves is the names (no .rws) of the saves in savesDir, newest first.
func ListSaves(savesDir string) []string {
	matches, _ := filepath.Glob(filepath.Join(savesDir, "*.rws"))
	type save struct {
		name string
		at   time.Time
	}
	var saves []save
	for _, path := range matches {
		if info, err := os.Stat(path); err == nil {
			saves = append(saves, save{strings.TrimSuffix(filepath.Base(path), ".rws"), info.ModTime()})
		}
	}
	sort.Slice(saves, func(i, j int) bool { return saves[i].at.After(saves[j].at) })
	names := make([]string, len(saves))
	for i, s := range saves {
		names[i] = s.name
	}
	return names
}

// LatestColonySave is the name (no .rws) of the newest save in savesDir
// written by colony: native scribes the id as <rimgovernorColonyId>, so an
// autosave and a checkpoint both match. "" when none does.
func LatestColonySave(savesDir, colony string) (string, error) {
	if colony == "" {
		return "", nil
	}
	matches, err := filepath.Glob(filepath.Join(savesDir, "*.rws"))
	if err != nil {
		return "", err
	}
	tag := []byte("<rimgovernorColonyId>" + colony + "</rimgovernorColonyId>")
	best, bestTime := "", time.Time{}
	for _, path := range matches {
		info, err := os.Stat(path)
		if err != nil || !info.ModTime().After(bestTime) {
			continue
		}
		data, err := os.ReadFile(path)
		if err != nil || !bytes.Contains(data, tag) {
			continue
		}
		best, bestTime = strings.TrimSuffix(filepath.Base(path), ".rws"), info.ModTime()
	}
	return best, nil
}

// ReloadSave asks the controller at baseURL to load save through the
// lifecycle API, retrying (same request id) while the controller or game is
// not ready yet, until ctx ends.
func ReloadSave(ctx context.Context, baseURL, save string) error {
	client := &http.Client{Timeout: 40 * time.Second}
	body, _ := json.Marshal(map[string]any{"requestId": "launcher-reload-" + strconv.FormatInt(time.Now().UnixNano(), 36), "saveName": save, "readiness": "map", "timeoutMs": 30000})
	var last error
	for {
		status, err := reloadOnce(ctx, client, baseURL, body)
		switch {
		case err == nil && status == http.StatusCreated:
			return nil
		case err == nil && status < 500:
			return fmt.Errorf("the controller refused the load (HTTP %d)", status)
		case err == nil:
			last = fmt.Errorf("HTTP %d", status)
		default:
			last = err
		}
		select {
		case <-ctx.Done():
			return fmt.Errorf("load %s did not complete: %w", save, last)
		case <-time.After(2 * time.Second):
		}
	}
}

func reloadOnce(ctx context.Context, client *http.Client, baseURL string, body []byte) (int, error) {
	req, _ := http.NewRequestWithContext(ctx, http.MethodGet, baseURL+"/api/player/session", nil)
	resp, err := client.Do(req)
	if err != nil {
		return 0, err
	}
	var session struct {
		Token string `json:"token"`
	}
	err = json.NewDecoder(resp.Body).Decode(&session)
	resp.Body.Close()
	if resp.StatusCode != http.StatusOK || err != nil {
		return 503, nil
	}
	req, _ = http.NewRequestWithContext(ctx, http.MethodPost, baseURL+"/api/lifecycle/load", bytes.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("X-RimGovernor-Player", session.Token)
	resp, err = client.Do(req)
	if err != nil {
		return 0, err
	}
	resp.Body.Close()
	return resp.StatusCode, nil
}

// ConfiguredGame is the one game id config.json lists (games.<id>, the launch
// spec the bridge reads).
func ConfiguredGame(configJSON []byte) (string, error) {
	var c struct {
		Games map[string]json.RawMessage `json:"games"`
	}
	if err := json.Unmarshal(configJSON, &c); err != nil {
		return "", err
	}
	if len(c.Games) != 1 {
		return "", fmt.Errorf("the launch configuration lists %d games; expected one", len(c.Games))
	}
	for id := range c.Games {
		return id, nil
	}
	return "", nil
}
