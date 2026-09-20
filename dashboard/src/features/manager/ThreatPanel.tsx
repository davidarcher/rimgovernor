import {useEffect, useState} from 'react';
import {fetchThreatStatus, ThreatHTTPError, type ThreatStatus} from './threatData';

// Raid points and the wealth split the storyteller scales them by (#395):
// evidence from the live colony census, read every few seconds while the
// Colony view is open. Hidden when the service does not serve the census.
type Reading = {value: ThreatStatus | null; stale: boolean; hidden: boolean; error: string};
const silver = (v: number | null) => v === null ? 'â€”' : Math.round(v).toLocaleString();
export default function ThreatPanel({active}: {active: boolean}) {
  const [state, setState] = useState<Reading>({value: null, stale: true, hidden: false, error: ''});
  useEffect(() => {
    if (!active) return;
    const controller = new AbortController(); let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      try {const value = await fetchThreatStatus(AbortSignal.any([controller.signal, AbortSignal.timeout(5000)])); if (!stopped) setState({value, stale: false, hidden: false, error: ''});}
      catch (error) {if (!stopped) setState(previous => ({value: previous.value, stale: true, hidden: error instanceof ThreatHTTPError && error.status === 404, error: error instanceof Error ? error.message : 'Colony status unavailable'}));}
      finally {if (!stopped) timer = setTimeout(() => void poll(), 5000);}
    };
    void poll(); return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, [active]);
  if (state.hidden) return null;
  const v = state.value;
  return <section className="observation-panel" aria-label="Raid threat"><h2>Raid threat</h2>
    {state.stale && <p role="status">{v ? 'Stale â€” last recorded census. ' : 'Unavailable. '}{state.error || 'Waiting for the colony census.'}</p>}
    <p className="observation-value">{!v ? '—' : v.raidPoints === null ? 'Raid points unknown' : `${silver(v.raidPoints)} raid points`}</p>
    {v && <dl className="observation-identity">
      <div><dt>Wealth</dt><dd>{silver(v.wealthTotal)}</dd></div>
      <div><dt>Items</dt><dd>{silver(v.wealthItems)}</dd></div>
      <div><dt>Buildings</dt><dd>{silver(v.wealthBuildings)}</dd></div>
      <div><dt>Pawns</dt><dd>{silver(v.wealthPawns)}</dd></div>
      <div><dt>Build tier</dt><dd>{v.buildTier ?? 'â€”'}{v.playerTechLevel ? ` (${v.playerTechLevel} faction)` : ''}</dd></div>
    </dl>}
    {v && v.shrines && v.shrines.length > 0 && <dl className="observation-identity" aria-label="Ancient shrines">
      {v.shrines.map(shrine => <div key={shrine.id}><dt>Shrine {shrine.id}</dt><dd>{shrine.sealed ? 'sealed' : shrine.guardsAlive ? 'breached, guards alive' : 'cleared'}, {shrine.filledCaskets}/{shrine.caskets} caskets filled{shrine.inHome ? ', in Home' : ''}{shrine.ready === null ? '' : shrine.ready ? ` — breach ready (${shrine.squad} armed, ${shrine.traps} traps)` : ` — hold: ${shrine.reason}`}</dd></div>)}
    </dl>}
    <p className="observation-note">Points a default threat incident would draw at tick {v ? v.tick.toLocaleString() : 'â€”'}, as the game computes them from colony wealth, colonists, adaptation and difficulty.</p>
  </section>;
}
