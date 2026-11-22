using System;
using Microsoft.Extensions.Caching.Memory;

namespace AspNetCoreVerifiableCredentials
{
    // Separate cache namespace prevents sample presentation/photo state from being used as issuance state.
    // This store is process-local; a shared atomic store is required before multi-instance deployment.
    public sealed class AyaIssuanceStateStore
    {
        private sealed class State
        {
            public string Owner;
            public string RequestId;
            public string Status = "request_created";
            public DateTimeOffset Expires;
        }
        private readonly IMemoryCache _cache;
        private readonly TimeProvider _time;
        private readonly object _gate = new object();
        public AyaIssuanceStateStore(IMemoryCache cache, TimeProvider time)
        {
            _cache = cache;
            _time = time;
        }
        private static string Key(string id) => "aya:issuance:" + id;
        public bool Create(string id, string owner, string requestId, DateTimeOffset expires)
        {
            if (!Guid.TryParse(id, out _) || string.IsNullOrWhiteSpace(owner) ||
                string.IsNullOrWhiteSpace(requestId) || requestId.Length > 256 || expires <= _time.GetUtcNow()) return false;
            lock (_gate)
            {
                if (_cache.TryGetValue(Key(id), out _)) return false;
                _cache.Set(Key(id), new State { Owner = owner, RequestId = requestId, Expires = expires }, expires);
                return true;
            }
        }
        public bool TryGetStatus(string id, string owner, out string status)
        {
            status = null;
            if (!Guid.TryParse(id, out _)) return false;
            lock (_gate)
            {
                if (!_cache.TryGetValue(Key(id), out State state) || state.Expires <= _time.GetUtcNow() ||
                    !string.Equals(owner, state.Owner, StringComparison.Ordinal)) return false;
                status = state.Status;
                return true;
            }
        }
        public bool Apply(CallbackEvent callback)
        {
            if (callback == null || !Guid.TryParse(callback.state, out _) ||
                string.IsNullOrWhiteSpace(callback.requestId) || callback.requestId.Length > 256 ||
                (callback.requestStatus != "request_retrieved" && callback.requestStatus != "issuance_successful" &&
                 callback.requestStatus != "issuance_error")) return false;
            lock (_gate)
            {
                if (!_cache.TryGetValue(Key(callback.state), out State state) || state.Expires <= _time.GetUtcNow() ||
                    !string.Equals(state.RequestId, callback.requestId, StringComparison.Ordinal)) return false;
                if (state.Status == callback.requestStatus) return true; // idempotent delivery, no TTL extension
                if (state.Status == "issuance_successful" || state.Status == "issuance_error") return false;
                state.Status = callback.requestStatus;
                return true;
            }
        }
    }
}
