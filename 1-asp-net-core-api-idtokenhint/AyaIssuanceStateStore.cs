using System;
using System.Linq;

namespace AspNetCoreVerifiableCredentials
{
    // Tracks both request kinds; the historical class name is retained to avoid unrelated API churn.
    public sealed class AyaIssuanceStateStore
    {
        private readonly IAyaRequestPersistence _storage;
        private readonly TimeProvider _time;
        public AyaIssuanceStateStore(IAyaRequestPersistence storage, TimeProvider time) { _storage = storage; _time = time; }
        public void CheckStorage() => _storage.Read(rows => rows.Count);
        private static bool Id(string id) => Guid.TryParseExact(id, "D", out var parsed) && parsed != Guid.Empty;
        private static bool Terminal(string status) => status == "issuance_successful" || status == "issuance_error" ||
            status == "presentation_verified" || status == "presentation_error";
        public bool Begin(string id, string owner, string kind, string issuer)
        {
            if (!Id(id) || string.IsNullOrWhiteSpace(owner) || (kind != "issuance" && kind != "presentation") ||
                string.IsNullOrWhiteSpace(issuer)) return false;
            return _storage.Commit(rows => {
                var now = _time.GetUtcNow();
                foreach (var key in rows.Where(p => p.Value.RetainUntil <= now).Select(p => p.Key).ToArray()) rows.Remove(key);
                if (rows.Count >= 2000 || rows.ContainsKey(id) || rows.Values.Any(r => r.Owner == owner && r.Kind == kind &&
                    r.AcceptUntil > now && !Terminal(r.Status))) return false;
                rows.Add(id, new AyaRequestRecord { Id = id, Owner = owner, Kind = kind, Issuer = issuer,
                    Created = now, AcceptUntil = now.AddMinutes(10), RetainUntil = now.AddHours(24) });
                return true;
            });
        }
        public bool Bind(string id, string requestId, DateTimeOffset expires)
        {
            if (!Id(id) || string.IsNullOrWhiteSpace(requestId) || requestId.Length > 256) return false;
            return _storage.Commit(rows => {
                var now = _time.GetUtcNow();
                if (expires <= now || expires > now.AddMinutes(10)) return false;
                if (!rows.TryGetValue(id, out var row) || row.RequestId != null || row.AcceptUntil <= now) return false;
                row.RequestId = requestId; row.AcceptUntil = expires.AddMinutes(2);
                if (row.Pending.Any(e => e.RequestId != requestId || e.Received >= row.AcceptUntil))
                { row.Status = "outcome_unknown"; row.Pending.Clear(); return false; }
                row.Status = "request_created";
                foreach (var e in row.Pending)
                    if (!Transition(row, e.Status, e.CredentialExpires))
                    { row.Status = "outcome_unknown"; row.Pending.Clear(); return false; }
                row.Pending.Clear(); return true;
            });
        }
        public void Unknown(string id) => _storage.Commit(rows => {
            if (rows.TryGetValue(id, out var row) && !Terminal(row.Status)) row.Status = "outcome_unknown";
            return true;
        });
        // Direct pre-bound creation remains useful for callers that already possess service metadata.
        public bool Create(string id, string owner, string requestId, DateTimeOffset expires)
        {
            if (!Id(id) || string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(requestId) || requestId.Length > 256 || expires <= _time.GetUtcNow()) return false;
            return _storage.Commit(rows => {
                if (rows.ContainsKey(id)) return false;
                rows.Add(id, new AyaRequestRecord { Id = id, Owner = owner, Kind = "issuance", RequestId = requestId,
                    Status = "request_created", Created = _time.GetUtcNow(), AcceptUntil = expires, RetainUntil = expires });
                return true;
            });
        }
        public bool TryGetStatus(string id, string owner, out string status) => TryGetStatus(id, owner, "issuance", out status);
        public bool TryGetStatus(string id, string owner, string kind, out string status)
        {
            status = null; if (!Id(id)) return false;
            status = _storage.Read(rows => {
                var now = _time.GetUtcNow();
                if (!rows.TryGetValue(id, out var row) || row.Kind != kind || row.Owner != owner || row.RetainUntil <= now) return null;
                if (row.Status == "presentation_verified" && (!row.CredentialExpires.HasValue || row.CredentialExpires <= now)) return "presentation_error";
                if (!Terminal(row.Status) && row.AcceptUntil <= now) return "outcome_unknown";
                return row.Status;
            });
            return status != null;
        }
        public bool Apply(CallbackEvent callback) => Apply(callback, "issuance");
        public bool Apply(CallbackEvent callback, string kind)
        {
            if (callback == null || !Id(callback.state) || string.IsNullOrWhiteSpace(callback.requestId) || callback.requestId.Length > 256 ||
                !(kind == "issuance" ? new[] { "request_retrieved", "issuance_successful", "issuance_error" } :
                    new[] { "request_retrieved", "presentation_verified", "presentation_error" }).Contains(callback.requestStatus)) return false;
            return _storage.Commit(rows => {
                var now = _time.GetUtcNow();
                if (!rows.TryGetValue(callback.state, out var row) || row.Kind != kind || row.RetainUntil <= now) return false;
                string status = callback.requestStatus; DateTimeOffset? credentialExpires = null;
                if (kind == "presentation" && status == "presentation_verified")
                {
                    if (!AyaPresentationPolicy.IsApproved(callback, row.Issuer, now, out var expiry)) status = "presentation_error";
                    else credentialExpires = expiry;
                }
                if (row.RequestId != null && row.RequestId != callback.requestId) return false;
                if (Terminal(row.Status)) return row.Status == status; // acknowledgement only, no extension
                if (row.AcceptUntil <= now) return false;
                if (row.RequestId == null)
                {
                    if (row.Pending.Any(e => e.RequestId != callback.requestId)) return false;
                    if (row.Pending.Any(e => e.Status == status)) return true;
                    if (row.Pending.Count >= 4 || row.Pending.Any(e => Terminal(e.Status))) return false;
                    row.Pending.Add(new AyaPendingEvent { RequestId = callback.requestId, Status = status, Received = now, CredentialExpires = credentialExpires });
                    return true; // durable receipt, never reported as success before Bind
                }
                if (row.Status == "outcome_unknown") return false;
                return Transition(row, status, credentialExpires);
            });
        }
        private static bool Transition(AyaRequestRecord row, string status, DateTimeOffset? expiry)
        {
            if (Terminal(row.Status)) return row.Status == status;
            row.Status = status; row.CredentialExpires = expiry; return true;
        }
    }
}
