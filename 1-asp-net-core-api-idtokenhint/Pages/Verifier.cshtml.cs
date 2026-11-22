using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace AspNetCoreVerifiableCredentials.Pages
{
    public class VerifierModel : PageModel
    {
        private readonly AyaRequestConfiguration _settings;
        public VerifierModel(AyaRequestConfiguration settings) => _settings = settings;
        public IActionResult OnGet()
        {
            try { _settings.Owner(HttpContext); return Page(); }
            catch (UnauthorizedAccessException) { return Unauthorized(); }
        }
    }
}
