using AspNetCoreVerifiableCredentials;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Aya.EmployeeId.Tests;
public sealed class StateStoreTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public void Sample_state_is_not_issuance_state()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions()); var id = Guid.NewGuid().ToString();
        cache.Set(id, "synthetic sample state");
        var store = new AyaIssuanceStateStore(new SyntheticPersistence(), TimeProvider.System);
        Assert.False(store.Apply(new CallbackEvent { state = id, requestId = "synthetic-request", requestStatus = "issuance_successful" }));
        Assert.False(store.TryGetStatus(id, IssuanceTests.Owner, out _));
    }
    [Fact]
    public void Duplicate_callbacks_do_not_extend_expiry()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions()); var clock = new Clock();
        var store = new AyaIssuanceStateStore(new SyntheticPersistence(), clock); var id = Guid.NewGuid().ToString();
        Assert.True(store.Create(id, IssuanceTests.Owner, "synthetic-request", clock.Now.AddMinutes(1)));
        var callback = new CallbackEvent { state = id, requestId = "synthetic-request", requestStatus = "request_retrieved" };
        Assert.True(store.Apply(callback)); clock.Now = clock.Now.AddSeconds(50); Assert.True(store.Apply(callback));
        clock.Now = clock.Now.AddSeconds(11);
        Assert.False(store.Apply(callback)); Assert.False(store.TryGetStatus(id, IssuanceTests.Owner, out _));
    }
    [Fact]
    public async Task Concurrent_duplicates_are_idempotent()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new AyaIssuanceStateStore(new SyntheticPersistence(), TimeProvider.System); var id = Guid.NewGuid().ToString();
        Assert.True(store.Create(id, IssuanceTests.Owner, "synthetic-request", DateTimeOffset.UtcNow.AddMinutes(1)));
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => store.Apply(
            new CallbackEvent { state = id, requestId = "synthetic-request", requestStatus = "issuance_successful" }))));
        Assert.All(results, Assert.True); Assert.True(store.TryGetStatus(id, IssuanceTests.Owner, out var status)); Assert.Equal("issuance_successful", status);
    }
    [Fact]
    public void Existing_state_cannot_be_reassigned()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions()); var store = new AyaIssuanceStateStore(new SyntheticPersistence(), TimeProvider.System); var id = Guid.NewGuid().ToString();
        Assert.True(store.Create(id, IssuanceTests.Owner, "synthetic-request", DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.False(store.Create(id, IssuanceTests.Other, "other-request", DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.False(store.TryGetStatus(id, IssuanceTests.Other, out _));
    }
}
