import React from 'react';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import GameVideo, { imagePoint } from './GameVideo';

let sockets: any[];
class Socket {
  binaryType = ''; onmessage: any; onerror: any; onclose: any;
  close = vi.fn(); send = vi.fn();
  constructor(public url: string, public protocol: string) { sockets.push(this); }
}
beforeEach(() => {
  sockets = []; vi.stubGlobal('WebSocket', Socket);
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({ drawImage: vi.fn() } as any);
  vi.stubGlobal('createImageBitmap', vi.fn(async () => ({ close: vi.fn() })));
  vi.stubGlobal('requestAnimationFrame', (callback: any) => { callback(0); return 1; });
  vi.stubGlobal('MessageChannel', class {
    port1 = { onmessage: null as null | (() => void), close() {} };
    port2 = { postMessage: () => this.port1.onmessage?.(), close() {} };
  });
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); vi.useRealTimers(); });
function packet(session = 'a', width = 1280, height = 720) {
  const meta = new TextEncoder().encode(JSON.stringify({ session, width, height, source: 'native', frame: 3, captured: Date.now()/1000 }));
  const bytes = new Uint8Array(meta.length + 5); new DataView(bytes.buffer).setUint32(0, meta.length, true);
  bytes.set(meta, 4); return bytes.buffer;
}
it('rejects letterboxing and maps scaling/fullscreen to source pixels', () => {
  const rect = { left: 20, top: 10, width: 1000, height: 1000 };
  expect(imagePoint(rect, 1280, 720, 520, 510)).toEqual({ x: 640, y: 360 });
  expect(imagePoint(rect, 1280, 720, 520, 20)).toBeNull();
  expect(imagePoint({ left: 0, top: 0, width: 2560, height: 1440 }, 1280, 720, 1280, 720)).toEqual({ x: 640, y: 360 });
  expect(imagePoint({ left: 0, top: 0, width: 0, height: 0 }, 1280, 720, 0, 0)).toBeNull();
});
it('retains snapshots when continuous video is unavailable', () => {
  vi.stubGlobal('WebSocket', undefined);
  render(<GameVideo session="a" viewer="one" enabled snapshot="/frame" />);
  expect(screen.getByAltText('Current RimWorld colony').getAttribute('src')).toBe('/frame');
});
it('uses same-origin protected streaming and acknowledges only decoded frames', async () => {
  render(<GameVideo session="a" viewer="one" enabled snapshot="/frame" />);
  expect(sockets[0].protocol).toBe('rimbot-view-v1');
  expect(sockets[0].url).toContain('/api/video/frames?session_id=a&viewer=one&connection_id=');
  expect(sockets[0].send).not.toHaveBeenCalled();
  await act(async () => { await sockets[0].onmessage({ data: packet() }); });
  expect(JSON.parse(sockets[0].send.mock.calls[0][0]).frame).toBe(3);
  expect(screen.getByRole('status').textContent).toContain('Live video');
});
it('retains the presented canvas on disconnect and rejects another load', async () => {
  const { rerender } = render(<GameVideo session="a" viewer="one" enabled snapshot="/frame" />);
  await act(async () => { await sockets[0].onmessage({ data: packet() }); sockets[0].onclose(); });
  expect(screen.getByLabelText('Live RimWorld colony').style.display).toBe('block');
  rerender(<GameVideo session="b" viewer="one" enabled={false} snapshot="/new-frame" />);
  expect(screen.getByLabelText('Live RimWorld colony').style.display).toBe('none');
  expect(screen.getByAltText('Current RimWorld colony').getAttribute('src')).toBe('/new-frame');
});
it('resizes the canvas to native resolution and closes on pause without retries', async () => {
  vi.useFakeTimers();
  const { rerender } = render(<GameVideo session="a" viewer="one" enabled snapshot="/frame" />);
  await act(async () => { await sockets[0].onmessage({ data: packet('a', 640, 480) }); });
  const canvas = screen.getByLabelText('Live RimWorld colony') as HTMLCanvasElement;
  expect([canvas.width, canvas.height]).toEqual([640, 480]);
  rerender(<GameVideo session="a" viewer="one" enabled={false} snapshot="/frame" />);
  await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
  expect(sockets[0].close).toHaveBeenCalled(); expect(sockets).toHaveLength(1);
});
it('rejects a stale-load packet before drawing or acknowledging it', async () => {
  render(<GameVideo session="a" viewer="one" enabled snapshot="/frame" />);
  await act(async () => { await sockets[0].onmessage({ data: packet('other') }); });
  expect(createImageBitmap).not.toHaveBeenCalled(); expect(sockets[0].send).not.toHaveBeenCalled();
  expect(sockets[0].close).toHaveBeenCalled();
});

it('stops streaming while hidden, then reconnects without requesting input ownership', async () => {
  const fetch = vi.fn(async () => ({ ok: true }));
  vi.stubGlobal('fetch', fetch);
  const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
  render(<GameVideo session="a" viewer="one" enabled snapshot="/frame" owner={{viewer: 'one', lease: 'lease', direct: true}} />);
  await act(async () => { await sockets[0].onmessage({ data: packet() }); });
  hidden.mockReturnValue(true);
  fireEvent(document, new Event('visibilitychange'));
  expect(sockets[0].close).toHaveBeenCalled();

  hidden.mockReturnValue(false);
  fireEvent(document, new Event('visibilitychange'));
  expect(sockets).toHaveLength(2);
  expect(fetch.mock.calls.some(call => (call as unknown[])[0] === '/api/input/take')).toBe(false);
});

it('preserves gesture boundaries, coalesces moves and never replays an uncertain event', async () => {
  vi.stubGlobal('PointerEvent', MouseEvent);
  let finish!: (value: unknown) => void;
  const fetch = vi.fn((path: string) => path === '/api/input/event'
    ? new Promise(resolve => { finish = resolve; }) : Promise.resolve({ ok: true }));
  vi.stubGlobal('fetch', fetch);
  render(<GameVideo session="a" viewer="one" enabled snapshot="" owner={{viewer: 'one', lease: 'lease', direct: true}} />);
  await act(async () => { await sockets[0].onmessage({ data: packet() }); });
  const element = screen.getByLabelText('Live RimWorld colony') as HTMLCanvasElement;
  vi.spyOn(element, 'getBoundingClientRect').mockReturnValue({left: 0, top: 0, width: 1280, height: 720} as DOMRect);
  element.setPointerCapture = vi.fn(); element.hasPointerCapture = () => false;
  fireEvent.pointerDown(element, {clientX: 50, clientY: 60, button: 0});
  fireEvent.pointerMove(element, {clientX: 55, clientY: 65});
  fireEvent.pointerMove(element, {clientX: 60, clientY: 70});
  fireEvent.pointerUp(element, {clientX: 60, clientY: 70, button: 0});
  expect(fetch).toHaveBeenCalledTimes(1);
  await act(async () => { finish({ok: true}); });
  await waitFor(() => expect(fetch).toHaveBeenCalledTimes(2));
  expect(JSON.parse((fetch.mock.calls[1] as any)[1].body)).toMatchObject({kind: 'move', x: 60, y: 70, order: 2});
  await act(async () => { finish({ok: true}); });
  await waitFor(() => expect(fetch).toHaveBeenCalledTimes(3));
  expect(JSON.parse((fetch.mock.calls[2] as any)[1].body)).toMatchObject({kind: 'up', order: 3});
  await act(async () => { finish({ok: false, json: async () => ({detail: 'uncertain'})}); });
  await waitFor(() => expect(fetch).toHaveBeenCalledTimes(4));
  expect(fetch.mock.calls[3][0]).toBe('/api/input/release');
  fireEvent.pointerDown(element, {clientX: 70, clientY: 70, button: 0});
  expect(fetch).toHaveBeenCalledTimes(4);
});
