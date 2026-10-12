package nativeaccept

import (
	"path/filepath"
	"testing"
)

// Two instances (two roots) must each read a log under their own root, and
// neither launch carries -logFile (ignored on 1.7, which writes one shared
// Player.log) while both carry -NoFileLog so that shared file stays unwritten.
func TestStartupLogPathIsPerRootAndLaunchesAvoidTheSharedPlayerLog(t *testing.T) {
	a, b := t.TempDir(), t.TempDir()
	for _, headless := range []bool{true, false} {
		pa := (&Config{Root: a, Headless: headless, GameID: "g"}).StartupLogPath()
		pb := (&Config{Root: b, Headless: headless, GameID: "g"}).StartupLogPath()
		if pa == pb || filepath.Dir(filepath.Dir(filepath.Dir(pa))) != a || filepath.Base(pa) != "game.log" {
			t.Errorf("headless=%v: log paths %q and %q are not per-root game.log files", headless, pa, pb)
		}
	}
	root, err := IsolatedRoot(writeSourceRoot(t), filepath.Join(t.TempDir(), "worker"))
	if err != nil {
		t.Fatal(err)
	}
	configuration, err := prepare(root, nil, nil, false)
	if err != nil {
		t.Fatal(err)
	}
	config, err := loadConfig(filepath.Join(configuration, "config.json"))
	if err != nil {
		t.Fatal(err)
	}
	game, err := gameSection(config)
	if err != nil {
		t.Fatal(err)
	}
	var noFileLog bool
	for _, arg := range game["args"].([]any) {
		switch arg {
		case "-logFile":
			t.Error("launch still passes -logFile")
		case "-NoFileLog":
			noFileLog = true
		}
	}
	if !noFileLog {
		t.Error("launch lacks -NoFileLog")
	}
}
