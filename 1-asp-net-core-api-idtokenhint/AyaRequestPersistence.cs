using System;
using System.Collections.Generic;

namespace AspNetCoreVerifiableCredentials
{
    // Implementations must isolate readers and commit atomically before returning. Commit delegates
    // must only transform the supplied dictionary: optimistic adapters can re-evaluate them after
    // a definitive conflict. Never put service calls or other side effects inside a delegate.
    public interface IAyaRequestPersistence
    {
        T Read<T>(Func<Dictionary<string, AyaRequestRecord>, T> read);
        T Commit<T>(Func<Dictionary<string, AyaRequestRecord>, T> change);
    }

    public sealed class AyaRequestRecord
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Owner { get; set; }
        public string Issuer { get; set; }
        public string RequestId { get; set; }
        public string Status { get; set; } = "request_pending";
        public DateTimeOffset Created { get; set; }
        public DateTimeOffset AcceptUntil { get; set; }
        public DateTimeOffset RetainUntil { get; set; }
        public DateTimeOffset? CredentialExpires { get; set; }
        public List<AyaPendingEvent> Pending { get; set; } = new();
    }
    public sealed class AyaPendingEvent
    {
        public string RequestId { get; set; }
        public string Status { get; set; }
        public DateTimeOffset Received { get; set; }
        public DateTimeOffset? CredentialExpires { get; set; }
    }
    public sealed class AyaStorageException : Exception
    {
        public AyaStorageException() : base("Request storage is unavailable. Administrator review required.") { }
    }
    public sealed class UnconfiguredAyaRequestPersistence : IAyaRequestPersistence
    {
        public T Read<T>(Func<Dictionary<string, AyaRequestRecord>, T> read) => throw new AyaStorageException();
        public T Commit<T>(Func<Dictionary<string, AyaRequestRecord>, T> change) => throw new AyaStorageException();
    }

}
