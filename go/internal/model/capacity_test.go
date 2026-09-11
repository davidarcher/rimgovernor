package model

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

const capacityJSON = `{"models":[{"key":"local","max_context_length":262144,"loaded_instances":[{"id":"instance","config":{"context_length":32768}}]}]}`

func TestLoadedCapacityDecode(t *testing.T) {
	for _, id := range []string{"local", "instance"} {
		v, e := decodeCapacity([]byte(capacityJSON), id)
		if e != nil || v.ContextTokens != 32768 || v.InstanceID != "instance" {
			t.Fatalf("%+v %v", v, e)
		}
	}
	for name, raw := range map[string]string{
		"missing": `{"models":[]}`, "unloaded": `{"models":[{"key":"local","loaded_instances":[]}]}`,
		"maxOnly":   strings.Replace(capacityJSON, `"context_length":32768`, `"max_context_length":32768`, 1),
		"wrong":     strings.Replace(capacityJSON, `"key":"local"`, `"key":"other"`, 1),
		"duplicate": strings.Replace(capacityJSON, `"context_length":32768`, `"context_length":32768,"context_length":8192`, 1),
		"ambiguous": strings.Replace(capacityJSON, `{"id":"instance","config":{"context_length":32768}}`, `{"id":"instance","config":{"context_length":32768}},{"id":"second","config":{"context_length":8192}}`, 1),
		"malformed": `{"models":`, "trailing": capacityJSON + ` {}`, "case": strings.ReplaceAll(capacityJSON, "context_length", "CONTEXT_LENGTH"),
	} {
		t.Run(name, func(t *testing.T) {
			if _, e := decodeCapacity([]byte(raw), "local"); !errors.Is(e, ErrInvalidResponse) {
				t.Fatal(e)
			}
		})
	}
	for _, number := range []string{"null", "0", "-1", "32768.0", "3e4", "9999999999999999999999", "16777217", `"32768"`} {
		if _, e := decodeCapacity([]byte(strings.Replace(capacityJSON, "32768", number, 1)), "local"); e == nil {
			t.Fatal(number)
		}
	}
}
func TestLoadedCapacityHTTP(t *testing.T) {
	for _, mode := range []string{"ok", "redirect", "large", "cancel", "closed"} {
		t.Run(mode, func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.Method != "GET" || r.URL.Path != "/api/v1/models" {
					t.Error(r.Method, r.URL.Path)
				}
				switch mode {
				case "redirect":
					http.Redirect(w, r, "/elsewhere", 302)
				case "large":
					fmt.Fprint(w, strings.Repeat("x", 2048))
				case "cancel":
					<-r.Context().Done()
				default:
					fmt.Fprint(w, capacityJSON)
				}
			}))
			defer server.Close()
			client, e := NewClient(Config{Model: "local", BaseURL: server.URL + "/v1", Timeout: time.Second, MaxResponseBytes: 1024})
			if e != nil {
				t.Fatal(e)
			}
			defer client.Close()
			ctx, cancel := context.WithTimeout(context.Background(), 30*time.Millisecond)
			defer cancel()
			if mode == "closed" {
				client.Close()
			}
			v, e := client.LoadedCapacity(ctx)
			if mode == "ok" {
				if e != nil || v.ContextTokens != 32768 {
					t.Fatal(v, e)
				}
			} else if e == nil {
				t.Fatal("expected error")
			}
		})
	}
}
