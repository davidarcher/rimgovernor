package nativeaccept

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"
)

// fakeCaller answers native calls from a table and records what was asked.
type fakeCaller struct {
	replies map[string]map[string]any
	fail    map[string]error
	calls   []string
	args    map[string]any
}

func (f *fakeCaller) Call(_ context.Context, _, tool string, arguments any) (map[string]any, error) {
	f.calls = append(f.calls, tool)
	if f.args == nil {
		f.args = map[string]any{}
	}
	f.args[tool] = arguments
	if err := f.fail[tool]; err != nil {
		return nil, err
	}
	return f.replies[tool], nil
}

func TestStartSavesDriveExpansions(t *testing.T) {
	if s := (DebugStart{}).saves(); s != nil {
		t.Fatalf("debug start names saves %v", s)
	}
	if s := (Save{Name: "colony"}).saves(); len(s) != 1 || s[0] != "colony" {
		t.Fatalf("save start saves %v", s)
	}
	if s := (Fixture{Op: "test/x_prepare"}).saves(); s != nil {
		t.Fatalf("fixture on the debug start names saves %v", s)
	}
	if s := (Fixture{Op: "test/x_prepare", On: Save{Name: "colony"}}).saves(); len(s) != 1 || s[0] != "colony" {
		t.Fatalf("fixture on a save saves %v", s)
	}
}

func TestLoadSaveIssuesLoadGameReady(t *testing.T) {
	c := &fakeCaller{}
	if err := loadSave(context.Background(), c, "colony", 0); err != nil {
		t.Fatal(err)
	}
	args := c.args["rimworld/load_game_ready"].(map[string]any)
	if args["saveName"] != "colony" || args["timeoutMs"] != int64(90000) || args["readiness"] != "visual" {
		t.Fatalf("load args %#v", args)
	}
	if err := loadSave(context.Background(), c, "colony", 5*time.Second); err != nil {
		t.Fatal(err)
	}
	if got := c.args["rimworld/load_game_ready"].(map[string]any)["timeoutMs"]; got != int64(5000) {
		t.Fatalf("explicit timeout %v", got)
	}
	c.fail = map[string]error{"rimworld/load_game_ready": errors.New("boom")}
	if err := loadSave(context.Background(), c, "colony", 0); err == nil || !strings.Contains(err.Error(), "colony") {
		t.Fatalf("load failure %v", err)
	}
}

func TestFixturePrepare(t *testing.T) {
	identity := map[string]any{"colonyId": "c", "loadToken": "t", "mapId": float64(1)}
	names := []string{"rimworld/start_debug_game_ready", "test/x_prepare"}
	ctx := context.Background()

	if _, err := (Fixture{}).prepare(ctx, &fakeCaller{}, names, identity); err == nil {
		t.Fatal("empty op prepared")
	}
	if _, err := (Fixture{Op: "test/missing"}).prepare(ctx, &fakeCaller{}, names, identity); err == nil || !strings.Contains(err.Error(), "discovery") {
		t.Fatalf("missing op: %v", err)
	}

	c := &fakeCaller{replies: map[string]map[string]any{"test/x_prepare": {"success": true, "patient": "p"}}}
	prepared, err := (Fixture{Op: "test/x_prepare"}).prepare(ctx, c, names, identity)
	if err != nil || prepared["patient"] != "p" {
		t.Fatalf("prepared %v, %v", prepared, err)
	}
	if args, ok := c.args["test/x_prepare"].(map[string]any); !ok || len(args) != 0 {
		t.Fatalf("nil args sent as %#v", c.args["test/x_prepare"])
	}
	c = &fakeCaller{replies: map[string]map[string]any{"test/x_prepare": {"success": true}}}
	if _, err := (Fixture{Op: "test/x_prepare", Args: map[string]any{"scenario": "dark"}}).prepare(ctx, c, names, identity); err != nil {
		t.Fatal(err)
	}
	if got := c.args["test/x_prepare"].(map[string]any)["scenario"]; got != "dark" {
		t.Fatalf("args %v", c.args["test/x_prepare"])
	}

	c = &fakeCaller{replies: map[string]map[string]any{"test/x_prepare": {"success": false, "reason": "no"}}}
	if _, err := (Fixture{Op: "test/x_prepare"}).prepare(ctx, c, names, identity); err == nil || !strings.Contains(err.Error(), "refused") {
		t.Fatalf("refusal: %v", err)
	}

	matching := map[string]any{"success": true, "colonyId": "c", "loadToken": "t", "mapId": float64(1)}
	c = &fakeCaller{replies: map[string]map[string]any{"test/x_prepare": matching}}
	if _, err := (Fixture{Op: "test/x_prepare"}).prepare(ctx, c, names, identity); err != nil {
		t.Fatalf("matching identity: %v", err)
	}
	stale := map[string]any{"success": true, "colonyId": "c", "loadToken": "old", "mapId": float64(1)}
	c = &fakeCaller{replies: map[string]map[string]any{"test/x_prepare": stale}}
	if _, err := (Fixture{Op: "test/x_prepare"}).prepare(ctx, c, names, identity); err == nil || !strings.Contains(err.Error(), "identity") {
		t.Fatalf("stale identity: %v", err)
	}
}

func TestFixtureDefaultsToDebugStart(t *testing.T) {
	if _, ok := (Fixture{Op: "test/x_prepare"}).base().(DebugStart); !ok {
		t.Fatal("nil On is not the debug start")
	}
	if _, ok := (Fixture{Op: "test/x_prepare", On: Save{Name: "s"}}).base().(Save); !ok {
		t.Fatal("On is not honoured")
	}
}
