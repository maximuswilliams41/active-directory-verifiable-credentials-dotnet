const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '../../1-asp-net-core-api-idtokenhint');

// Execute the shipped page handlers and QR encoder, with a minimal DOM/canvas and
// browser-like timer receiver checks. No network, QR stub, real identity or clock waits.
function page(prefix) {
    const elements = new Map(), calls = [], timers = new Map(); let painted = 0, cancelled = 0;
    const context2d = { clearRect() {}, strokeRect() {}, fillRect() { painted++; } };
    function element(tag = 'div') {
        return { tagName: tag.toUpperCase(), style: {}, children: [], dataset: { ready: 'true' }, listeners: {},
            appendChild(child) { this.children.push(child); }, replaceChildren() { this.children = []; },
            removeAttribute(name) { delete this[name]; }, getContext() { return context2d; },
            addEventListener(event, action) { this.listeners[event] = action; } };
    }
    const markup = fs.readFileSync(path.join(root, 'Pages/Issuer.cshtml'), 'utf8');
    for (const match of markup.matchAll(/id="([^"]+)"/g)) elements.set(match[1], element());
    const responses = [
        { displayName: 'Synthetic Fixture', employeeId: 'SYNTHETIC-001', companyName: 'Aya Living Inc.' },
        { id: '11111111-1111-4111-8111-111111111111', url: prefix + 'request_uri=https://verifiedid.did.msidentity.com/synthetic',
            expiry: Math.floor(Date.now() / 1000) + 300, statusExpiry: Math.floor(Date.now() / 1000) + 420, pin: '123456' }
    ];
    const context = vm.createContext({ URL, URLSearchParams, Date,
        navigator: { userAgent: 'Synthetic browser' }, CanvasRenderingContext2D: function () {},
        document: { documentElement: { tagName: 'HTML' }, getElementById: id => elements.get(id),
            createElement: element, querySelector: () => ({ value: 'synthetic-antiforgery' }) },
        window: { fetch: async (url, options) => { calls.push({ url, options }); assert.ok(responses.length); return { ok: true, json: async () => responses.shift() }; } },
        setTimeout: function (callback, delay) {
            if (this?.state) throw new TypeError('Invalid browser timer receiver');
            assert.equal(delay, 2000); const id = timers.size + 1; timers.set(id, callback); return id;
        },
        clearTimeout: function (id) {
            if (this?.state) throw new TypeError('Invalid browser timer receiver');
            cancelled++; timers.delete(id);
        }
    });
    vm.runInContext(fs.readFileSync(path.join(root, 'wwwroot/qrcode.min.js'), 'utf8'), context);
    vm.runInContext(fs.readFileSync(path.join(root, 'wwwroot/aya-employee-id.js'), 'utf8'), context);
    return { elements, calls, responses, timers, painted: () => painted, cancelled: () => cancelled,
        click: id => elements.get(id).listeners.click() };
}
for (const [name, prefix] of [['original', 'openid://vc?'], ['confirmed', 'openid-vc://?']]) {
    test(`${name} issuance renders with the real QR encoder and schedules confirmed-result polling`, async () => {
        const p = page(prefix);
        await p.click('check-eligibility'); await p.click('issue-credential');
        assert.match(p.elements.get('aya-status').textContent, /^Open Authenticator/);
        assert.equal(p.elements.get('aya-wallet').hidden, false);
        assert.ok(p.painted() > 0); assert.equal(p.elements.get('qrcode').children.length, 2);
        assert.equal(p.timers.size, 1); assert.equal(p.calls.length, 2);
        p.responses.push({ status: 'issuance_successful' }); await p.timers.get(1)();
        assert.equal(p.calls[2].url.startsWith('/api/request-status?id='), true);
        assert.match(p.elements.get('aya-status').textContent, /was issued/);
        assert.equal(p.elements.get('aya-wallet').hidden, true);
        assert.equal(p.elements.get('authenticator-link').href, undefined);
        assert.equal(p.elements.get('qrcode').children.length, 0);
    });
    test(`${name} employee stop button safely cancels the native timer wrapper`, async () => {
        const p = page(prefix);
        await p.click('check-eligibility'); await p.click('issue-credential'); await p.click('stop-waiting');
        assert.equal(p.cancelled(), 1); assert.equal(p.timers.size, 0); assert.equal(p.calls.length, 2);
        assert.match(p.elements.get('aya-status').textContent, /^Stopped checking/);
        assert.equal(p.elements.get('aya-wallet').hidden, true);
        assert.equal(p.elements.get('qrcode').children.length, 0);
    });
}
