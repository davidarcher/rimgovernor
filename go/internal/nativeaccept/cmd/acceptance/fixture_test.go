package main

import (
	"bytes"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func TestParseFixtureDecodesArgumentsAndFlags(t *testing.T) {
	root := absRoot()
	var stderr bytes.Buffer
	o, err := parseFixture([]string{"test/place_prepare", "x=12", "def=Wall", "roofed=true", `cells=[[1,2],[3,4]]`, "note=a=b", "-root", root, "-timeout", "3m"}, &stderr)
	if err != nil {
		t.Fatalf("parseFixture: %v (%s)", err, stderr.String())
	}
	if o.Op != "test/place_prepare" {
		t.Fatalf("op = %q", o.Op)
	}
	want := map[string]any{"x": float64(12), "def": "Wall", "roofed": true, "cells": []any{[]any{float64(1), float64(2)}, []any{float64(3), float64(4)}}, "note": "a=b"}
	if !reflect.DeepEqual(o.Args, want) {
		t.Fatalf("args = %#v", o.Args)
	}
	if o.Save != "RimGovernor-tribal8-baseline" || o.Loaded || o.Timeout != 3*time.Minute || !o.Headless {
		t.Fatalf("opts = %+v", o)
	}
	if o.Output != filepath.Join(root, "acceptance", "fixture") {
		t.Fatalf("output = %q", o.Output)
	}
}

func TestParseFixtureRejects(t *testing.T) {
	root := absRoot()
	for name, args := range map[string][]string{
		"no op":           {"-root", root},
		"missing root":    {"test/x"},
		"relative root":   {"test/x", "-root", "bridge"},
		"bare argument":   {"test/x", "novalue", "-root", root},
		"op after flags":  {"-root", root, "test/x"},
		"loaded and save": {"test/x", "-root", root, "-loaded", "-save", "other"},
		"unknown flag":    {"test/x", "-root", root, "-bogus"},
	} {
		var stderr bytes.Buffer
		if _, err := parseFixture(args, &stderr); err == nil {
			t.Errorf("%s: parseFixture(%v) = nil", name, args)
		}
	}
}

func TestPrintFixtureTextListsNonZeroStock(t *testing.T) {
	var out bytes.Buffer
	printFixture(&out, false, fixtureResult{Op: "test/x", Output: "out", Success: true,
		Response: map[string]any{"success": true},
		Census: &fixtureCensus{Tick: 120, Paused: true, LoadToken: "tok",
			Stock: map[string]na.StockCounts{"WoodLog": {Units: 300, Forbidden: 20}, "Steel": {Units: 0, Forbidden: -1}, "RawBerries": {Units: 40, Forbidden: -1}}}})
	text := out.String()
	if !strings.HasPrefix(text, "OK\ttest/x\tout\n") {
		t.Fatalf("header: %q", text)
	}
	if !strings.Contains(text, "census: tick=120 paused=true") || strings.Contains(text, "Steel") {
		t.Fatalf("census: %q", text)
	}
	if berries, wood := strings.Index(text, "RawBerries\t40"), strings.Index(text, "WoodLog\t300\t(forbidden 20)"); berries < 0 || wood < berries {
		t.Fatalf("stock order: %q", text)
	}
	out.Reset()
	printFixture(&out, false, fixtureResult{Op: "test/x", Output: "out", Response: map[string]any{"success": false, "reason": "no pawn"}})
	if !strings.HasPrefix(out.String(), "REFUSED\t") {
		t.Fatalf("refused: %q", out.String())
	}
	out.Reset()
	printFixture(&out, false, fixtureResult{Op: "test/x", Output: "out", Error: "boom"})
	if !strings.HasPrefix(out.String(), "ERROR\ttest/x\tout\n\tboom\n") {
		t.Fatalf("error: %q", out.String())
	}
}

func TestRunRoutesFixture(t *testing.T) {
	var stdout, stderr bytes.Buffer
	if code := run([]string{"fixture"}, &stdout, &stderr); code != 2 || !strings.Contains(stderr.String(), "op name") {
		t.Fatalf("code=%d stderr=%q", code, stderr.String())
	}
}
