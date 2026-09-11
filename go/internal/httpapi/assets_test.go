package httpapi

import (
	"context"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func assetFixture(t *testing.T) (string, *Server) {
	t.Helper()
	directory := t.TempDir()
	if err := os.Mkdir(filepath.Join(directory, "assets"), 0700); err != nil {
		t.Fatal(err)
	}
	for name, body := range map[string]string{"index.html": "<!doctype html><title>RimGovernor</title>", "assets/app.js": "export const ready=true;", "assets/app.css": "body{color:white}", "assets/icon.svg": "<svg/>"} {
		if err := os.WriteFile(filepath.Join(directory, name), []byte(body), 0600); err != nil {
			t.Fatal(err)
		}
	}
	server, err := New(Config{AssetsDir: directory, ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20}, snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), planFunc(unavailablePlan))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { server.Close() })
	return directory, server
}
func TestAssetsRegularFilesMIMEAndSPARoutes(t *testing.T) {
	_, api := assetFixture(t)
	server := testHTTP(t, api)
	for _, tc := range []struct{ path, kind, contains string }{{"/", "text/html", "RimGovernor"}, {"/colonies", "text/html", "RimGovernor"}, {"/scenario", "text/html", "RimGovernor"}, {"/assets/app.js", "text/javascript", "ready"}, {"/assets/app.css", "text/css", "color"}, {"/assets/icon.svg", "image/svg+xml", "svg"}} {
		t.Run(tc.path, func(t *testing.T) {
			response, err := http.Get(server.URL + tc.path)
			if err != nil {
				t.Fatal(err)
			}
			defer response.Body.Close()
			if response.StatusCode != 200 || !strings.HasPrefix(response.Header.Get("Content-Type"), tc.kind) || response.Header.Get("X-Content-Type-Options") != "nosniff" || !strings.Contains(response.Header.Get("Content-Security-Policy"), "script-src 'self'") {
				t.Fatalf("%d %v", response.StatusCode, response.Header)
			}
			_, body := get(t, server.URL+tc.path)
			if !strings.Contains(string(body), tc.contains) {
				t.Fatal(string(body))
			}
		})
	}
	request, _ := http.NewRequest("HEAD", server.URL+"/assets/app.js", nil)
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if response.StatusCode != 200 || response.ContentLength <= 0 {
		t.Fatal("invalid asset HEAD")
	}
}
func TestAssetBoundaryDoesNotListOrMaskMissingAPI(t *testing.T) {
	_, api := assetFixture(t)
	server := testHTTP(t, api)
	for _, route := range []string{"/assets", "/assets/", "/assets/missing.js", "/api/missing", "/api", "/missing-route", "/assets/%2e%2e/index.html", "/assets/%5c..%5cindex.html"} {
		status, body := get(t, server.URL+route)
		if status != 404 && status != 400 {
			t.Fatalf("%s: %d %s", route, status, body)
		}
		if strings.Contains(string(body), "<!doctype") {
			t.Fatal("fallback masked invalid route")
		}
	}
	request, _ := http.NewRequest("GET", server.URL+"/", nil)
	request.Host = "evil.example"
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if response.StatusCode != 403 {
		t.Fatal("asset bypassed local host protection")
	}
	request, _ = http.NewRequest("POST", server.URL+"/api/chat", strings.NewReader("{}"))
	response, err = http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if response.StatusCode != 501 {
		t.Fatal("assets masked unsupported mutation")
	}
}
func TestAssetsRejectEscapingSymlink(t *testing.T) {
	directory, api := assetFixture(t)
	outside := filepath.Join(t.TempDir(), "secret.txt")
	if err := os.WriteFile(outside, []byte("private"), 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(outside, filepath.Join(directory, "assets", "escape.txt")); err != nil {
		t.Skipf("symlink privilege unavailable: %v", err)
	}
	server := testHTTP(t, api)
	status, body := get(t, server.URL+"/assets/escape.txt")
	if status != 404 || strings.Contains(string(body), "private") {
		t.Fatalf("escaped asset root: %d %s", status, body)
	}
}
func TestAssetsConfigurationAndOwnedClose(t *testing.T) {
	for _, directory := range []string{"relative", t.TempDir()} {
		if root, err := openAssets(directory); err == nil {
			root.Close()
			t.Fatal("accepted missing/relative index")
		}
	}
	directory := t.TempDir()
	if err := os.Mkdir(filepath.Join(directory, "index.html"), 0700); err != nil {
		t.Fatal(err)
	}
	if root, err := openAssets(directory); err == nil {
		root.Close()
		t.Fatal("accepted directory index")
	}
	_, api := assetFixture(t)
	if err := api.Close(); err != nil {
		t.Fatal(err)
	}
	if err := api.Close(); err != nil {
		t.Fatal(err)
	}
	if file, err := api.assets.Open("index.html"); err == nil {
		file.Close()
		t.Fatal("asset root remained open")
	}
	_, api = assetFixture(t)
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if err := api.Serve(ctx, listener); err != nil {
		t.Fatal(err)
	}
	if file, err := api.assets.Open("index.html"); err == nil {
		file.Close()
		t.Fatal("Serve leaked asset root")
	}
}
