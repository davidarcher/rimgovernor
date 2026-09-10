import React from "react";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import GameVideo from "./GameVideo";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.useRealTimers(); });

it("keeps the snapshot when WebRTC is unavailable", () => {
  vi.stubGlobal("RTCPeerConnection", undefined);
  render(<GameVideo session="a" viewer="one" enabled snapshot="/api/camera?v=1" />);
  expect(screen.getByAltText("Current RimWorld colony").getAttribute("src")).toBe("/api/camera?v=1");
  expect(screen.getByRole("status").textContent).toContain("Snapshots");
});

it("closes the old peer on pause and does not negotiate in a paused view", async () => {
  const peers: any[] = [];
  class Peer {
    iceGatheringState = "complete";
    localDescription = { type: "offer", sdp: "offer" };
    addTransceiver = vi.fn();
    createOffer = vi.fn(async () => this.localDescription);
    setLocalDescription = vi.fn(async () => {});
    setRemoteDescription = vi.fn(async () => {});
    close = vi.fn();
    constructor() { peers.push(this); }
  }
  vi.stubGlobal("RTCPeerConnection", Peer);
  vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, json: async () => ({ type: "answer", sdp: "answer" }) })));
  const { rerender } = render(<GameVideo session="a" viewer="one" enabled snapshot="frame" />);
  await waitFor(() => expect(peers[0].setRemoteDescription).toHaveBeenCalled());
  expect(peers[0].addTransceiver).toHaveBeenCalledWith("video", { direction: "recvonly" });
  const request = JSON.parse((fetch as any).mock.calls[0][1].body);
  expect(request).toEqual({ session_id: "a", viewer: "one", sdp: "offer" });
  rerender(<GameVideo session="a" viewer="one" enabled={false} snapshot="frame" />);
  expect(peers[0].close).toHaveBeenCalled();
  expect(peers).toHaveLength(1);
  expect(screen.getByRole("status").textContent).toBe("Video paused");
});

it("times out negotiation and retains the fallback", async () => {
  vi.useFakeTimers();
  const close = vi.fn();
  vi.stubGlobal("RTCPeerConnection", class {
    addTransceiver() {}
    createOffer() { return new Promise(() => {}); }
    close = close;
  });
  render(<GameVideo session="a" viewer="one" enabled snapshot="last-frame" />);
  await act(async () => { await vi.advanceTimersByTimeAsync(11000); });
  expect(close).toHaveBeenCalled();
  expect(screen.getByAltText("Current RimWorld colony").getAttribute("src")).toBe("last-frame");
});
