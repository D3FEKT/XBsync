using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using XBsync.Models;
using XboxAuthNet.XboxLive;
using XboxAuthNet.XboxLive.Requests;
using XboxAuthNet.XboxLive.Responses;

namespace XBsync.Services;

public interface IXboxAuthenticationService
{
    XboxProfile? CurrentProfile { get; }
    Task<AuthenticationResult> TrySignInSilentlyAsync(CancellationToken cancellationToken);
    Task<AuthenticationResult> SignInInteractivelyAsync(CancellationToken cancellationToken);
    void SignOut();
}

internal sealed class XboxSession(string token, string userHash, XboxProfile profile, DateTimeOffset expiresAt)
{
    internal string Token { get; } = token;
    internal string UserHash { get; } = userHash;
    internal XboxProfile Profile { get; set; } = profile;
    internal DateTimeOffset ExpiresAt { get; } = expiresAt;
    public override string ToString() => nameof(XboxSession);
}

public sealed class XboxAuthenticationService(HttpClient httpClient, XboxAuthenticationOptions options, IDesktopUiService desktop) : IXboxAuthenticationService
{
    private readonly SemaphoreSlim _signInGate = new(1, 1);
    private readonly object _sessionGate = new();
    private XboxSession? _session;
    private long _generation;
    public XboxProfile? CurrentProfile => Volatile.Read(ref _session)?.Profile;
    internal XboxSession? CurrentSession => Volatile.Read(ref _session);

    public async Task<AuthenticationResult> TrySignInSilentlyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clientId = options.ClientId;
        var scope = options.Scope;
        var generation = BeginAttempt();
        await _signInGate.WaitAsync(cancellationToken);
        try
        {
            CheckGeneration(generation, cancellationToken);
            var hint = ReadXuidHint();
            var candidates = NativeCredentials.ReadXboxTokens(DateTimeOffset.UtcNow);
            var eligible = candidates.Where(candidate => hint is null || candidate.Xuid is null || candidate.Xuid == hint)
                .OrderByDescending(candidate => candidate.ExpiresAt).ToArray();
            if (hint is null && eligible.Select(candidate => candidate.Xuid).Distinct().Count() > 1)
                return Required("Multiple local Xbox accounts were found. Choose an account with Sign in.");
            if (hint is null && eligible.Length > 1 && eligible.Any(candidate => candidate.Xuid is null))
                return Required("The active Xbox account could not be identified. Choose an account with Sign in.");

            var accountMismatch = false;
            foreach (var candidate in eligible)
            {
                CheckGeneration(generation, cancellationToken);
                try
                {
                    var session = await ExchangeAsync(candidate.Token, cancellationToken);
                    if ((hint is not null && session.Profile.Xuid != hint) ||
                        (candidate.Xuid is not null && session.Profile.Xuid != candidate.Xuid))
                    {
                        accountMismatch = true;
                        continue;
                    }
                    return Commit(session, generation, cancellationToken, "Connected using local Xbox credentials.");
                }
                catch (XboxAuthException exception) when (exception.StatusCode is 400 or 401 or 403) { }
            }

            if (accountMismatch || (hint is not null && candidates.Count > 0 && eligible.Length == 0))
                return new(AuthenticationStatus.AccountMismatch, null, "Local credentials belong to a different Xbox account. Use Sign in to choose an account.");

            if (!string.IsNullOrWhiteSpace(clientId))
            {
                var cache = NativeCredentials.ReadRefreshToken(clientId);
                if (cache is not null && hint is not null && cache.Xuid != hint)
                    return new(AuthenticationStatus.AccountMismatch, null, "XBsync's saved account differs from the local Xbox account. Use Sign in to choose an account.");
                for (var source = 0; source < 2; source++)
                {
                    var refreshToken = source == 0 ? cache?.RefreshToken :
                        await ReadReferenceRefreshTokenAsync(options.ReferenceRefreshTokenPath, cancellationToken);
                    CheckGeneration(generation, cancellationToken);
                    if (string.IsNullOrWhiteSpace(refreshToken) || (source == 1 && refreshToken == cache?.RefreshToken)) continue;
                    var response = await RequestTokenAsync(new Dictionary<string, string>
                    {
                        ["client_id"] = clientId, ["scope"] = scope,
                        ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken
                    }, cancellationToken);
                    CheckGeneration(generation, cancellationToken);
                    if (response.Error == "invalid_grant") continue;
                    if (response.Error is not null) return OAuthFailure(response.Error);
                    if (string.IsNullOrWhiteSpace(response.AccessToken))
                        return new(AuthenticationStatus.ServiceFailure, null, "Microsoft could not refresh the saved sign-in. Try again or use Sign in.");
                    var session = await ExchangeMicrosoftTokenAsync(response.AccessToken, cancellationToken);
                    if ((source == 0 && session.Profile.Xuid != cache!.Xuid) || (hint is not null && session.Profile.Xuid != hint))
                        return new(AuthenticationStatus.AccountMismatch, null, "The refreshed identity did not match the expected Xbox account. Use Sign in.");
                    return Commit(session, generation, cancellationToken, source == 0 ?
                        "Connected using XBsync's saved sign-in." : "Connected using the reference app's saved sign-in.",
                        response.RefreshToken ?? refreshToken, clientId);
                }
            }
            return Required("Silent sign-in is unavailable. Open the Xbox app and sign in, or use Sign in here.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && generation == Volatile.Read(ref _generation))
        {
            return new(AuthenticationStatus.ServiceFailure, null, "Sign-in timed out. Check your connection and try again.");
        }
        catch (Exception exception) when (IsServiceException(exception))
        {
            CheckGeneration(generation, cancellationToken);
            return Failure(exception);
        }
        finally { _signInGate.Release(); }
    }

    public async Task<AuthenticationResult> SignInInteractivelyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clientId = options.ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
            return new(AuthenticationStatus.ServiceFailure, null, "The built-in Microsoft sign-in configuration is unavailable.");
        var scope = options.Scope;
        var generation = BeginAttempt();
        await _signInGate.WaitAsync(cancellationToken);
        var confirmed = false;
        try
        {
            CheckGeneration(generation, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = scope })
            };
            using var response = await httpClient.SendAsync(request, cancellationToken);
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = data.RootElement;
            CheckGeneration(generation, cancellationToken);
            if (ReadString(root, "error") is { } error) return OAuthFailure(error);
            response.EnsureSuccessStatusCode();
            var deviceCode = root.GetProperty("device_code").GetString() ?? throw new FormatException();
            var verificationUri = new Uri(root.GetProperty("verification_uri").GetString() ?? throw new FormatException());
            if (!verificationUri.IsAbsoluteUri || verificationUri.Scheme != Uri.UriSchemeHttps || !verificationUri.IsDefaultPort ||
                verificationUri.UserInfo.Length != 0 || !IsMicrosoftHost(verificationUri.Host)) throw new FormatException();
            var lifetime = Math.Clamp(root.GetProperty("expires_in").GetInt32(), 1, 1800);
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetime);
            var interval = root.TryGetProperty("interval", out var intervalValue) ? Math.Clamp(intervalValue.GetInt32(), 1, 60) : 5;
            var prompt = new DeviceCodePrompt(root.GetProperty("user_code").GetString() ?? throw new FormatException(), verificationUri, expiresAt);
            var result = await desktop.WithDeviceCodeDialogAsync(prompt, async dialogToken =>
            {
                while (DateTimeOffset.UtcNow < expiresAt)
                {
                    await Task.Delay(TimeSpan.FromSeconds(interval), dialogToken);
                    CheckGeneration(generation, dialogToken);
                    var token = await RequestTokenAsync(new Dictionary<string, string>
                    {
                        ["client_id"] = clientId,
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = deviceCode
                    }, dialogToken);
                    CheckGeneration(generation, dialogToken);
                    if (token.Error == "authorization_pending") continue;
                    if (token.Error == "slow_down") { interval = Math.Min(interval + 5, 60); continue; }
                    if (token.Error is not null) return OAuthFailure(token.Error);
                    if (string.IsNullOrWhiteSpace(token.AccessToken))
                        return new(AuthenticationStatus.ServiceFailure, null, "Microsoft did not return an access token. Start Sign in again.");
                    var session = await ExchangeMicrosoftTokenAsync(token.AccessToken, dialogToken);
                    return Commit(session, generation, dialogToken, "Connected using the Microsoft account you selected.", token.RefreshToken, clientId);
                }
                return Required("The sign-in code expired. Start Sign in again.");
            }, cancellationToken);
            CheckGeneration(generation, cancellationToken);
            confirmed = result.IsSuccess;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && generation == Volatile.Read(ref _generation))
        {
            return new(AuthenticationStatus.ServiceFailure, null, "Sign-in timed out. Check your connection and try again.");
        }
        catch (Exception exception) when (IsServiceException(exception))
        {
            CheckGeneration(generation, cancellationToken);
            return Failure(exception);
        }
        finally
        {
            if (!confirmed || cancellationToken.IsCancellationRequested)
            {
                lock (_sessionGate)
                    if (generation == _generation) Volatile.Write(ref _session, null);
            }
            _signInGate.Release();
        }
    }

    public void SignOut()
    {
        lock (_sessionGate)
        {
            ++_generation;
            Volatile.Write(ref _session, null);
        }
    }

    private long BeginAttempt()
    {
        lock (_sessionGate)
        {
            Volatile.Write(ref _session, null);
            return ++_generation;
        }
    }

    private async Task<XboxSession> ExchangeMicrosoftTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var transport = CreateAuthenticationClient(cancellationToken);
        var client = new XboxAuthClient(transport);
        var user = await client.RequestUserToken(XboxAuthConstants.AzureTokenPrefix + accessToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(user.Token)) throw new FormatException();
        return await ExchangeAsync(user.Token, cancellationToken);
    }

    private async Task<XboxSession> ExchangeAsync(string userToken, CancellationToken cancellationToken)
    {
        using var transport = CreateAuthenticationClient(cancellationToken);
        var response = await new XboxAuthClient(transport).RequestXsts(new XboxXstsRequest
        { UserToken = userToken, RelyingParty = XboxAuthConstants.XboxLiveRelyingParty });
        cancellationToken.ThrowIfCancellationRequested();
        var claims = response.XuiClaims;
        if (string.IsNullOrWhiteSpace(response.Token) || string.IsNullOrWhiteSpace(claims?.UserHash) ||
            !ulong.TryParse(claims.XboxUserId, NumberStyles.None, CultureInfo.InvariantCulture, out var xuid) || xuid == 0 ||
            !DateTimeOffset.TryParse(response.ExpireOn, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires) || expires <= DateTimeOffset.UtcNow)
            throw new FormatException("Xbox authentication did not return a valid identity.");
        var profile = new XboxProfile(claims.XboxUserId!, claims.UniqueModernGamertag ?? claims.ModernGamertag ?? claims.Gamertag ?? "Xbox profile");
        return new XboxSession(response.Token, claims.UserHash, profile, expires);
    }

    private HttpClient CreateAuthenticationClient(CancellationToken cancellationToken) =>
        new(new AuthenticationHttpHandler(httpClient, cancellationToken))
        { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 };

    private sealed class AuthenticationHttpHandler(HttpClient sharedClient, CancellationToken operationToken) : HttpMessageHandler
    {
        private HttpResponseMessage? _response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, cancellationToken);
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            { Content = request.Content, Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            try
            {
                var response = await sharedClient.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                try
                {
                    await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, linked.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false),
                            new JsonDocumentOptions { MaxDepth = 32 });
                        var root = document.RootElement;
                        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("DisplayClaims", out var claims) ||
                            claims.ValueKind != JsonValueKind.Object || !claims.TryGetProperty("xui", out var users) ||
                            users.ValueKind != JsonValueKind.Array || users.GetArrayLength() != 1 || users[0].ValueKind != JsonValueKind.Object)
                            throw new FormatException("Xbox authentication did not return one unambiguous identity.");
                    }
                    linked.Token.ThrowIfCancellationRequested();
                    _response = response;
                    return response;
                }
                catch (JsonException)
                {
                    response.Dispose();
                    throw new FormatException("Xbox authentication returned malformed data.");
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
            finally
            {
                forwarded.Content = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _response?.Dispose();
            base.Dispose(disposing);
        }
    }

    private async Task<OAuthTokenReply> RequestTokenAsync(Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://login.microsoftonline.com/consumers/oauth2/v2.0/token") { Content = new FormUrlEncodedContent(fields) };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = data.RootElement;
        var error = ReadString(root, "error");
        if (!response.IsSuccessStatusCode && error is null) response.EnsureSuccessStatusCode();
        return new(ReadString(root, "access_token"), ReadString(root, "refresh_token"), error);
    }

    private static async Task<string?> ReadReferenceRefreshTokenAsync(string? path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path)) return null;
        var bytes = new byte[16385];
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 16384) return null;
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken);
                if (read == 0) break;
                length += read;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (length is 0 or > 16384) return null;
            var token = new UTF8Encoding(false, true).GetString(bytes, 0, length).TrimStart('﻿').Trim();
            return token.Length == 0 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl) ? null : token;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
        finally { Array.Clear(bytes); }
    }

    private AuthenticationResult Commit(XboxSession session, long generation, CancellationToken cancellationToken, string message, string? refreshToken = null, string? clientId = null)
    {
        lock (_sessionGate)
        {
            CheckGeneration(generation, cancellationToken);
            if (!string.IsNullOrWhiteSpace(refreshToken) && clientId is not null)
                NativeCredentials.WriteRefreshToken(clientId, session.Profile.Xuid, refreshToken);
            Volatile.Write(ref _session, session);
            return new(AuthenticationStatus.Success, session.Profile, message);
        }
    }

    private void CheckGeneration(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _generation)) throw new OperationCanceledException("Sign-in was superseded.");
    }

    private static string? ReadXuidHint()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\XboxLive");
            var value = Convert.ToString(key?.GetValue("Xuid"), CultureInfo.InvariantCulture);
            return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var xuid) && xuid != 0
                ? xuid.ToString(CultureInfo.InvariantCulture) : null;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsMicrosoftHost(string host) => host == "microsoft.com" || host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase) || host == "login.microsoftonline.com" || host == "login.live.com";
    private static bool IsServiceException(Exception exception) => exception is HttpRequestException or XboxAuthException or JsonException or FormatException or Win32Exception or UnauthorizedAccessException or TimeoutException or KeyNotFoundException or InvalidOperationException;
    private static AuthenticationResult Required(string message) => new(AuthenticationStatus.InteractionRequired, null, message);
    private static AuthenticationResult OAuthFailure(string error) => error switch
    {
        "invalid_client" or "unauthorized_client" => new(AuthenticationStatus.ServiceFailure, null,
            "Microsoft rejected the built-in sign-in flow. The reference application's public client may no longer be permitted."),
        "invalid_scope" => new(AuthenticationStatus.ServiceFailure, null,
            "Microsoft rejected the Xbox access requested by the reference sign-in flow. Try again after updating XBsync."),
        "access_denied" or "authorization_declined" => Required("Microsoft sign-in was declined. Start Sign in again and accept the requested access."),
        "expired_token" or "code_expired" or "bad_verification_code" => Required("The Microsoft sign-in code expired or is invalid. Start Sign in again for a new code."),
        "invalid_grant" => Required("The saved sign-in or device code is no longer valid. Start Sign in again."),
        "temporarily_unavailable" or "server_error" => new(AuthenticationStatus.ServiceFailure, null,
            "Microsoft sign-in is temporarily unavailable. Try again shortly."),
        _ => new(AuthenticationStatus.ServiceFailure, null, "Microsoft could not complete sign-in. Try again.")
    };
    private static AuthenticationResult Failure(Exception exception) => new(AuthenticationStatus.ServiceFailure, null, exception is XboxAuthException xbox
        ? $"Xbox Live rejected sign-in (HTTP {xbox.StatusCode}). Check the Xbox app or try another account."
        : exception is Win32Exception ? "Windows credential access failed. Check your Windows account and try again." : "Sign-in failed because the service was unreachable or returned an unexpected response. Try again.");
    private static string? ReadString(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private sealed record OAuthTokenReply(string? AccessToken, string? RefreshToken, string? Error)
    {
        public override string ToString() => nameof(OAuthTokenReply);
    }
}

internal static class NativeCredentials
{
    private const int MaximumBlobSize = 5120;
    internal sealed class UserToken(string token, string? xuid, DateTimeOffset expiresAt)
    {
        internal string Token { get; } = token;
        internal string? Xuid { get; } = xuid;
        internal DateTimeOffset ExpiresAt { get; } = expiresAt;
    }
    internal sealed class RefreshTokenCache(string refreshToken, string xuid)
    {
        internal string RefreshToken { get; } = refreshToken;
        internal string Xuid { get; } = xuid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags; public uint Type; public nint TargetName; public nint Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize; public nint Blob; public uint Persist; public uint AttributeCount;
        public nint Attributes; public nint TargetAlias; public nint UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(string filter, uint flags, out uint count, out nint credentials);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint buffer);

    internal static IReadOnlyList<UserToken> ReadXboxTokens(DateTimeOffset now)
    {
        if (!CredEnumerate("Xbl|*", 0, out var count, out var credentials))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return [];
            throw new Win32Exception(error);
        }
        var tokens = new List<UserToken>();
        try
        {
            if (count > 1024) throw new FormatException();
            for (var index = 0; index < count; index++)
            {
                var credential = Marshal.PtrToStructure<Credential>(Marshal.ReadIntPtr(credentials, index * nint.Size));
                var target = Marshal.PtrToStringUni(credential.TargetName) ?? "";
                if (credential.Type != 1 || !target.Contains("Utoken", StringComparison.OrdinalIgnoreCase)) continue;
                var bytes = ReadBlob(credential);
                try
                {
                    var parsed = ParseUserToken(bytes, now);
                    if (parsed is not null && tokens.All(token => token.Token != parsed.Token)) tokens.Add(parsed);
                }
                finally { Array.Clear(bytes); }
            }
            return tokens;
        }
        finally { CredFree(credentials); }
    }

    internal static UserToken? ParseUserToken(byte[] bytes, DateTimeOffset now)
    {
        if (bytes.Length is 0 or > MaximumBlobSize) return null;
        foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UnicodeEncoding(false, true, true) })
        {
            try
            {
                var text = encoding.GetString(bytes).TrimEnd('\0').TrimStart('\uFEFF');
                if (text.EndsWith("}X", StringComparison.Ordinal)) text = text[..^1];
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (!root.TryGetProperty("TokenData", out var data) || data.ValueKind != JsonValueKind.Object) continue;
                if (!data.TryGetProperty("Token", out var tokenValue) || tokenValue.ValueKind != JsonValueKind.String) continue;
                var token = tokenValue.GetString();
                if (string.IsNullOrWhiteSpace(token) || token.Length > MaximumBlobSize || token.All(character => character == 'A')) continue;
                if (!data.TryGetProperty("NotAfter", out var expiryValue) || expiryValue.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(expiryValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiry) || expiry <= now.AddMinutes(1)) continue;
                string? xuid = null;
                if (data.TryGetProperty("DisplayClaims", out var claims))
                {
                    if (claims.ValueKind != JsonValueKind.Object) continue;
                    if (claims.TryGetProperty("xui", out var users))
                    {
                        if (users.ValueKind != JsonValueKind.Array || users.GetArrayLength() != 1 || users[0].ValueKind != JsonValueKind.Object) continue;
                        if (users[0].TryGetProperty("xid", out var xid))
                        {
                            if (xid.ValueKind != JsonValueKind.String) continue;
                            xuid = xid.GetString();
                        }
                    }
                }
                if (xuid is not null && (!ulong.TryParse(xuid, NumberStyles.None, CultureInfo.InvariantCulture, out var numericXuid) || numericXuid == 0)) continue;
                return new UserToken(token, xuid, expiry);
            }
            catch (Exception exception) when (exception is JsonException or DecoderFallbackException or InvalidOperationException) { }
        }
        return null;
    }

    internal static RefreshTokenCache? ReadRefreshToken(string clientId)
    {
        if (!CredRead("XBsync|OAuth|" + clientId, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var bytes = ReadBlob(Marshal.PtrToStructure<Credential>(pointer));
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                var refresh = root.GetProperty("refreshToken").GetString();
                var xuid = root.GetProperty("xuid").GetString();
                return !string.IsNullOrWhiteSpace(refresh) && ulong.TryParse(xuid, NumberStyles.None, CultureInfo.InvariantCulture, out var numericXuid) && numericXuid != 0
                    ? new RefreshTokenCache(refresh, numericXuid.ToString(CultureInfo.InvariantCulture)) : null;
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    internal static void WriteRefreshToken(string clientId, string xuid, string refreshToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { refreshToken, xuid });
        nint target = 0, userName = 0, blob = 0;
        try
        {
            if (bytes.Length > MaximumBlobSize) throw new FormatException();
            target = Marshal.StringToCoTaskMemUni("XBsync|OAuth|" + clientId);
            userName = Marshal.StringToCoTaskMemUni(xuid);
            blob = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = target, UserName = userName, BlobSize = (uint)bytes.Length, Blob = blob, Persist = 2 };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Array.Clear(bytes);
            if (blob != 0)
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                Marshal.FreeCoTaskMem(blob);
            }
            Marshal.FreeCoTaskMem(userName);
            Marshal.FreeCoTaskMem(target);
        }
    }

    private static byte[] ReadBlob(Credential credential)
    {
        if (credential.Blob == 0 || credential.BlobSize is 0 or > MaximumBlobSize) return [];
        var bytes = new byte[credential.BlobSize];
        Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
        return bytes;
    }
}
