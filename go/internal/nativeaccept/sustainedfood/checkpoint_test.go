package sustainedfood

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// A checkpoint pauses, waits for manual control, saves, copies the .rws
// into profile/Saves and resumes, holding the keep-alive throughout.
func TestCheckpointPausesSavesCopiesAndResumes(t *testing.T) {
	t.Parallel()
	root := t.TempDir()
	for _, profile := range []string{"profile", "headless-profile"} {
		if err := os.MkdirAll(filepath.Join(root, profile, "Saves"), 0755); err != nil {
			t.Fatal(err)
		}
	}
	keepAlive := &authorityKeepAlive{}
	var calls []string
	mode := "automate"
	api := func(method, path string, body map[string]any, token string) (map[string]any, int, error) {
		calls = append(calls, method+" "+path)
		if !keepAlive.hold.Load() {
			t.Fatal("keep-alive not held during", path)
		}
		switch path {
		case "/api/player/control/pause":
			mode = "manual"
			return map[string]any{}, 200, nil
		case "/api/state":
			return map[string]any{"mode": mode}, 200, nil
		case "/api/lifecycle/save":
			if mode != "manual" || token != "tok" || body["saveName"] != "workshop" {
				t.Fatal(mode, token, body)
			}
			if err := os.WriteFile(filepath.Join(root, "headless-profile", "Saves", "workshop.rws"), []byte("save"), 0644); err != nil {
				t.Fatal(err)
			}
			return map[string]any{"saveName": "workshop"}, 201, nil
		case "/api/player/control/resume":
			mode = "automate"
			return map[string]any{"record": map[string]any{"phase": "running"}}, 200, nil
		}
		t.Fatal("unexpected call", path)
		return nil, 0, nil
	}
	cfg := RunConfig{Root: root, Headless: true, Checkpoint: &Checkpoint{Name: "workshop"}}
	out, err := checkpoint(context.Background(), cfg, keepAlive, api, map[string]any{"colonyId": "c"}, "tok", "test")
	if err != nil {
		t.Fatal(err)
	}
	if keepAlive.hold.Load() {
		t.Fatal("keep-alive still held")
	}
	if data, err := os.ReadFile(filepath.Join(root, "profile", "Saves", "workshop.rws")); err != nil || string(data) != "save" {
		t.Fatal("save not copied into profile/Saves", err)
	}
	if out["path"] != filepath.Join(root, "profile", "Saves", "workshop.rws") {
		t.Fatal(out)
	}
	joined := strings.Join(calls, ",")
	want := "POST /api/player/control/pause,GET /api/state,POST /api/lifecycle/save,POST /api/player/control/resume"
	if joined != want {
		t.Fatalf("calls %s", joined)
	}
}
