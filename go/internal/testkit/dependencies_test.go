package testkit

import (
	"context"
	"database/sql"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	_ "modernc.org/sqlite"
)

func TestPinnedMCPInMemorySession(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	server := mcp.NewServer(&mcp.Implementation{Name: "dependency-check", Version: "1"}, nil)
	client := mcp.NewClient(&mcp.Implementation{Name: "dependency-check", Version: "1"}, nil)
	serverTransport, clientTransport := mcp.NewInMemoryTransports()
	serverSession, err := server.Connect(ctx, serverTransport, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer serverSession.Close()
	clientSession, err := client.Connect(ctx, clientTransport, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer clientSession.Close()
	result, err := clientSession.ListTools(ctx, nil)
	if err != nil || len(result.Tools) != 0 {
		t.Fatalf("empty tool discovery: result=%v err=%v", result, err)
	}
}

func TestPinnedSQLiteDurabilityAndClosure(t *testing.T) {
	path := filepath.Join(t.TempDir(), "source.sqlite")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	if _, err = db.Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE evidence(id TEXT PRIMARY KEY); INSERT INTO evidence VALUES ('action-α')"); err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	renamed := filepath.Join(filepath.Dir(path), "closed.sqlite")
	if err = os.Rename(path, renamed); err != nil {
		t.Fatalf("closed database still locked: %v", err)
	}
	reopened, err := sql.Open("sqlite", renamed)
	if err != nil {
		t.Fatal(err)
	}
	defer reopened.Close()
	var value, integrity string
	if err = reopened.QueryRow("SELECT id FROM evidence").Scan(&value); err != nil || value != "action-α" {
		t.Fatalf("durable value=%q err=%v", value, err)
	}
	if err = reopened.QueryRow("PRAGMA integrity_check").Scan(&integrity); err != nil || integrity != "ok" {
		t.Fatalf("integrity=%q err=%v", integrity, err)
	}
	if err = reopened.Close(); err != nil {
		t.Fatal(err)
	}
	if err = os.Remove(renamed); err != nil {
		t.Fatalf("reopened database still locked: %v", err)
	}
}
