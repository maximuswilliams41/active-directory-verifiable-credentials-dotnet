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

test('issuance QR expiry allows the server-bounded final result window', async () => {
    const f = fixture(); f.responses.push({ body: profile }, { body: { ...request, statusExpiry: request.expiry + 120 } });
    await f.flow.profile(); await f.flow.issue(); f.advance(60000);
    f.responses.push({ body: { status: 'issuance_successful' } }); await f.poll(); assert.equal(f.flow.state.stage, 'success');
});
test('issuance final deadline remains unconfirmed and does not reissue', async () => {
    const f = fixture(); f.responses.push({ body: profile }, { body: { ...request, statusExpiry: request.expiry + 120 } });
    await f.flow.profile(); await f.flow.issue(); f.advance(180000); await f.poll();
    assert.equal(f.flow.state.stage, 'expired'); assert.match(f.flow.state.message, /unconfirmed/); assert.equal(f.calls.length, 2);
});

for (const [name, prefix] of [['original', 'openid://vc?'], ['confirmed', 'openid-vc://?']]) {
    test(`${name} issuance format reaches waiting and requires confirmed callback status`, async () => {
        const f = fixture(); const body = { ...request, url: prefix + 'request_uri=https://verifiedid.did.msidentity.com:443/synthetic' };
        f.responses.push({ body: profile }, { body }); await f.flow.profile(); await f.flow.issue();
        assert.equal(f.flow.state.stage, 'waiting'); assert.equal(f.flow.state.request.url, body.url);
        assert.equal(f.calls[1].url, '/api/issuer/issuance-request');
        assert.equal(f.calls[1].options.headers.RequestVerificationToken, 'synthetic-antiforgery');
        f.responses.push({ body: { status: 'request_retrieved' } }); await f.poll(); assert.equal(f.flow.state.stage, 'waiting');
        f.responses.push({ body: { status: 'issuance_successful' } }); await f.poll(); assert.equal(f.flow.state.stage, 'success');
    });
    test(`${name} issuance format rejects malformed or unsafe destinations`, async () => {
        for (const query of [
            '', 'other=synthetic', 'request_uri=', 'request_uri=synthetic',
            'request_uri=http://verifiedid.did.msidentity.com/synthetic',
            'request_uri=https://example.invalid/synthetic',
            'request_uri=https://verifiedid.did.msidentity.com:8443/synthetic',
            'request_uri=https://synthetic:synthetic@verifiedid.did.msidentity.com/synthetic',
            'request_uri=' + encodeURIComponent('https://verifiedid.did.msidentity.com/synthetic#fragment'),
            'request_uri=https://verifiedid.did.msidentity.com/synthetic&request_uri=https://verifiedid.did.msidentity.com/synthetic',
            'request_uri=' + 'x'.repeat(8193)
        ]) {
            const f = fixture(); f.responses.push({ body: profile }, { body: { ...request, url: prefix + query } });
            await f.flow.profile(); await f.flow.issue();
            assert.equal(f.flow.state.stage, 'error'); assert.equal(f.flow.state.request, null); assert.equal(f.calls.length, 2);
        }
    });
    test(`${name} issuance format preserves expiry and request identity guards`, async () => {
        for (const fields of [{ expiry: -1 }, { expiry: '60' }, { id: 'synthetic-invalid' }, { statusExpiry: 181 }, { statusExpiry: 59 }]) {
            const f = fixture(); f.responses.push({ body: profile }, { body: { ...request, url: prefix + 'request_uri=https://verifiedid.did.msidentity.com/synthetic', ...fields } });
            await f.flow.profile(); await f.flow.issue(); assert.equal(f.flow.state.stage, 'error'); assert.equal(f.flow.state.request, null);
        }
    });
}
test('issuance rejects every unapproved structural format without exposing a wallet link', async () => {
    for (const prefix of ['openid://vc/?', 'OPENID-VC://?', ' openid-vc://?', 'openid-vc:///?', 'openid-vc://vc?', 'synthetic-private://?', 'openid://synthetic@vc?', 'openid://vc:8443?']) {
        const f = fixture(); f.responses.push({ body: profile }, { body: { ...request, url: prefix + 'request_uri=https://verifiedid.did.msidentity.com/synthetic' } });
        await f.flow.profile(); await f.flow.issue(); assert.equal(f.flow.state.stage, 'error'); assert.equal(f.flow.state.request, null);
    }
});

for (const [name, prefix] of [['original', 'openid://vc?'], ['confirmed', 'openid-vc://?']]) {
    test(`${name} issuance format reaches the employee page wallet link and QR renderer`, async () => {
        const vm = require('node:vm'); const fs = require('node:fs');
        const elements = new Map(), rendered = [], calls = [];
        function element(id) {
            if (!elements.has(id)) elements.set(id, { dataset: { ready: 'true' }, listeners: {},
                addEventListener(event, action) { this.listeners[event] = action; },
                replaceChildren() {}, removeAttribute(name) { delete this[name]; } });
            return elements.get(id);
        }
        const body = { ...request, expiry: Math.floor(Date.now() / 1000) + 60,
            url: prefix + 'request_uri=https://verifiedid.did.msidentity.com/synthetic' };
        const responses = [profile, body];
        const context = { URL, URLSearchParams, Date, setTimeout: () => 1, clearTimeout: () => {},
            document: { getElementById: element, querySelector: () => ({ value: 'synthetic-antiforgery' }) },
            window: { fetch: async (url, options) => { calls.push({ url, options }); return { ok: true, json: async () => responses.shift() }; } },
            QRCode: class { constructor(target, options) { rendered.push(options.text); } }
        };
        vm.runInNewContext(fs.readFileSync(require.resolve('../../1-asp-net-core-api-idtokenhint/wwwroot/aya-employee-id.js'), 'utf8'), context);
        await element('check-eligibility').listeners.click();
        await element('issue-credential').listeners.click();
        assert.equal(element('aya-wallet').hidden, false);
        assert.equal(element('authenticator-link').href, body.url); assert.deepEqual(rendered, [body.url]);
        assert.equal(calls[1].url, '/api/issuer/issuance-request'); assert.equal(calls[1].options.method, 'POST');
        await element('stop-waiting').listeners.click();
        assert.equal(element('aya-wallet').hidden, true); assert.equal(element('authenticator-link').href, undefined);
    });
}
