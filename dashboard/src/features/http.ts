// A non-2xx reply from the service, carrying its status and the detail the
// server (or the local fallback) gave.
export class HTTPError extends Error {constructor(public status: number, detail: string) {super(detail);}}

// Shared polling cadence: how long one request may take, and the wait between
// successive polls.
export const requestTimeoutMs = 5000;
export const pollIntervalMs = 1500;
