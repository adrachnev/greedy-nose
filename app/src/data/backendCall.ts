// The one place a backend request is made for the connect flow and the bank
// lists (Stories 5.6 and 5.7): API key header, the same AbortController +
// timeout as deviceStore.ts, and a log line that carries the path and the HTTP
// status only. Nothing here throws.
//
// `logPath` is the path as it may appear in a log: without the query and with
// an id written as `{id}`. The real path can carry an attempt id or the text
// the user typed, and the error object of a failed fetch can echo the URL, so
// neither is ever logged.

import { backendHeaders, hasApiKey } from './backendAuth';
import { BACKEND_BASE_URL, BACKEND_TIMEOUT_MS, USE_BACKEND } from './config';

/**
 * - `ok`: a 2xx answer; `body` is the parsed JSON, `null` when there was none.
 * - not `ok`: `status` is the HTTP status, or `null` when nothing was answered
 *   (not configured, no network, timeout).
 */
export type BackendReply =
  | { ok: true; status: number; body: unknown }
  | { ok: false; status: number | null };

let warnedNotConfigured = false;

/** Test-only. */
export function __resetBackendCallForTests(): void {
  warnedNotConfigured = false;
}

export async function backendCall(
  tag: string,
  path: string,
  logPath: string,
  init: { method: 'GET' | 'POST'; body?: unknown },
): Promise<BackendReply> {
  if (!USE_BACKEND || !hasApiKey() || BACKEND_BASE_URL === '') {
    // Said once per process (a poll would repeat it); USE_BACKEND off is a choice, not a fault. No value is printed.
    if (USE_BACKEND && !warnedNotConfigured) {
      warnedNotConfigured = true;
      console.warn(`[${tag}] API key or backend address not configured`);
    }
    return { ok: false, status: null };
  }
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), BACKEND_TIMEOUT_MS);
  try {
    const hasBody = init.body !== undefined;
    const response = await fetch(`${BACKEND_BASE_URL}${path}`, {
      method: init.method,
      signal: controller.signal,
      headers: backendHeaders(hasBody ? { 'Content-Type': 'application/json' } : {}),
      body: hasBody ? JSON.stringify(init.body) : undefined,
    });
    if (!response.ok) {
      console.warn(`[${tag}] ${logPath} answered HTTP ${response.status}`);
      return { ok: false, status: response.status };
    }
    // An answer without a body (or with one we cannot read) is still an answer.
    const body = await response.json().catch(() => null);
    return { ok: true, status: response.status, body };
  } catch {
    console.warn(`[${tag}] ${logPath} failed`);
    return { ok: false, status: null };
  } finally {
    clearTimeout(timeout);
  }
}
