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
 expect(() => readRoster({roster: {context, colonists: [{pawnId: 'a', dossier: {pawn: {id: 'b'}}}]}})).toThrow();
 expect(() => readRoster({roster: {context, colonists: [{pawnId: 'a', dossier: {pawn: {id: 'a'}, needs: {mood: 7}}}]}})).toThrow();
});
it('reads the colonist dossier and ignores observation fields it does not show', () => {
 const dossier = {pawn: {id: 'a', label: 'Ann'}, colonist: true, drafted: true, job: {defName: 'Haul', loadId: 'j1'}, needs: {mood: 0.42, food: 0.9, breakRisk: 'none', issues: []},
  health: {summaryFraction: 0.8, needsTend: true, hediffs: [{definition: {defName: 'Cut', label: 'Cut'}, partLabel: 'Left arm', severityLabel: '12%', visible: true}]},
  equipment: {equipped: [{thing: {id: 'w', defName: 'Bow', label: 'Short bow'}, weapon: true}], apparel: []},
  biography: {biologicalAgeYears: 31.5, childhood: {defName: 'C', label: 'Urchin'}, skills: [{definition: {defName: 'Shooting', label: 'Shooting'}, level: 9, passion: 'Minor'}], traits: [{defName: 'Tough'}]},
  social: {memories: [{label: 'Ate without table', moodOffsetTotal: -3, count: 1}]}, snapshot: {token: 'x'}};
 const roster = readRoster({roster: {context, colonists: [{pawnId: 'a', dossier}]}});
 expect(roster.colonists[0].dossier).toMatchObject({job: 'Haul', drafted: true, downed: false, needs: {mood: 0.42, food: 0.9, rest: null}, health: {summaryFraction: 0.8, needsTend: true, hediffs: [{label: 'Cut', partLabel: 'Left arm'}]}, gear: {weapons: [{label: 'Short bow'}]}, biography: {biologicalAgeYears: 31.5, childhood: {label: 'Urchin'}, skills: [{label: 'Shooting', level: 9, passion: 'Minor'}], traits: [{defName: 'Tough'}]}, thoughts: [{label: 'Ate without table', moodOffsetTotal: -3}]});
});
