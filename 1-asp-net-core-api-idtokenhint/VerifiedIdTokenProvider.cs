using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace AspNetCoreVerifiableCredentials
{
    public interface IVerifiedIdTokenProvider
    {
        Task<(string token, string error, string error_description)> GetAccessToken();
    }
    public sealed class VerifiedIdTokenProvider : IVerifiedIdTokenProvider
    {
        private readonly IConfiguration _configuration;
        public VerifiedIdTokenProvider(IConfiguration configuration) => _configuration = configuration;
        public Task<(string token, string error, string error_description)> GetAccessToken() =>
            MsalAccessTokenHandler.GetAccessToken(_configuration);
    }
}
