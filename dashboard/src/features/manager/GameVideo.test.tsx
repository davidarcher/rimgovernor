import React from "react";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import GameVideo from "./GameVideo";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); vi.useRealTimers(); });

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
  expect(request).toEqual({ session_id: "a", viewer: "one", sdp: "offer", connection_id: expect.any(String) });
  rerender(<GameVideo session="a" viewer="one" enabled={false} snapshot="frame" />);
  expect(peers[0].close).toHaveBeenCalled();
  expect(peers).toHaveLength(1);
  const closeRequest = (fetch as any).mock.calls.find((call: any[]) => call[0] === "/api/video/close");
  expect(JSON.parse(closeRequest[1].body)).toEqual({ session_id: "a", viewer: "one", connection_id: request.connection_id });
  expect(screen.getByRole("status").textContent).toBe("Video paused");
});

it("times out negotiation and retains the fallback", async () => {
  vi.useFakeTimers();
  const close = vi.fn();
  vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true })));
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

it("retries with a new connection ID and cancels retries on pause", async () => {
  vi.useFakeTimers();
  const peers: any[] = [];
  vi.stubGlobal("fetch", vi.fn(async () => ({ ok: false })));
  vi.stubGlobal("RTCPeerConnection", class {
    iceGatheringState = "complete";
    localDescription = { sdp: "offer" };
    constructor() { peers.push(this); }
    addTransceiver() {}
    async createOffer() { return this.localDescription; }
    async setLocalDescription() {}
    close() {}
  });
  const { rerender } = render(<GameVideo session="a" viewer="one" enabled snapshot="frame" />);
  await act(async () => { await vi.advanceTimersByTimeAsync(3100); });
  expect(peers).toHaveLength(2);
  const offers = (fetch as any).mock.calls.filter((call: any[]) => call[0] === "/api/video/offer");
  expect(JSON.parse(offers[0][1].body).connection_id).not.toBe(JSON.parse(offers[1][1].body).connection_id);
  rerender(<GameVideo session="a" viewer="one" enabled={false} snapshot="frame" />);
  await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
  expect(peers).toHaveLength(2);
});

it("preserves a presented frame when the media track closes and clears it on load", async () => {
  let peer: any;
  let presented: () => void = () => {};
  vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, json: async () => ({ type: "answer", sdp: "answer" }) })));
  vi.stubGlobal("MediaStream", class {});
  vi.spyOn(HTMLMediaElement.prototype, "play").mockResolvedValue();
  vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue({ drawImage: vi.fn() } as any);
  vi.spyOn(HTMLCanvasElement.prototype, "toDataURL").mockReturnValue("data:image/jpeg;base64,cHJlc2VydmVk");
  vi.stubGlobal("RTCPeerConnection", class {
    iceGatheringState = "complete";
    connectionState = "connected";
    localDescription = { sdp: "offer" };
    constructor() { peer = this; }
    addTransceiver() {}
    async createOffer() { return this.localDescription; }
    async setLocalDescription() {}
    async setRemoteDescription() {}
    close() {}
  });
  const { rerender } = render(<GameVideo session="a" viewer="one" enabled snapshot="frame" />);
  const element = screen.getByLabelText("Live RimWorld colony") as HTMLVideoElement;
  Object.defineProperties(element, { readyState: { value: 2, configurable: true }, videoWidth: { value: 64 }, videoHeight: { value: 36 } });
  element.requestVideoFrameCallback = (callback) => { presented = callback as any; return 1; };
  element.cancelVideoFrameCallback = vi.fn();
  await act(async () => { peer.ontrack({ track: {} }); presented(); });
  Object.defineProperty(element, "readyState", { value: 0 });
  await act(async () => { peer.connectionState = "closed"; peer.onconnectionstatechange(); });
  expect(screen.getByAltText("Current RimWorld colony").getAttribute("src")).toBe("data:image/jpeg;base64,cHJlc2VydmVk");
  rerender(<GameVideo session="b" viewer="one" enabled={false} snapshot="new-frame" />);
  expect(screen.getByAltText("Current RimWorld colony").getAttribute("src")).toBe("new-frame");
});
