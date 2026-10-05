package bridge

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/gabp"
	"github.com/davidarcher/RimGovernor/go/internal/gabp/gabptest"
)

func clockAdvanceFrame(seq int, newest int64) gabp.Message {
	return gabp.Message{V: gabp.Version, Type: gabp.TypeEvent, Channel: clockChannel, Seq: seq, Payload: encode(map[string]any{"type": "advance", "newest": newest})}
}

func connectFakeGame(t *testing.T, game *fakeGame) *Client {
	t.Helper()
	client, err := Open(context.Background(), ProcessConfig{GameID: "fixture", Launch: game.spec, Timeout: 20 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = client.Close() })
	started, err := client.GamesStart(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := client.ConnectWithPoll(context.Background(), started); err != nil {
		t.Fatal(err)
	}
	return client
}

// Pushed announcements move the signal, newest cursor included; the
// subscription itself moves it once, so a connect ends in a tail read of the
// journal even when the mod announced nothing while it was away.
func TestClockChannelAnnouncementsMoveTheSignal(t *testing.T) {
	game := startFakeGame(t)
	game.onClockSubscribe = func(conn *gabptest.ServerConn) {
		conn.Send(clockAdvanceFrame(1, 3))
		conn.Send(clockAdvanceFrame(2, 9))
	}
	client := connectFakeGame(t, game)
	signal := client.ClockSignal()
	if signal == nil {
		t.Fatal("no clock signal")
	}
	deadline := time.Now().Add(20 * time.Second)
	for signal.Newest() != 9 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	if signal.Newest() != 9 || signal.Version() < 3 {
		t.Fatalf("newest %d version %d", signal.Newest(), signal.Version())
	}
}

// A reconnect (Reattach) subscribes again and moves the signal without any
// announcement: the replay-on-connect is the reader's one-shot tail read.
func TestClockChannelResubscribeMovesTheSignalOnReconnect(t *testing.T) {
	game := startFakeGame(t)
	game.onClockSubscribe = func(*gabptest.ServerConn) {}
	client := connectFakeGame(t, game)
	signal := client.ClockSignal()
	before := signal.Version()
	if before == 0 {
		t.Fatal("subscription did not move the signal")
	}
	if !client.DropConnection() {
		t.Fatal("DropConnection found no connection")
	}
	select {
	case <-client.Disconnected():
	case <-time.After(10 * time.Second):
		t.Fatal("drop not observed")
	}
	if err := client.Reattach(context.Background()); err != nil {
		t.Fatal(err)
	}
	if signal.Version() <= before {
		t.Fatalf("version %d did not move past %d on reconnect", signal.Version(), before)
	}
}

// An announcement that lands while the reader is mid-read is not lost: the
// reader takes the version before the read, so the wait returns at once.
func TestClockSignalWaitSeesAnnouncementsDuringARead(t *testing.T) {
	signal := NewClockSignal()
	seen := signal.Version()
	signal.Announce(4) // lands during the read
	if !signal.Wait(context.Background(), seen, time.Hour) {
		t.Fatal("announcement during the read was lost")
	}
	seen = signal.Version()
	if signal.Wait(context.Background(), seen, 10*time.Millisecond) {
		t.Fatal("wait returned without an announcement")
	}
	go func() { time.Sleep(10 * time.Millisecond); signal.Announce(5) }()
	if !signal.Wait(context.Background(), seen, 20*time.Second) || signal.Newest() != 5 {
		t.Fatal("pushed announcement did not release the wait")
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if signal.Wait(ctx, signal.Version(), time.Hour) {
		t.Fatal("cancelled wait reported an announcement")
	}
}
