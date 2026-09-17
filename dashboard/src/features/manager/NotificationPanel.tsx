import {useEffect, useState} from 'react';
import type {ObservationState} from './observationData';
import {samePresentationWorld} from './presentationData';
import {fetchNotifications, NotificationHTTPError, type Notifications, type NoticeSection} from './notificationData';
type Reading = {key: string; value: Notifications | null; stale: boolean; hidden: boolean; error: string};
function Section({name, value}: {name: string; value: NoticeSection}) {
 if (value.kind === 'unavailable') return <section aria-label={name}><h3>{name}</h3><p role="status">Unavailable: {value.detail ?? value.reason}</p></section>;
 const l = value.listing;
 return <section aria-label={name}><h3>{name}</h3><p>{l?.complete === true ? 'Complete' : l?.truncated === true ? 'Truncated' : 'Completeness unknown or partial'} · {l?.returnedCount ?? 'unknown'} returned of {l?.totalCount ?? 'unknown'}</p>
 {value.rows.length === 0 && <p>{l?.complete === true ? `No ${name.toLowerCase()}.` : `No ${name.toLowerCase()} reported; availability may be incomplete.`}</p>}
 <ul>{value.rows.map((row, index) => <li key={row.id ?? `unknown-${index}`}>
 {name !== 'Messages' && <strong>{row.label ?? 'Label unknown'}</strong>}<p className="notification-text">{row.text ?? 'Text unknown'}</p>
 {name === 'Messages' && <p>Expired: {row.expired === null ? 'unknown' : row.expired ? 'yes' : 'no'} · Age: {row.ageSeconds ?? 'unknown'} seconds</p>}
 {name === 'Alerts' && <p>Active: {row.active === null ? 'unknown' : row.active ? 'yes' : 'no'}</p>}
 {row.tick !== null && <p>Native tick: {row.tick}</p>}{row.detail !== null && <p role="status">Partial read: {row.detail}</p>}
 </li>)}</ul></section>;
}
export default function NotificationPanel({observation, observationFresh}: {observation: ObservationState | null; observationFresh: boolean}) {
 const world = observation?.identity, key = world ? JSON.stringify([observation?.sessionId, world.colonyId, world.mapId, world.loadToken]) : '';
 const enabled = Boolean(world && observation?.connected && !observation.game.stale && observationFresh);
 const [state, setState] = useState<Reading>({key: '', value: null, stale: true, hidden: false, error: ''});
 useEffect(() => {
  if (!enabled || !world) {setState(previous => ({...previous, stale: true})); return;}
  const controller = new AbortController(); let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
  const poll = async () => {
   try {const value = await fetchNotifications(AbortSignal.any([controller.signal, AbortSignal.timeout(5000)])); if (!samePresentationWorld(value.context.identity, world)) throw Error('Observed world changed; waiting for matching notifications'); if (!stopped) setState({key, value, stale: false, hidden: false, error: ''});}
   catch (error) {if (!stopped) setState(previous => ({key, value: previous.key === key ? previous.value : null, stale: true, hidden: error instanceof NotificationHTTPError && error.status === 404, error: error instanceof Error ? error.message : 'Notifications unavailable'}));}
   finally {if (!stopped) timer = setTimeout(() => void poll(), 1500);}
  };
  void poll(); return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
 }, [key, enabled]);
 if (!world || state.key === key && state.hidden) return null;
 const value = state.key === key ? state.value : null, stale = state.key !== key || state.stale || !enabled;
 return <section className="observation-panel notification-panel" aria-label="Notifications"><h2>Notifications</h2>{stale && <p role="status">{value ? 'Stale — last good notifications. ' : 'Unavailable. '}{state.key === key ? state.error || 'Waiting for fresh observations.' : 'Waiting for this world.'}</p>}
 {value && <><p>Observed tick: {value.context.tick}</p><div className="notification-columns"><Section name="Letters" value={value.letters}/><Section name="Messages" value={value.messages}/><Section name="Alerts" value={value.alerts}/></div></>}
 </section>;
}
