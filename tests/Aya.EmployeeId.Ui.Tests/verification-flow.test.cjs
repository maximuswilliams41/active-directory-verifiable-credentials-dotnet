const test = require('node:test');
const assert = require('node:assert/strict');
const { AyaVerificationFlow } = require('../../1-asp-net-core-api-idtokenhint/wwwroot/aya-verification.js');
const request = { id: '11111111-1111-4111-8111-111111111111', url: 'openid-vc://?request_uri=https://verifiedid.did.msidentity.com/synthetic', expiry: 60, statusExpiry: 180 };
function fixture(enabled = true) {
    let now = 1000, timer; const responses = [], calls = [];
    const flow = new AyaVerificationFlow({ enabled, token: 'synthetic-csrf', now: () => now,
        fetch: async (url, options) => { calls.push({ url, options }); const response = responses.shift(); if (!response) throw Error(); return { ok: response.ok !== false, json: async () => response.body }; },
        schedule: fn => { timer = fn; return 1; }, cancel: () => { timer = null; }
    });
    return { flow, calls, responses, advance: ms => { now += ms; }, poll: async () => { const fn = timer; timer = null; await fn(); } };
}
async function begin(f) { f.responses.push({ body: request }); await f.flow.start(); }
test('disabled verifier makes no service requests', async () => { const f = fixture(false); await f.flow.start(); assert.equal(f.calls.length, 0); });
test('verifier creates authenticated antiforgery POST and polls only its status endpoint', async () => {
    const f = fixture(); await begin(f); assert.equal(f.calls[0].options.method, 'POST'); assert.equal(f.calls[0].options.headers.RequestVerificationToken, 'synthetic-csrf');
    f.responses.push({ body: { status: 'request_retrieved' } }); await f.poll(); assert.match(f.calls[1].url, /^\/api\/verifier\/request-status\?id=/);
    assert.equal(f.calls[1].options.cache, 'no-store'); assert.equal(f.flow.state.stage, 'waiting');
});
test('only confirmed presentation is success', async () => {
    const f = fixture(); await begin(f); assert.equal(f.flow.state.stage, 'waiting');
    f.responses.push({ body: { status: 'presentation_verified' } }); await f.poll(); assert.equal(f.flow.state.stage, 'success'); assert.equal(f.flow.state.request, null);
});
test('failed presentation is rejected and wallet link removed', async () => {
    const f = fixture(); await begin(f); f.responses.push({ body: { status: 'presentation_error' } }); await f.poll(); assert.equal(f.flow.state.stage, 'error'); assert.equal(f.flow.state.request, null);
});
test('QR expiry still allows a bounded final callback result', async () => {
    const f = fixture(); await begin(f); f.advance(60000); f.responses.push({ body: { status: 'presentation_verified' } }); await f.poll(); assert.equal(f.flow.state.stage, 'success');
});
test('final deadline stops polling without claiming success', async () => {
    const f = fixture(); await begin(f); f.advance(180000); await f.poll(); assert.equal(f.flow.state.stage, 'unknown'); assert.equal(f.calls.length, 1);
});
test('status failure and unconfirmed states never become success', async () => {
    for (const response of [{ ok: false }, { body: { status: 'outcome_unknown' } }, { body: { status: 'issuance_successful' } }]) {
        const f = fixture(); await begin(f); f.responses.push(response); await f.poll(); assert.equal(f.flow.state.stage, 'unknown'); assert.equal(f.flow.state.request, null);
    }
});
test('duplicate start and stopped pending response cannot create or reopen a second wallet flow', async () => {
    const f = fixture(); await begin(f); await f.flow.start(); assert.equal(f.calls.length, 1); f.flow.stop(); assert.equal(f.flow.state.request, null);
    let resolve; const deferred = new AyaVerificationFlow({ enabled: true, fetch: () => new Promise(r => { resolve = r; }) });
    const pending = deferred.start(); deferred.stop(); resolve({ ok: true, json: async () => request }); await pending; assert.equal(deferred.state.stage, 'stopped');
});
test('invalid link or unbounded retention metadata is not exposed', async () => {
    for (const body of [{ ...request, url: 'https://example.invalid' }, { ...request, statusExpiry: 999999 }, { ...request, expiry: -1 }]) {
        const f = fixture(); f.responses.push({ body }); await f.flow.start(); assert.equal(f.flow.state.stage, 'error'); assert.equal(f.flow.state.request, null);
    }
});
