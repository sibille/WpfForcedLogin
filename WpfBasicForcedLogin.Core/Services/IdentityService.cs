﻿using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using WpfBasicForcedLogin.Core.Contracts.Services;
using WpfBasicForcedLogin.Core.Helpers;

namespace WpfBasicForcedLogin.Core.Services
{
    public class IdentityService : IIdentityService
    {
        //// For more information about using Identity, see
        //// https://github.com/Microsoft/WindowsTemplateStudio/blob/master/docs/UWP/features/identity.md
        ////
        //// Read more about Microsoft Identity Client here
        //// https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/wiki
        //// https://docs.microsoft.com/azure/active-directory/develop/v2-overview

        // TODO WTS: The IdentityClientId in App.config is provided to test the project in development environments.
        // Please, follow these steps to create a new one with Azure Active Directory and replace it before going to production.
        // https://docs.microsoft.com/azure/active-directory/develop/quickstart-register-app
        // private readonly string _clientId = ConfigurationManager.AppSettings["IdentityClientId"];
        private readonly string _clientId = "31f2256a-e9aa-4626-be94-21c17add8fd9";

        private readonly string[] _graphScopes = new string[] { "user.read" };
        private readonly object _fileLock = new object();
        private readonly string _msalCacheFilePath = Assembly.GetExecutingAssembly().Location + ".msalcache.bin3";

        private bool _integratedAuthAvailable;
        private IPublicClientApplication _client;
        private AuthenticationResult _authenticationResult;

        public event EventHandler LoggedIn;

        public event EventHandler LoggedOut;

        public void InitializeWithAadAndPersonalMsAccounts(string redirectUri = null, bool addCache = false)
        {
            _integratedAuthAvailable = false;
            _client = PublicClientApplicationBuilder.Create(_clientId)
                                                    .WithAuthority(AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount)
                                                    .WithRedirectUri(redirectUri)
                                                    .Build();
            AddCacheIfNeeded(addCache);
        }

        public void InitializeWithAadMultipleOrgs(bool integratedAuth = false, string redirectUri = null, bool addCache = false)
        {
            _integratedAuthAvailable = integratedAuth;
            _client = PublicClientApplicationBuilder.Create(_clientId)
                                                    .WithAuthority(AadAuthorityAudience.AzureAdMultipleOrgs)
                                                    .WithRedirectUri(redirectUri)
                                                    .Build();
            AddCacheIfNeeded(addCache);
        }

        public void InitializeWithAadSingleOrg(string tenant, bool integratedAuth = false, string redirectUri = null, bool addCache = false)
        {
            _integratedAuthAvailable = integratedAuth;
            _client = PublicClientApplicationBuilder.Create(_clientId)
                                                    .WithAuthority(AzureCloudInstance.AzurePublic, tenant)
                                                    .WithRedirectUri(redirectUri)
                                                    .Build();
            AddCacheIfNeeded(addCache);
        }

        private void AddCacheIfNeeded(bool addCache)
        {
            if (addCache)
            {
                // .Net Core Apps should provide a mechanism to serialize and deserialize the user token cache
                // https://aka.ms/msal-net-token-cache-serialization
                _client.UserTokenCache.SetBeforeAccess(BeforeAccessNotification);
                _client.UserTokenCache.SetAfterAccess(AfterAccessNotification);
            }
        }

        public bool IsLoggedIn() => _authenticationResult != null;

        public async Task<LoginResultType> LoginAsync()
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                return LoginResultType.NoNetworkAvailable;
            }

            try
            {
                var accounts = await _client.GetAccountsAsync();
                _authenticationResult = await _client.AcquireTokenInteractive(_graphScopes)
                                                     .WithAccount(accounts.FirstOrDefault())
                                                     .ExecuteAsync();
                if (!IsAuthorized())
                {
                    _authenticationResult = null;
                    return LoginResultType.Unauthorized;
                }

                LoggedIn?.Invoke(this, EventArgs.Empty);
                return LoginResultType.Success;
            }
            catch (MsalClientException ex)
            {
                if (ex.ErrorCode == "authentication_canceled")
                {
                    return LoginResultType.CancelledByUser;
                }

                return LoginResultType.UnknownError;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                return LoginResultType.UnknownError;
            }
        }

        public bool IsAuthorized()
        {
            // TODO WTS: You can also add extra authorization checks here.
            // i.e.: Checks permisions of _authenticationResult.Account.Username in a database.
            return true;
        }

        public string GetAccountUserName()
        {
            return _authenticationResult?.Account?.Username;
        }

        public async Task LogoutAsync()
        {
            try
            {
                var accounts = await _client.GetAccountsAsync();
                var account = accounts.FirstOrDefault();
                if (account != null)
                {
                    await _client.RemoveAsync(account);
                }

                _authenticationResult = null;
                LoggedOut?.Invoke(this, EventArgs.Empty);
            }
            catch (MsalException)
            {
                // TODO WTS: LogoutAsync can fail please handle exceptions as appropriate to your scenario
                // For more info on MsalExceptions see
                // https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/wiki/exceptions
            }
        }

        public async Task<string> GetAccessTokenForGraphAsync() => await GetAccessTokenAsync(_graphScopes);

        public async Task<string> GetAccessTokenAsync(string[] scopes)
        {
            var acquireTokenSuccess = await AcquireTokenSilentAsync(scopes);
            if (acquireTokenSuccess)
            {
                return _authenticationResult.AccessToken;
            }
            else
            {
                try
                {
                    // Interactive authentication is required
                    var accounts = await _client.GetAccountsAsync();
                    _authenticationResult = await _client.AcquireTokenInteractive(scopes)
                                                         .WithAccount(accounts.FirstOrDefault())
                                                         .ExecuteAsync();
                    return _authenticationResult.AccessToken;
                }
                catch (MsalException)
                {
                    // AcquireTokenSilent and AcquireTokenInteractive failed, the session will be closed.
                    _authenticationResult = null;
                    LoggedOut?.Invoke(this, EventArgs.Empty);
                    return string.Empty;
                }
            }
        }

        public async Task<bool> AcquireTokenSilentAsync() => await AcquireTokenSilentAsync(_graphScopes);

        private async Task<bool> AcquireTokenSilentAsync(string[] scopes)
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                return false;
            }

            try
            {
                var accounts = await _client.GetAccountsAsync();
                _authenticationResult = await _client.AcquireTokenSilent(scopes, accounts.FirstOrDefault())
                                                     .ExecuteAsync();
                return true;
            }
            catch (MsalUiRequiredException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                if (_integratedAuthAvailable)
                {
                    try
                    {
                        _authenticationResult = await _client.AcquireTokenByIntegratedWindowsAuth(_graphScopes)
                                                             .ExecuteAsync();
                        return true;
                    }
                    catch (MsalUiRequiredException)
                    {
                        // Interactive authentication is required
                        return false;
                    }
                }
                else
                {
                    // Interactive authentication is required
                    return false;
                }
            }
            catch (MsalException)
            {
                // TODO WTS: Silentauth failed, please handle this exception as appropriate to your scenario
                // For more info on MsalExceptions see
                // https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/wiki/exceptions
                return false;
            }
        }

        private void BeforeAccessNotification(TokenCacheNotificationArgs args)
        {
            lock (_fileLock)
            {
                if (File.Exists(_msalCacheFilePath))
                {
                    var encryptedData = File.ReadAllBytes(_msalCacheFilePath);
                    var data = ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.CurrentUser);
                    args.TokenCache.DeserializeMsalV3(data);
                }
            }
        }

        private void AfterAccessNotification(TokenCacheNotificationArgs args)
        {
            if (args.HasStateChanged)
            {
                lock (_fileLock)
                {
                    var data = args.TokenCache.SerializeMsalV3();
                    var encryptedData = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                    File.WriteAllBytes(_msalCacheFilePath, encryptedData);
                }
            }
        }
    }
}