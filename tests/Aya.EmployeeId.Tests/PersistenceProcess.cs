using AspNetCoreVerifiableCredentials;
namespace Aya.EmployeeId.Tests;
// Child-process probe for abrupt termination and OS-level lock release. Never part of the web application.
internal static class PersistenceProcess
{
    public static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        using var disk = new FileAyaRequestPersistence(args[0]);
        var store = new AyaIssuanceStateStore(disk, TimeProvider.System);
        if (!store.Begin(args[1], "synthetic-owner", "issuance", "did:example:synthetic") ||
            !store.Bind(args[1], "synthetic-request", DateTimeOffset.UtcNow.AddMinutes(5)) ||
            !store.Apply(new CallbackEvent { state = args[1], requestId = "synthetic-request", requestStatus = "issuance_successful" })) return 3;
        Console.WriteLine("committed"); Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite); return 0;
    }
}
