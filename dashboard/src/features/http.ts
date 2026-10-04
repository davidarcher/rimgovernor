// A non-2xx reply from the service, carrying its status and the detail the
// server (or the local fallback) gave.
export class HTTPError extends Error {constructor(public status: number, detail: string) {super(detail);}}

// Shared polling cadence: how long one request may take, and the wait between
// successive polls.
export const requestTimeoutMs = 5000;
export const pollIntervalMs = 1500;

// GET a JSON route uncached. A non-2xx reply throws HTTPError with the
// body's detail, else `<unavailable> (<status>)`; a body over maxBytes, when
// given, throws.
export async function getJSON(path: string, signal: AbortSignal, unavailable: string, maxBytes?: number): Promise<unknown> {
  const response = await fetch(path, {method: 'GET', cache: 'no-store', credentials: 'same-origin', signal});
  if (!response.ok) {
    let detail = `${unavailable} (${response.status})`;
    try {const body: unknown = await response.json(); if (typeof body === 'object' && body !== null && 'detail' in body && typeof body.detail === 'string') detail = body.detail;} catch { /* Keep the status text for a malformed error body. */ }
    throw new HTTPError(response.status, detail);
  }
  if (maxBytes === undefined) return response.json();
  const source = await response.text();
  if (new TextEncoder().encode(source).length > maxBytes) throw Error(`${unavailable}: response exceeds size bound`);
  return JSON.parse(source) as unknown;
}
