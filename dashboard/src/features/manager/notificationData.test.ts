import {expect, it} from 'vitest';
import {readNotifications} from './notificationData';
const context = {identity: {colonyId: 'colony', mapId: 0, loadToken: 'load'}, tick: '9007199254740993', nativeGeneration: '18446744073709551615'};
const empty = {observed: {listing: {totalCount: 0, returnedCount: 0, complete: true}}};
const base = () => ({context, letters: empty, messages: empty, alerts: empty});
it('preserves section availability, unknown counts, known zeros and exact64-bit values', () => {
 const result = readNotifications({notifications: {...base(), letters: {unavailable: {reason: 'UNAVAILABLE_REASON_READ_FAILED'}}, messages: {observed: {messages: [{id: 'm', ageSeconds: 0, expired: false, startingFrame: '18446744073709551615', startingTick: '9007199254740993'}]}}}});
 expect(result.context.tick).toBe(context.tick); expect(result.letters.kind).toBe('unavailable');
 if (result.messages.kind !== 'observed' || result.alerts.kind !== 'observed') throw Error('Wrong sections');
 expect(result.messages.listing).toBeNull(); expect(result.messages.rows[0]).toMatchObject({ageSeconds: 0, expired: false, tick: '9007199254740993'}); expect(result.alerts.listing?.complete).toBe(true);
});
it('accepts partial counts and optional choice identities without fabricating completeness', () => {
 const result = readNotifications({notifications: {...base(), letters: {observed: {letters: [{choices: [{}, {index: 1}], lookTargets: {targets: [{}], listing: {returnedCount: 1}}}], listing: {returnedCount: 1, totalCount: 4, truncated: true}}}}});
 if (result.letters.kind !== 'observed') throw Error('Wrong section'); expect(result.letters.listing?.complete).toBeNull(); expect(result.letters.rows[0].label).toBeNull();
});
it('preserves complete listings with optional counts and diagnostic UTF8 fingerprints', () => {
 for (const listing of [{complete: true}, {complete: true, totalCount: 0}, {complete: true, returnedCount: 0}]) {
  const result = readNotifications({notifications: {...base(), alerts: {observed: {listing, snapshotFingerprint: 'é'.repeat(2048)}}}});
  if (result.alerts.kind !== 'observed') throw Error('Wrong section'); expect(result.alerts.listing?.complete).toBe(true);
 }
 expect(() => readNotifications({notifications: {...base(), alerts: {observed: {snapshotFingerprint: 'é'.repeat(2049)}}}})).toThrow();
 expect(() => readNotifications({notifications: {...base(), alerts: {observed: {listing: {complete: true, totalCount: 1}}}}})).toThrow();
});
it('rejects missing sections, conflicting outcomes, malformed counts and unsafe numeric/text evidence', () => {
 const changes = [{...base(), letters: undefined}, {...base(), letters: {observed: {}, unavailable: {reason: 'UNAVAILABLE_REASON_HIDDEN'}}}, {...base(), messages: {observed: {messages: [{ageSeconds: Infinity}]}}}, {...base(), context: {...context, nativeGeneration: '18446744073709551616'}}, {...base(), alerts: {observed: {listing: {complete: true, returnedCount: 1}}}}, {...base(), letters: {observed: {letters: [{choices: [{index: 0}]}]}}}, {...base(), letters: {observed: {letters: [{id: 'x'}, {id: 'x'}]}}}, {...base(), letters: {observed: {letters: [{text: '\ud800'}]}}}, {...base(), messages: {observed: {messages: Array.from({length: 13}, () => ({}))}}}];
 for (const notifications of changes) expect(() => readNotifications({notifications})).toThrow();
});
