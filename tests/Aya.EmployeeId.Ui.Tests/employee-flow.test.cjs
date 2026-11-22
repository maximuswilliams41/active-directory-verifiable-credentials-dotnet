const test = require('node:test');
const assert = require('node:assert/strict');
const { AyaEmployeeFlow } = require('../../1-asp-net-core-api-idtokenhint/wwwroot/aya-employee-id.js');
function fixture(enabled = true) {
    let now = 1000, scheduled;
    const calls = [], responses = [];
    const flow = new AyaEmployeeFlow({ enabled, token: 'synthetic-antiforgery', now: () => now,
        schedule: callback => { scheduled = callback; return 1; }, cancel: () => { scheduled = null; },
        fetch: async (url, options) => { calls.push({ url, options }); if (!responses.length) throw Error('Unexpected application call'); const next = responses.shift(); return { ok: next.ok !== false, json: async () => next.body }; }
    });
    return { flow, calls, responses, advance: ms => { now += ms; }, poll: async () => { const callback = scheduled; scheduled = null; await callback(); } };
}
const profile = { displayName: 'Synthetic Employee', employeeId: 'SYNTHETIC-001', companyName: 'Aya Living Inc.' };
const request = { id: '11111111-1111-4111-8111-111111111111', url: 'openid://vc?request_uri=https://verifiedid.did.msidentity.com/synthetic', expiry: 60, pin: '123456' };
async function begin(f) { f.responses.push({ body: profile }, { body: request }); await f.flow.profile(); await f.flow.issue(); }

test('disabled experience makes no calls', async () => {
    const f = fixture(false); await f.flow.profile(); await f.flow.issue(); assert.equal(f.flow.state.stage, 'disabled'); assert.equal(f.calls.length, 0);
});
test('employee reviews minimum fields before starting a protected request', async () => {
    const f = fixture(); f.responses.push({ body: profile }); await f.flow.profile();
    assert.equal(f.flow.state.stage, 'ready'); assert.deepEqual(f.flow.state.profile, profile);
    f.responses.push({ body: request }); await f.flow.issue();
    assert.equal(f.flow.state.stage, 'waiting'); assert.equal(f.calls[1].options.method, 'POST');
    assert.equal(f.calls[1].options.headers.RequestVerificationToken, 'synthetic-antiforgery');
    assert.equal(f.calls[1].options.cache, 'no-store');
});
test('only confirmed issuance succeeds, never QR creation or retrieval', async () => {
    const f = fixture(); await begin(f); assert.equal(f.flow.state.stage, 'waiting');
    f.responses.push({ body: { status: 'request_retrieved' } }); await f.poll(); assert.equal(f.flow.state.stage, 'waiting');
    f.responses.push({ body: { status: 'issuance_successful' } }); await f.poll(); assert.equal(f.flow.state.stage, 'success'); assert.equal(f.flow.state.request, null);
});
test('declined eligibility cannot start issuance', async () => {
    const f = fixture(); f.responses.push({ ok: false }); await f.flow.profile(); await f.flow.issue();
    assert.equal(f.flow.state.stage, 'error'); assert.equal(f.calls.length, 1);
});
test('expiry removes QR and PIN without another status call', async () => {
    const f = fixture(); await begin(f); f.advance(60000); await f.poll();
    assert.equal(f.flow.state.stage, 'expired'); assert.equal(f.flow.state.request, null); assert.equal(f.calls.length, 2);
});
test('service error is not displayed as completion', async () => {
    const f = fixture(); await begin(f); f.responses.push({ body: { status: 'issuance_error' } }); await f.poll();
    assert.equal(f.flow.state.stage, 'error'); assert.equal(f.flow.state.request, null);
});
test('status outage explains uncertainty and clears wallet link', async () => {
    const f = fixture(); await begin(f); f.responses.push({ ok: false }); await f.poll();
    assert.equal(f.flow.state.stage, 'error'); assert.match(f.flow.state.message, /could not confirm/); assert.equal(f.flow.state.request, null);
});
test('stop waiting does not claim to cancel the wallet request', async () => {
    const f = fixture(); await begin(f); f.flow.stop();
    assert.equal(f.flow.state.stage, 'stopped'); assert.equal(f.flow.state.request, null); assert.match(f.flow.state.message, /may still finish/);
});
test('repeated clicks do not submit another request while waiting', async () => {
    const f = fixture(); await begin(f); await f.flow.issue(); await f.flow.profile(); assert.equal(f.calls.length, 2);
});
test('incomplete employee profile is not ready for issuance', async () => {
    const f = fixture(); f.responses.push({ body: { displayName: 'Synthetic Employee' } }); await f.flow.profile(); assert.equal(f.flow.state.stage, 'error');
});
test('late response after stopping cannot reopen a wallet request', async () => {
    let resolve; const f = fixture(); f.responses.push({ body: profile }); await f.flow.profile();
    f.flow.fetch = () => new Promise(done => { resolve = done; });
    const issuing = f.flow.issue(); f.flow.stop(); resolve({ ok: true, json: async () => request }); await issuing;
    assert.equal(f.flow.state.stage, 'stopped'); assert.equal(f.flow.state.request, null);
});
