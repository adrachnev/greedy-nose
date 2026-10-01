// The API key every backend call carries (go-live Story 1.2). One file owns it
// so the three callers (feed, device token, rule sync) cannot drift apart on
// header name or on what "no key" means.
//
// The key lives in `./apiKey.local.ts`: gitignored, a credential of the same
// class as the Enable Banking .pem. Metro bundles it at build time, so the
// release build needs no native change or extra dependency. See
// `./apiKey.local.example.ts` for the template.
//
// The key is NEVER logged or put in an error message, anywhere. Callers that
// need to report "no key" say so without echoing anything from here.
//
// `Authorization: Bearer <key>` on purpose: when per-user sign-in replaces the
// shared key later, the app keeps the same header and only the token changes.

// A guarded require, not an import: a fresh checkout has no apiKey.local.ts,
// and that must mean "not configured" (reported loudly by the callers), not a
// bundling failure. metro.config.js sets transformer.allowOptionalDependencies
// so Metro tolerates the missing file.
function readKey(): string {
  try {
    const key = (require('./apiKey.local') as { API_KEY?: unknown }).API_KEY;
    return typeof key === 'string' ? key.trim() : '';
  } catch {
    return '';
  }
}

let apiKey = readKey();

/** `false` means the app must not call the backend: it would only get a 401. */
export function hasApiKey(): boolean {
  return apiKey.length > 0;
}

/** Request headers for any backend call: the key plus the caller's own headers. */
export function backendHeaders(extra: Record<string, string> = {}): Record<string, string> {
  return { Authorization: `Bearer ${apiKey}`, ...extra };
}

/** Test-only: swaps the key without touching the file system. */
export function __setApiKeyForTests(key: string): void {
  apiKey = key;
}
