import {expect, it} from 'vitest';
import {readCamera, readRoster, readSelection} from './presentationData';
const context = {identity: {colonyId: 'colony', mapId: 0, loadToken: 'load'}, tick: '9007199254740993', nativeGeneration: '18446744073709551615'};
it('preserves exact integer strings and unknown values', () => {
 const camera = readCamera({camera: {context, mapPosition: {x: 0}}});
 expect(camera.context.tick).toBe(context.tick); expect(camera.context.nativeGeneration).toBe(context.nativeGeneration);
 expect(camera.mapPosition).toEqual({x: 0, z: null}); expect(camera.zoomRootSize).toBeNull();
 expect(readSelection({selection: {context}}).listing).toBeNull();
 expect(readSelection({selection: {context, fingerprint: 'x'.repeat(4096)}}).fingerprint).toHaveLength(4096);
 expect(() => readSelection({selection: {context, fingerprint: 'x'.repeat(4097)}})).toThrow();
 expect(() => readSelection({selection: {context, fingerprint: '界'.repeat(1366)}})).toThrow();
 expect(readCamera({camera: {context, zoomRootSize: 0, mapPosition: {x: -1.5}}}).zoomRootSize).toBe(0);
});
it('distinguishes complete empty and partial selection', () => {
 expect(readSelection({selection: {context, listing: {complete: true, totalCount: 0, returnedCount: 0}}}).selectedObjects).toEqual([]);
 const result = readSelection({selection: {context, selectedObjects: [{id: 'x', label: 'Wall', inspectText: 'Needs work'}], listing: {totalCount: 2, returnedCount: 1, complete: false, truncated: true}}});
 expect(result.listing?.truncated).toBe(true); expect(result.selectedObjects[0].inspectText).toBe('Needs work');
 expect(readSelection({selection: {context, listing: {complete: true}}}).listing).toEqual({complete: true, totalCount: null, returnedCount: null, truncated: null});
});
it('rejects malformed identities, numeric bounds, variants and collection evidence', () => {
 for (const camera of [{context: {...context, tick: 42}}, {context: {...context, nativeGeneration: '18446744073709551616'}}, {context, rootSize: Infinity}, {context, zoomRootSize: -1}, {context, viewRect: {minX: 4, maxX: 2}}, {context, extra: true}, {context: {...context, identity: {...context.identity, loadToken: '\ud800'}}}]) expect(() => readCamera({camera})).toThrow();
 expect(() => readCamera({failure: {code: 'UNKNOWN'}})).toThrow();
 expect(() => readSelection({selection: {context, selectedObjects: [{id: 'x'}, {id: 'x'}]}})).toThrow();
 expect(() => readSelection({selection: {context, listing: {complete: true, totalCount: 1}}})).toThrow();
 expect(() => readSelection({selection: {context, selectedObjects: [{position: {x: -1}}]}})).toThrow();
 expect(() => readRoster({roster: {context, colonists: [{mapId: 1}]}})).toThrow();
 expect(() => readRoster({roster: {context, colonists: Array.from({length: 4097}, () => ({}))}})).toThrow();
});
