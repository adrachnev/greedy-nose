// Template for the release build's backend address. Copy this file to
// `backend.local.ts` (same folder, gitignored) and put the https URL between the
// quotes. Only used in release builds; debug builds talk to localhost.
// Left empty or non-https, a release build reports "not configured" and sends no request.
export const BACKEND_URL = '';
