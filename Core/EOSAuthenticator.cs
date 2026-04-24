using System;
using System.Threading;
using System.Threading.Tasks;
using Epic.OnlineServices;
using Epic.OnlineServices.Auth;
using Epic.OnlineServices.Connect;
using UnityEngine;
using AuthCredentials = Epic.OnlineServices.Auth.Credentials;
using AuthLoginCallbackInfo = Epic.OnlineServices.Auth.LoginCallbackInfo;
using AuthLoginOptions = Epic.OnlineServices.Auth.LoginOptions;
using ConnectCredentials = Epic.OnlineServices.Connect.Credentials;
using ConnectLoginCallbackInfo = Epic.OnlineServices.Connect.LoginCallbackInfo;
using ConnectLoginOptions = Epic.OnlineServices.Connect.LoginOptions;

namespace FishNet.Transporting.EpicNetPlugin
{
    internal static class EOSAuthenticator
    {
        internal static async Task<Result> Authenticate(AuthData authData, CancellationToken ct)
        {
            if (authData is null) return Result.InvalidParameters;

            return authData.loginCredentialType switch
            {
                LoginCredentialType.AccountPortal or
                LoginCredentialType.ExchangeCode or
                LoginCredentialType.ExternalAuth or
                LoginCredentialType.Password or
                LoginCredentialType.PersistentAuth or
                LoginCredentialType.RefreshToken =>
                    await ConnectLoginAsync(authData.token, authData.externalCredentialType,
                        authData.displayName, authData.automaticallyCreateConnectAccount, authData.timeout, ct),

                LoginCredentialType.Developer =>
                    await DeveloperLoginAsync(authData, ct),

                LoginCredentialType.DeviceCode =>
                    await DeviceIdLoginAsync(authData, ct),

                _ => Result.InvalidParameters
            };
        }

        static async Task<Result> DeveloperLoginAsync(AuthData authData, CancellationToken ct)
        {
            var authResult = await AuthLoginAsync(authData.id, authData.token,
                authData.loginCredentialType, authData.externalCredentialType,
                authData.authScopeFlags, authData.timeout, ct);

            if (authResult.ResultCode != Result.Success)
                return authResult.ResultCode;

            var authInterface = EOS.GetAuthInterface();
            if (authInterface is null) return Result.InvalidState;

            var copyOptions = new CopyUserAuthTokenOptions();
            var copyResult = authInterface.CopyUserAuthToken(ref copyOptions, authResult.LocalUserId, out var authToken);

            if (copyResult != Result.Success)
                return copyResult;

            string accessToken = authToken?.AccessToken;
            authToken?.Release();

            return await ConnectLoginAsync(accessToken, authData.externalCredentialType,
                authData.displayName, authData.automaticallyCreateConnectAccount, authData.timeout, ct);
        }

        static async Task<Result> DeviceIdLoginAsync(AuthData authData, CancellationToken ct)
        {
            var result = await ConnectLoginAsync(authData.token, authData.externalCredentialType,
                authData.displayName, authData.automaticallyCreateConnectAccount, authData.timeout, ct);

            if (result != Result.NotFound || !authData.automaticallyCreateDeviceId)
                return result;

            var deviceResult = await CreateDeviceIdAsync(authData.timeout, ct);
            if (deviceResult != Result.Success && deviceResult != Result.DuplicateNotAllowed)
                return deviceResult;

            return await ConnectLoginAsync(authData.token, authData.externalCredentialType,
                authData.displayName, authData.automaticallyCreateConnectAccount, authData.timeout, ct);
        }

        static async Task<Result> ConnectLoginAsync(string token, ExternalCredentialType externalCredentialType,
            string displayName, bool autoCreateAccount, float timeout, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ConnectLoginCallbackInfo>();
            using var reg = ct.Register(() => tcs.TrySetCanceled());

            var connectInterface = EOS.GetConnectInterface();
            if (connectInterface is null) return Result.InvalidState;

            var loginOptions = new ConnectLoginOptions
            {
                Credentials = new ConnectCredentials { Token = token, Type = externalCredentialType },
                UserLoginInfo = new UserLoginInfo { DisplayName = displayName }
            };

            connectInterface.Login(ref loginOptions, null,
                (ref ConnectLoginCallbackInfo data) => tcs.TrySetResult(data));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = Task.Delay(TimeSpan.FromSeconds(timeout), timeoutCts.Token);
            var completedTask = await Task.WhenAny(tcs.Task, delayTask);
            timeoutCts.Cancel();

            if (completedTask != tcs.Task)
                return Result.TimedOut;

            if (ct.IsCancellationRequested) return Result.TimedOut;

            var callbackInfo = await tcs.Task;

            if (callbackInfo.ResultCode == Result.InvalidUser && autoCreateAccount)
            {
                var createResult = await CreateUserAsync(callbackInfo.ContinuanceToken, timeout, ct);
                return createResult != Result.Success
                    ? createResult
                    : await ConnectLoginAsync(token, externalCredentialType, displayName, false, timeout, ct);
            }

            return callbackInfo.ResultCode;
        }

        static async Task<AuthLoginCallbackInfo> AuthLoginAsync(string id, string token,
            LoginCredentialType loginCredentialType, ExternalCredentialType externalCredentialType,
            AuthScopeFlags scopeFlags, float timeout, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<AuthLoginCallbackInfo>();
            using var reg = ct.Register(() => tcs.TrySetCanceled());

            var authInterface = EOS.GetAuthInterface();
            if (authInterface is null)
                return new AuthLoginCallbackInfo { ResultCode = Result.InvalidState };

            var loginOptions = new AuthLoginOptions
            {
                Credentials = new AuthCredentials
                {
                    Id = id, Token = token, Type = loginCredentialType,
                    SystemAuthCredentialsOptions = default, ExternalType = externalCredentialType
                },
                ScopeFlags = scopeFlags
            };

            authInterface.Login(ref loginOptions, null,
                (ref AuthLoginCallbackInfo data) => tcs.TrySetResult(data));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = Task.Delay(TimeSpan.FromSeconds(timeout), timeoutCts.Token);
            var completedTask = await Task.WhenAny(tcs.Task, delayTask);
            timeoutCts.Cancel();

            if (completedTask != tcs.Task)
                return new AuthLoginCallbackInfo { ResultCode = Result.TimedOut };

            return ct.IsCancellationRequested
                ? new AuthLoginCallbackInfo { ResultCode = Result.TimedOut }
                : await tcs.Task;
        }

        static async Task<Result> CreateUserAsync(ContinuanceToken continuanceToken, float timeout, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<CreateUserCallbackInfo>();
            using var reg = ct.Register(() => tcs.TrySetCanceled());

            var connectInterface = EOS.GetConnectInterface();
            if (connectInterface is null) return Result.InvalidState;

            var options = new CreateUserOptions { ContinuanceToken = continuanceToken };
            connectInterface.CreateUser(ref options, null,
                (ref CreateUserCallbackInfo data) => tcs.TrySetResult(data));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = Task.Delay(TimeSpan.FromSeconds(timeout), timeoutCts.Token);
            var completedTask = await Task.WhenAny(tcs.Task, delayTask);
            timeoutCts.Cancel();

            if (completedTask != tcs.Task)
                return Result.TimedOut;

            var result = ct.IsCancellationRequested ? Result.TimedOut : (await tcs.Task).ResultCode;

            continuanceToken?.Release();

            return result;
        }

        static async Task<Result> CreateDeviceIdAsync(float timeout, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<CreateDeviceIdCallbackInfo>();
            using var reg = ct.Register(() => tcs.TrySetCanceled());

            var connectInterface = EOS.GetConnectInterface();
            if (connectInterface is null) return Result.InvalidState;

            var options = new CreateDeviceIdOptions
            {
                DeviceModel = $"{SystemInfo.deviceType}"
            };
            connectInterface.CreateDeviceId(ref options, null,
                (ref CreateDeviceIdCallbackInfo data) => tcs.TrySetResult(data));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = Task.Delay(TimeSpan.FromSeconds(timeout), timeoutCts.Token);
            var completedTask = await Task.WhenAny(tcs.Task, delayTask);
            timeoutCts.Cancel();

            if (completedTask != tcs.Task)
                return Result.TimedOut;

            return ct.IsCancellationRequested ? Result.TimedOut : (await tcs.Task).ResultCode;
        }
    }
}