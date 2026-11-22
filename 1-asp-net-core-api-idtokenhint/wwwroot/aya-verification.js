class AyaVerificationFlow {
    constructor({ fetch, enabled, token, changed = () => {}, now = () => Date.now(), schedule = fn => setTimeout(fn, 2000), cancel = clearTimeout }) {
        Object.assign(this, { fetch, enabled, token, changed, now, schedule, cancel }); this.generation = 0;
        this.state = { stage: enabled ? "ready" : "disabled", request: null };
    }
    update(patch) { this.state = { ...this.state, ...patch }; this.changed(this.state); }
    async start() {
        if (!this.enabled || ["loading", "waiting"].includes(this.state.stage)) return;
        const generation = ++this.generation;
        this.update({ stage: "loading", request: null, message: "Preparing verification…" });
        try {
            const response = await this.fetch('/api/verifier/presentation-request', { method: 'POST', credentials: 'same-origin', cache: 'no-store', headers: { RequestVerificationToken: this.token } });
            if (!response.ok) throw Error();
            const request = await response.json(); if (generation !== this.generation) return;
            if (!request.url?.startsWith('openid-vc://?')) throw Error();
            const target = new URL(new URLSearchParams(request.url.slice('openid-vc://?'.length)).get('request_uri'));
            if (target.protocol !== 'https:' || target.hostname !== 'verifiedid.did.msidentity.com' || target.username || target.password ||
                !/^[0-9a-f-]{36}$/i.test(request.id) || !Number.isFinite(request.expiry) || request.expiry * 1000 <= this.now() ||
                !Number.isFinite(request.statusExpiry) || request.statusExpiry < request.expiry || request.statusExpiry > request.expiry + 120) throw Error();
            this.update({ stage: 'waiting', request, message: 'Open Authenticator or scan the code and present your Aya Employee ID.' });
            this.timer = this.schedule(() => this.poll(generation));
        } catch { if (generation === this.generation) this.update({ stage: 'error', request: null, message: 'Verification could not be prepared. Contact the pilot administrator before trying again.' }); }
    }
    async poll(generation) {
        if (generation !== this.generation || this.state.stage !== 'waiting') return;
        if (this.state.request.statusExpiry * 1000 <= this.now()) { this.update({ stage: 'unknown', request: null, message: 'Verification outcome is unconfirmed.' }); return; }
        if (this.state.request.expiry * 1000 <= this.now()) this.update({ message: 'The QR code expired. Checking for a final result…' });
        try {
            const response = await this.fetch('/api/verifier/request-status?id=' + encodeURIComponent(this.state.request.id), { credentials: 'same-origin', cache: 'no-store' });
            if (!response.ok) throw Error(); const result = await response.json(); if (generation !== this.generation) return;
            if (result.status === 'presentation_verified') this.update({ stage: 'success', request: null, message: 'Aya Employee ID verified at this time.' });
            else if (result.status === 'presentation_error') this.update({ stage: 'error', request: null, message: 'The credential was not accepted.' });
            else if (['request_created', 'request_retrieved', 'request_pending'].includes(result.status)) this.timer = this.schedule(() => this.poll(generation));
            else throw Error();
        } catch { if (generation === this.generation) this.update({ stage: 'unknown', request: null, message: 'Verification outcome is unconfirmed. Contact the pilot administrator.' }); }
    }
    stop() { ++this.generation; this.cancel(this.timer); this.update({ stage: 'stopped', request: null, message: 'Stopped checking. A wallet request may still finish.' }); }
}
if (typeof module !== 'undefined') module.exports = { AyaVerificationFlow };
if (typeof document !== 'undefined' && document.getElementById('aya-verification')) {
    const el = id => document.getElementById(id); let last = null;
    const flow = new AyaVerificationFlow({ fetch: window.fetch.bind(window), enabled: el('aya-verification').dataset.ready === 'true',
        token: document.querySelector('input[name="__RequestVerificationToken"]').value, changed: state => {
            el('verification-status').textContent = state.message;
            el('verify-credential').disabled = !flow.enabled || ['loading', 'waiting'].includes(state.stage);
            el('stop-verification').hidden = !['loading', 'waiting'].includes(state.stage);
            const visible = state.stage === 'waiting' && state.request?.expiry * 1000 > Date.now();
            el('verification-wallet').hidden = !visible;
            if (visible && last !== state.request.url) { last = state.request.url; el('verification-link').href = last;
                el('qrcode').replaceChildren(); new QRCode(el('qrcode'), { text: last, width: 180, height: 180 }); }
            if (!visible) { last = null; el('verification-link').removeAttribute('href'); el('qrcode').replaceChildren(); }
        }});
    el('verify-credential').addEventListener('click', () => flow.start()); el('stop-verification').addEventListener('click', () => flow.stop());
}
