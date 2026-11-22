using System;
using System.Text.RegularExpressions;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace AspNetCoreVerifiableCredentials
{
    public static class AyaRequestStorageConfiguration
    {
        public static bool IsConfigured(IConfiguration config) => TrySettings(config, out _, out _, out _);
        private static bool TrySettings(IConfiguration config, out Uri endpoint, out string table, out string ledger)
        {
            table = config["Aya:RequestTable:TableName"];
            ledger = config["Aya:RequestTable:LedgerId"];
            return Uri.TryCreate(config["Aya:RequestTable:Endpoint"], UriKind.Absolute, out endpoint) &&
                endpoint.Scheme == "https" && endpoint.IsDefaultPort && endpoint.AbsolutePath == "/" &&
                endpoint.UserInfo == "" && endpoint.Query == "" && endpoint.Fragment == "" &&
                Regex.IsMatch(endpoint.Host, @"^[a-z0-9]{3,24}\.table\.core\.windows\.net$") &&
                !endpoint.Host.EndsWith("-secondary.table.core.windows.net", StringComparison.Ordinal) &&
                table != null && Regex.IsMatch(table, @"^[A-Za-z][A-Za-z0-9]{2,62}$") &&
                !table.Equals("tables", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(ledger, "D", out var id) && id != Guid.Empty && ledger == id.ToString("D");
        }
        public static IAyaRequestPersistence Create(IConfiguration config)
        {
            if (!TrySettings(config, out var endpoint, out var table, out var ledger)) return new UnconfiguredAyaRequestPersistence();
            try
            {
                var options = new TableClientOptions();
                // No SDK retries: an ambiguous write must not be mistaken for a definitive conflict.
                options.Retry.MaxRetries = 0;
                options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
                options.Diagnostics.IsLoggingEnabled = false;
                options.Diagnostics.IsLoggingContentEnabled = false;
                options.Diagnostics.IsDistributedTracingEnabled = false;
                var identityOptions = new ManagedIdentityCredentialOptions(ManagedIdentityId.SystemAssigned);
                identityOptions.Retry.MaxRetries = 0;
                identityOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
                identityOptions.Diagnostics.IsLoggingEnabled = false;
                identityOptions.Diagnostics.IsLoggingContentEnabled = false;
                identityOptions.Diagnostics.IsDistributedTracingEnabled = false;
                var credential = new ManagedIdentityCredential(identityOptions);
                var client = new TableClient(endpoint, table, credential, options);
                return new AyaTableRequestPersistence(new AzureAyaTableOperations(client), ledger);
            }
            catch { throw new AyaStorageException(); }
        }
    }
}
