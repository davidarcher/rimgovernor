package testkit

import (
	"context"
	"encoding/json"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/gabp/gabptest"
	"github.com/davidarcher/RimGovernor/go/internal/gamehost"
)

const fakeGameRole = "testkit-fake-game"

// FakeGameMain parks the process forever when the test binary was launched
// as a fake game (StartFakeGame); a TestMain calls it first. It returns
// false otherwise.
func FakeGameMain(args []string) bool {
	if len(args) < 2 || args[1] != fakeGameRole {
		return false
	}
	for {
		time.Sleep(time.Hour)
	}
}

// StartFakeGame launches the test binary through gamehost as game gameID
// under stateDir (its TestMain must call FakeGameMain) and points the
// recorded endpoint at server, so a bridge session attaches to a live
// process and talks GABP to the in-process fake. The process is stopped on
// test cleanup. It returns the launch spec a bridge.ProcessConfig uses.
func StartFakeGame(t testing.TB, stateDir, gameID string, server *gabptest.Server) gamehost.Spec {
	t.Helper()
	exe, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	spec := gamehost.Spec{GameID: gameID, Executable: exe, Args: []string{fakeGameRole}, StateDir: stateDir}
	game, err := gamehost.Launch(context.Background(), spec)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		_ = game.Stop(context.Background())
		// Launch's reaper writes exit.json after the process is gone; wait
		// for it so TempDir removal does not race the write.
		for deadline := time.Now().Add(30 * time.Second); time.Now().Before(deadline); time.Sleep(10 * time.Millisecond) {
			if _, ok := gamehost.LastExit(stateDir, gameID); ok {
				return
			}
		}
	})
	endpoint := game.Endpoint()
	_, port, _ := net.SplitHostPort(server.Addr())
	endpoint.Port, _ = strconv.Atoi(port)
	endpoint.Token = server.Token
	data, err := json.Marshal(endpoint)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(stateDir, gameID, "endpoint.json"), data, 0o644); err != nil {
		t.Fatal(err)
	}
	return spec
}
