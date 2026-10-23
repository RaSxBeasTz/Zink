using Microsoft.Identity.Client;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Zink.Services
{
    internal static class OneDriveAuthenticationService
    {
        internal const string ClientId = "070089d3-2f30-498d-82c9-b4eb59878467";
        // Microsoft requires this delegated scope for the personal-account shared-item API.
        private static readonly string[] Scopes = { "https://graph.microsoft.com/Files.ReadWrite" };
        private static readonly SemaphoreSlim SignInGate = new(1, 1);
        private static IAccount? _currentAccount;
        private static readonly IPublicClientApplication Application = PublicClientApplicationBuilder
            .Create(ClientId)
            .WithAuthority("https://login.microsoftonline.com/common")
            .WithRedirectUri("http://localhost")
            .Build();

        // MSAL handles PKCE, state validation, loopback callback and token refresh. Its cache
        // stays in process memory; no passwords, client secrets or tokens are written to disk.
        internal static async Task<string> GetAccessTokenAsync(bool selectAccount, CancellationToken token)
        {
            await SignInGate.WaitAsync(token);
            try
            {
                if (!selectAccount)
                {
                    var account = _currentAccount ?? (await Application.GetAccountsAsync()).FirstOrDefault();
                    if (account != null)
                    {
                        try { return (await Application.AcquireTokenSilent(Scopes, account).ExecuteAsync(token)).AccessToken; }
                        catch (MsalUiRequiredException) { }
                    }
                }
                var request = Application.AcquireTokenInteractive(Scopes).WithUseEmbeddedWebView(false);
                if (selectAccount) request = request.WithPrompt(Prompt.SelectAccount);
                var result = await request.ExecuteAsync(token);
                _currentAccount = result.Account;
                return result.AccessToken;
            }
            finally { SignInGate.Release(); }
        }
    }
}
