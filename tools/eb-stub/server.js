// A stand-in for Enable Banking for local checks of the consent flow (Story 5.3). Plain Node, no dependencies.
// It checks no signature and keeps nothing on disk. Everything it answers is invented: the banks, the IBANs and
// the session ids below are not real. Never point it at real data.
//
// Run (the backend container joins the same network and reaches it as http://gn-eb-stub):
//   docker run -d --rm --network backend_default --name gn-eb-stub -e SCENARIO=several \
//     -v "C:/Workspace/greedy-nose/tools/eb-stub:/app:ro" -w /app node:alpine node server.js
//   (Git Bash: prefix with MSYS_NO_PATHCONV=1.) Point the backend at it with EnableBanking__BaseUrl=http://gn-eb-stub
//   and stop it with `docker rm -f gn-eb-stub`.
//
// Endpoints:
//   GET    /aspsps               a small invented list of banks
//   POST   /auth                records the "state" of the body, answers {url}
//   GET    /last-state           {"state": "<the state of the last POST /auth>"}  (for the callback in curl)
//   POST   /sessions             answers by SCENARIO: one | several | none (euro accounts; env, or POST /scenario/<name>)
//   DELETE /sessions/{id}        answers 200 and counts the call
//   GET    /deleted-count        {"count": n, "ids": [...]}  (the ids are invented session ids)
//   POST   /scenario/{name}      switches the scenario without a restart
//   GET    /accounts/{uid}/transactions   the first sync (Story 5.5), answered by a mode: ok = an empty list of
//                                 transactions, limit = 429 with an error body, notfound = 404 (the default)
//   POST   /transactions-mode/{ok|limit|notfound}   switches that mode
const http = require('http');

const ASPSPS = {
  aspsps: [
    { name: 'Testbank Alpha', country: 'DE', maximum_consent_validity: 7776000 },
    { name: 'Testbank Beta', country: 'DE', maximum_consent_validity: 15552000 },
    { name: 'Testbank Beta', country: 'FI' },
    { name: 'Mockkasse', country: 'AT', maximum_consent_validity: 7776000 },
    { name: 'Prüfbank Süd', country: 'DE', maximum_consent_validity: 7776000 },
  ],
};

const SCENARIOS = ['one', 'several', 'none'];
let scenario = SCENARIOS.includes(process.env.SCENARIO) ? process.env.SCENARIO : 'one';
const MODES = ['ok', 'limit', 'notfound'];
let mode = 'notfound';
let lastState = null;
let sessionCounter = 0;
const deleted = [];

function account(n, extra) {
  return Object.assign({
    uid: `stub-uid-${n}`,
    account_id: { iban: `DE0000000000000000000${n}` },
    name: `Stub account ${n}`,
    currency: 'EUR',
    cash_account_type: 'CACC',
    product: `Stub product ${n}`,
    usage: 'PRIV',
    identification_hash: `stub-hash-${n}`,
  }, extra || {});
}

function sessionBody() {
  sessionCounter += 1;
  const validUntil = new Date(Date.now() + 90 * 24 * 3600 * 1000).toISOString().replace(/\.\d+Z$/, 'Z');
  const accounts = scenario === 'one' ? [account(1)]
    : scenario === 'several' ? [account(1), account(2), account(3, { currency: 'USD' })]
    : [account(4, { currency: 'USD' })]; // none: no account in euro
  return { session_id: `stub-session-${sessionCounter}`, access: { valid_until: validUntil }, accounts };
}

function send(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(body));
}

http.createServer((req, res) => {
  const path = req.url.split('?')[0];
  let raw = '';
  req.on('data', (chunk) => { raw += chunk; });
  req.on('end', () => {
    if (req.method === 'GET' && path === '/aspsps') return send(res, 200, ASPSPS);
    if (req.method === 'POST' && path === '/auth') {
      try { lastState = JSON.parse(raw).state || null; } catch { lastState = null; }
      return send(res, 200, { url: 'http://gn-eb-stub/authorize?stub=1' });
    }
    if (req.method === 'GET' && path === '/last-state') return send(res, 200, { state: lastState });
    if (req.method === 'POST' && path === '/sessions') return send(res, 200, sessionBody());
    if (req.method === 'DELETE' && path.startsWith('/sessions/')) {
      deleted.push(decodeURIComponent(path.slice('/sessions/'.length)));
      return send(res, 200, {});
    }
    if (req.method === 'GET' && path === '/deleted-count') return send(res, 200, { count: deleted.length, ids: deleted });
    if (req.method === 'GET' && path.startsWith('/accounts/') && path.endsWith('/transactions')) {
      if (mode === 'ok') return send(res, 200, { transactions: [], continuation_key: null });
      if (mode === 'limit') return send(res, 429, { error: 'RATE_LIMIT_EXCEEDED', message: 'stub: daily limit used up' });
      return send(res, 404, { error: 'ACCOUNT_NOT_FOUND', message: 'stub: no such account' });
    }
    if (req.method === 'POST' && path.startsWith('/transactions-mode/')) {
      const name = path.slice('/transactions-mode/'.length);
      if (!MODES.includes(name)) return send(res, 400, { error: 'unknown mode' });
      mode = name;
      return send(res, 200, { mode });
    }
    if (req.method === 'POST' && path.startsWith('/scenario/')) {
      const name = path.slice('/scenario/'.length);
      if (!SCENARIOS.includes(name)) return send(res, 400, { error: 'unknown scenario' });
      scenario = name;
      return send(res, 200, { scenario });
    }
    return send(res, 404, { error: 'not found' });
  });
}).listen(80, () => console.log(`eb-stub listening on 80, scenario ${scenario}`));
