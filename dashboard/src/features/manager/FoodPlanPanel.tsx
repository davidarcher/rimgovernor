import {useEffect, useState} from 'react';
import {fetchFoodPlan, type FoodPlanStatus} from './foodPlanData';
import './FoodPlanPanel.css';

const rate = (n: number) => n.toLocaleString(undefined, {maximumFractionDigits: 2});
export default function FoodPlanPanel({active}: {active: boolean}) {
  const [reading, setReading] = useState<FoodPlanStatus | null>(null);
  const [error, setError] = useState('');
  useEffect(() => {
    if (!active) return;
    const controller = new AbortController();
    let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      try {
        const value = await fetchFoodPlan(AbortSignal.any([controller.signal, AbortSignal.timeout(20000)]));
        if (!stopped) {setReading(value); setError('');}
      } catch (e) {if (!stopped) setError(e instanceof Error ? e.message : 'Food plan unavailable');}
      finally {if (!stopped) timer = setTimeout(() => void poll(), 5000);}
    };
    void poll();
    return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, [active]);
  const p = reading?.plan;
  // Only chart geometry is computed here. Contributions and demand are server facts.
  const scale = p ? Math.max(1, p.demandPerDay, ...p.portfolio.map(r => r.deliveredPerDay)) * 1.1 : 1;
  return <section className="observation-panel food-plan" aria-label="Food channels">
    <h2>Food channels</h2>
    {error && <p role="status">{reading ? 'Stale — last recorded plan. ' : ''}{error}</p>}
    {!p ? <p>{reading ? 'Food plan unavailable for the current tick.' : 'Waiting for the food plan.'}</p> : <>
      <p className="food-summary"><strong>{rate(p.deliveredPerDay)}</strong> nutrition/day delivered · <strong>{rate(p.demandPerDay)}</strong> demand · <strong>{rate(p.gapPerDay)}</strong> plan gap</p>
      <p className="observation-note">Projected delivery admitted by the controller, including colonists, prisoners and animals. Reviewed tick {reading.tick?.toLocaleString()}.</p>
      <p className="food-legend"><span className="food-Open">● Open</span><span className="food-Hold">● Hold</span><span className="food-Close">● Close</span><span>│ Demand {rate(p.demandPerDay)}/day</span><span>▧ Reserve channel</span></p>
      {p.portfolio.length === 0 && <p>No known channels in the portfolio.</p>}
      <ul className="food-channels">{p.portfolio.map(row => <li key={JSON.stringify([row.kind, row.id])}>
        <div className="food-row-label"><strong>{row.kind}</strong><span className={`food-${row.decision}`}>{row.decision}</span><span>{rate(row.deliveredPerDay)} nutrition/day</span></div>
        <div className={`food-track${row.kind === 'Reserve' ? ' food-reserve' : ''}`} role="img" aria-label={`${row.kind} ${row.id}: ${row.decision}, ${rate(row.deliveredPerDay)} nutrition/day; demand ${rate(p.demandPerDay)}`}>
          <span className={`food-bar food-bar-${row.decision}`} style={{width: `${row.deliveredPerDay / scale * 100}%`}}/>
          <span className="food-demand" style={{left: `${p.demandPerDay / scale * 100}%`}}/>
        </div>
        <p className="food-reason">{row.id} · {row.reason}</p>
      </li>)}</ul>
      <h3>Unknown ({p.unknown.length})</h3>
      {p.unknown.length ? <ul className="food-unknown">{p.unknown.map(row => <li key={JSON.stringify([row.kind, row.id])}><strong>{row.kind}</strong> · {row.id} — {row.reason}</li>)}</ul> : <p>No unknown channels.</p>}
      <details><summary>Controller explanation</summary><p className="food-explain">{p.explain}</p></details>
    </>}
  </section>;
}
