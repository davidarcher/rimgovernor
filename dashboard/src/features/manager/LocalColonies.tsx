import { useEffect, useState } from 'react';
import './LocalColonies.css';

type Colony = { id: string; name: string; url: string | null; display: string; startedAt: string };

export default function LocalColonies() {
  const [colonies, setColonies] = useState<Colony[]>([]);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    const refresh = async () => {
      try {
        const response = await fetch('/api/colonies', { signal: AbortSignal.timeout(12000) });
        if (!response.ok) throw Error('Could not refresh local colonies.');
        const data = await response.json();
        if (data.error || !Array.isArray(data.colonies)) throw Error(data.error || 'Could not refresh local colonies.');
        if (!stopped) { setColonies(data.colonies); setError(''); }
      } catch (e) {
        if (!stopped) setError(e instanceof Error ? e.message : 'Could not refresh local colonies.');
      } finally {
        if (!stopped) { setLoading(false); timer = setTimeout(refresh, 10000); }
      }
    };
    void refresh();
    return () => { stopped = true; clearTimeout(timer); };
  }, []);
  return <section className="local-colonies" aria-label="Local colonies">
    <h2>Local colonies</h2>
    <p>Open a colony in its own tab and switch between them without losing your place.</p>
    {loading && <p role="status">Finding running colonies…</p>}
    {error && <p role="status">{error} {colonies.length > 0 && 'Showing the last discovered colonies.'}</p>}
    {!loading && !error && colonies.length === 0 && <p>No running RimGovernor Docker colonies found.</p>}
    <ul>{colonies.map(colony => <li key={colony.id}>
      <div><strong>{colony.name}</strong><small>{colony.display === 'xvfb' ? 'Rendered game view' : 'Headless · no game image'}</small></div>
      {colony.url ? <a href={colony.url} target={`colony-${colony.id}`} rel="noopener">Open colony ↗</a>
        : <span>Dashboard port not published</span>}
    </li>)}</ul>
    <p className="colony-discovery-note">Refreshes every 10 seconds. Running containers may still be starting their colony.</p>
  </section>;
}
