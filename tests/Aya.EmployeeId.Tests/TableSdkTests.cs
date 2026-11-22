using System.Net;
using System.Text;
using AspNetCoreVerifiableCredentials;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aya.EmployeeId.Tests;

public sealed class TableSdkTests
{
    private sealed class SyntheticCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-storage-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class SyntheticTransport : HttpMessageHandler
    {
        public readonly List<(string Method, string Match, string Body)> Calls = new();
        public HttpStatusCode Status = HttpStatusCode.NoContent;
        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            string body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            Calls.Add((request.Method.Method, request.Headers.TryGetValues("If-Match", out var match) ? match.Single() : null, body));
            var response = new HttpResponseMessage(Status) { RequestMessage = request };
            if ((int)Status >= 400) response.Content = new StringContent("{\"odata.error\":{\"code\":\"UpdateConditionNotSatisfied\",\"message\":{\"lang\":\"en-US\",\"value\":\"synthetic conflict\"}}}", Encoding.UTF8, "application/json");
            return response;
        }
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => Respond(request);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Respond(request));
    }

    [Fact]
    public void Real_sdk_sends_insert_only_chunks_and_exact_etag_conditional_replace_without_network()
    {
        using var transport = new SyntheticTransport(); using var http = new HttpClient(transport);
        var options = new TableClientOptions { Transport = new HttpClientTransport(http) }; options.Retry.MaxRetries = 0;
        var operations = new AzureAyaTableOperations(new TableClient(new Uri("https://synthetic.table.core.windows.net"), "SyntheticRequests", new SyntheticCredential(), options));
        operations.Add(new TableEntity(TablePersistenceTests.Ledger, "synthetic-chunk") { ["Data"] = new byte[] { 1, 2 } }, default);
        operations.Replace(new TableEntity(TablePersistenceTests.Ledger, "head") { ["Schema"] = 1 }, new ETag("\"synthetic-etag\""), default);
        Assert.Equal("POST", transport.Calls[0].Method); Assert.Null(transport.Calls[0].Match);
        Assert.Equal("PUT", transport.Calls[1].Method); Assert.Equal("\"synthetic-etag\"", transport.Calls[1].Match);
        transport.Status = HttpStatusCode.PreconditionFailed;
        var ex = Assert.Throws<RequestFailedException>(() => operations.Replace(new TableEntity(TablePersistenceTests.Ledger, "head"), new ETag("\"stale\""), default));
        Assert.Equal(412, ex.Status); Assert.Equal(3, transport.Calls.Count);
    }

    [Fact]
    public void Valid_configuration_constructs_managed_identity_adapter_without_accessing_services()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string> {
            ["Aya:RequestTable:Endpoint"] = "https://synthetic.table.core.windows.net",
            ["Aya:RequestTable:TableName"] = "SyntheticRequests", ["Aya:RequestTable:LedgerId"] = TablePersistenceTests.Ledger
        }).Build();
        Assert.True(AyaRequestStorageConfiguration.IsConfigured(config));
        // Construction does not acquire a token, probe storage or initialize the head.
        Assert.IsType<AyaTableRequestPersistence>(AyaRequestStorageConfiguration.Create(config));
    }
}
