import {afterEach, expect, it, vi} from 'vitest';
import {leaseVideo, mintVideoTicket} from './videoStreamData';
const state = {supported: true, active: true, sourceId: 'pawn-1', source: {kind: 'pawn', pawnId: 'Thing_Human42', width: 320, height: 200, framesPerSecond: 15}, remainingLeaseMs: 15000, capturedFrames: 3, framesPerSecond: 15, pixelFormat: 'RGBA32_BOTTOM_UP', captureMethod: 'render_texture'};
function stub(value: unknown) {
  const fetcher = vi.fn(async (_url: string, _init: {body: string}) => ({ok: true, json: async () => value}));
  vi.stubGlobal('fetch', fetcher);
  return fetcher;
}
const sentBody = (fetcher: ReturnType<typeof stub>) => JSON.parse(fetcher.mock.calls[0][1].body) as unknown;
afterEach(() => vi.unstubAllGlobals());
it('leases a typed source and reads the echoed source', async () => {
  const fetcher = stub(state);
  const lease = await leaseVideo('t', 15, {kind: 'pawn', pawnId: 'Thing_Human42', width: 320, height: 200, framesPerSecond: 15});
  expect(sentBody(fetcher)).toEqual({leaseSeconds: 15, source: {kind: 'pawn', pawnId: 'Thing_Human42', width: 320, height: 200, framesPerSecond: 15}});
  expect(lease.sourceId).toBe('pawn-1');
  expect(lease.source).toEqual({kind: 'pawn', pawnId: 'Thing_Human42', width: 320, height: 200, framesPerSecond: 15});
});
it('stops one source by id and binds a ticket to it', async () => {
  const fetcher = stub({...state, active: false, source: {kind: 'screen'}});
  const stopped = await leaseVideo('t', 0, {kind: 'pawn', pawnId: 'Thing_Human42'}, undefined, 'pawn-1');
  expect(sentBody(fetcher)).toEqual({leaseSeconds: 0, sourceId: 'pawn-1'});
  expect(stopped.source).toEqual({kind: 'screen'});
  const ticketFetcher = stub({ticket: 'abc', expiresMs: 5000});
  await mintVideoTicket('t', undefined, 'pawn-1');
  expect(sentBody(ticketFetcher)).toEqual({sourceId: 'pawn-1'});
});
it('rejects a state without a source or with an unknown kind', async () => {
  const {source: _source, ...withoutSource} = state; void _source;
  stub(withoutSource);
  await expect(leaseVideo('t', 15)).rejects.toThrow();
  stub({...state, source: {kind: 'drone'}});
  await expect(leaseVideo('t', 15)).rejects.toThrow();
});
