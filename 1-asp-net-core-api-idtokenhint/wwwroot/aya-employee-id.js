/* Local UI controller: network calls are same-origin application endpoints only. */
class AyaEmployeeFlow {
    constructor({ fetch, now = () => Date.now(), schedule = setTimeout, cancel = clearTimeout, changed = () => {}, token = "", enabled = false }) {
        Object.assign(this, { fetch, now, schedule, cancel, changed, token, enabled });
        this.generation = 0; this.state = { stage: enabled ? "idle" : "disabled", message: enabled ? "Check your details to begin." : "Issuance is currently disabled. No credential will be issued." };
    }
    update(values) { this.state = { ...this.state, ...values }; this.changed(this.state); }
    async profile() {
        if (!this.enabled || this.state.stage === "loading" || this.state.stage === "waiting") return;
        const generation = ++this.generation;
        this.update({ stage: "loading", message: "Checking your eligibility…", profile: null, request: null });
        try {
            const response = await this.fetch("/api/aya/my-profile", { credentials: "same-origin", cache: "no-store" });
            if (generation !== this.generation) return;
            if (!response.ok) throw new Error();
            const profile = await response.json();
            if (generation !== this.generation) return;
            if (![profile.displayName, profile.employeeId, profile.companyName].every(v => typeof v === "string" && v.trim())) throw new Error();
            this.update({ stage: "ready", profile, message: "Check these details before adding your ID." });
        } catch { if (generation === this.generation) this.update({ stage: "error", message: "We could not confirm eligibility. Contact your Aya administrator or try again." }); }
    }
    async issue() {
        if (!this.enabled || this.state.stage !== "ready") return;
        const generation = ++this.generation;
        this.update({ stage: "loading", message: "Preparing your Authenticator request…" });
        try {
            const response = await this.fetch("/api/issuer/issuance-request", { method: "POST", credentials: "same-origin", cache: "no-store", headers: { RequestVerificationToken: this.token } });
            if (!response.ok) throw new Error();
            const request = await response.json();
            if (generation !== this.generation) return;
            if (typeof request.url !== "string" || request.url.length > 8192) throw new Error();
            const prefix = ["openid://vc?", "openid-vc://?"].find(value => request.url.startsWith(value));
            if (!prefix || request.url.includes("#")) throw new Error();
            const targets = new URL(request.url).searchParams.getAll("request_uri");
            if (targets.length !== 1) throw new Error();
            const target = new URL(targets[0]);
            if (target.protocol !== "https:" || target.hostname !== "verifiedid.did.msidentity.com" ||
                target.port || target.username || target.password || target.href.includes("#") ||
                !/^[0-9a-f-]{36}$/i.test(request.id) || !Number.isFinite(request.expiry) || request.expiry * 1000 <= this.now() ||
                (request.statusExpiry !== undefined && (!Number.isFinite(request.statusExpiry) || request.statusExpiry < request.expiry || request.statusExpiry > request.expiry + 120))) throw new Error();
            this.update({ stage: "waiting", request, message: "Open Authenticator or scan the code. Review and accept your ID." });
            this.timer = this.schedule(() => this.poll(generation), 2000);
        } catch { if (generation === this.generation) this.update({ stage: "error", request: null, message: "Your ID could not be prepared. Check eligibility again or contact your Aya administrator." }); }
    }
    async poll(generation) {
        if (generation !== this.generation || this.state.stage !== "waiting") return;
        if ((this.state.request.statusExpiry ?? this.state.request.expiry) * 1000 <= this.now()) { this.update({ stage: "expired", request: null, message: "The waiting period ended. Outcome is unconfirmed; contact the pilot administrator before requesting another ID." }); return; }
        if (this.state.request.expiry * 1000 <= this.now()) this.update({ message: "The QR code expired. Checking for a final result…" });
        try {
            const response = await this.fetch("/api/request-status?id=" + encodeURIComponent(this.state.request.id), { credentials: "same-origin", cache: "no-store" });
            if (!response.ok) throw new Error();
            const result = await response.json();
            if (generation !== this.generation) return;
            if (result.status === "issuance_successful") this.update({ stage: "success", request: null, message: "Your Aya Employee ID was issued. Find it in Microsoft Authenticator." });
            else if (result.status === "issuance_error") this.update({ stage: "error", request: null, message: "Issuance was not completed. Check eligibility to try again." });
            else if (["request_created", "request_retrieved"].includes(result.status)) {
                if (result.status === "request_retrieved") this.update({ message: "Request opened. Complete the steps in Authenticator." });
                this.timer = this.schedule(() => this.poll(generation), 2000);
            } else throw new Error();
        } catch { if (generation === this.generation) this.update({ stage: "error", request: null, message: "We could not confirm the outcome. Check Authenticator before requesting another ID." }); }
    }
    stop() {
        ++this.generation; this.cancel(this.timer);
        this.update({ stage: "stopped", request: null, message: "Stopped checking. An existing wallet request may still finish; check Authenticator before starting again." });
    }
}
if (typeof module !== "undefined") module.exports = { AyaEmployeeFlow };
if (typeof document !== "undefined" && document.getElementById("aya-issuance")) {
    const element = id => document.getElementById(id);
    let lastUrl = null;
    const flow = new AyaEmployeeFlow({ fetch: window.fetch.bind(window), enabled: element("aya-issuance").dataset.ready === "true",
        token: document.querySelector('input[name="__RequestVerificationToken"]').value,
        changed: state => {
            element("aya-status").textContent = state.message;
            element("check-eligibility").disabled = !flow.enabled || ["loading", "waiting"].includes(state.stage);
            element("issue-credential").hidden = state.stage !== "ready";
            element("stop-waiting").hidden = !["loading", "waiting"].includes(state.stage);
            const walletExpired = state.request && state.request.expiry * 1000 <= Date.now();
            element("aya-wallet").hidden = state.stage !== "waiting" || walletExpired;
            if (state.profile) {
                element("employee-name").textContent = state.profile.displayName;
                element("employee-number").textContent = state.profile.employeeId;
                element("employee-company").textContent = state.profile.companyName;
            }
            if (state.request && !walletExpired && state.request.url !== lastUrl) {
                lastUrl = state.request.url; element("authenticator-link").href = lastUrl;
                element("qrcode").replaceChildren(); new QRCode(element("qrcode"), { text: lastUrl, width: 180, height: 180 });
                element("pinCode").hidden = !state.request.pin;
                element("pinCode").textContent = state.request.pin ? "Your one-time PIN: " + state.request.pin : "";
            }
            if (!state.request || walletExpired) { lastUrl = null; element("authenticator-link").removeAttribute("href"); element("qrcode").replaceChildren(); element("pinCode").textContent = ""; }
        }
    });
    element("check-eligibility").addEventListener("click", () => flow.profile());
    element("issue-credential").addEventListener("click", () => flow.issue());
    element("stop-waiting").addEventListener("click", () => flow.stop());
}
