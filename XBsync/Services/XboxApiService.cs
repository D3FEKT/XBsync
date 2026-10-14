using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using XBsync.Models;

namespace XBsync.Services;

public interface IXboxApiService
{
    Task<XboxProfile> GetProfileAsync(string xuid, CancellationToken cancellationToken);
    Task<IReadOnlyList<GameTitle>> GetPlayedGamesAsync(string xuid, CancellationToken cancellationToken);
    Task<GameTitle> ResolveStorageIdentityAsync(string xuid, GameTitle game, CancellationToken cancellationToken);
}

public sealed class XboxApiService(HttpClient httpClient, XboxAuthenticationService authentication) : IXboxApiService
{
    private const int MaximumResponseBytes = 32 * 1024 * 1024;
    private const int MaximumPages = 50;
    private const int MaximumTitles = 10_000;
    private const int MaximumAchievementPageSize = 1_000;
    private const int MaximumAchievementPages = 40;
    private const int MaximumAchievements = 40_000;
    private const string HistoryDecoration = "scid";
    private const int ArtworkConcurrency = 8;
    private const int ArtworkBudgetSeconds = 90;
    private const int ThrottleRetries = 2;

    public async Task<XboxProfile> GetProfileAsync(string xuid, CancellationToken cancellationToken)
    {
        var session = GetSession(cancellationToken);
        var target = RequireXuid(xuid);
        var uri = new Uri($"https://profile.xboxlive.com/users/xuid({target})/profile/settings?settings=Gamertag,GameDisplayPicRaw");
        using var document = await GetJsonAsync(uri, session, cancellationToken).ConfigureAwait(false);
        var users = RequireArray(document.RootElement, "profileUsers");
        if (users.GetArrayLength() != 1 || ReadIdentifier(users[0], "id") != target)
            throw new XboxServiceException("Xbox Live returned profile information for a different account. Check the XUID and try again.");

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var setting in RequireArray(users[0], "settings").EnumerateArray())
        {
            var id = RequireString(setting, "id");
            if (id is not ("Gamertag" or "GameDisplayPicRaw")) continue;
            if (!settings.TryAdd(id, RequireString(setting, "value", allowEmpty: true)))
                throw InvalidResponse();
        }

        if (!settings.TryGetValue("Gamertag", out var gamertag) || string.IsNullOrWhiteSpace(gamertag))
            throw InvalidResponse();
        settings.TryGetValue("GameDisplayPicRaw", out var picture);
        CheckSession(session, cancellationToken);
        return new XboxProfile(target, gamertag, ReadImageUri(picture));
    }

    public async Task<IReadOnlyList<GameTitle>> GetPlayedGamesAsync(string xuid, CancellationToken cancellationToken)
    {
        var session = GetSession(cancellationToken);
        var target = RequireXuid(xuid);
        var titles = await GetTitleHistoryAsync(session, target, cancellationToken).ConfigureAwait(false);
        if (titles.Count == 0)
            titles = await EnrichTitlesAsync(session, target,
                await GetAchievementTitlesAsync(session, target, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return titles;
    }

    private async Task<IReadOnlyList<GameTitle>> GetTitleHistoryAsync(XboxSession session, string target,
        CancellationToken cancellationToken)
    {
        var firstPage = new Uri($"https://titlehub.xboxlive.com/users/xuid({target})/titles/titlehistory/decoration/{HistoryDecoration}?maxItems={MaximumTitles}");
        Uri? nextPage = firstPage;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var titles = new List<GameTitle>();
        var identities = new HashSet<(string TitleId, string? Scid, string? Pfn)>();
        var titleCount = 0;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            while (nextPage is not null)
            {
                CheckSession(session, operation.Token);
                ValidateEndpoint(nextPage, firstPage);
                if (visited.Count >= MaximumPages || !visited.Add(nextPage.AbsoluteUri))
                    throw new XboxServiceException("Xbox Live returned repeated or excessive history pages. Refresh the game library.");

                using var document = await GetJsonAsync(nextPage, session, operation.Token).ConfigureAwait(false);
                var root = document.RootElement;
                var pageTitles = RequireArray(root, "titles");
                ValidateResponseAccount(root, target);

                foreach (var title in pageTitles.EnumerateArray())
                {
                    CheckSession(session, operation.Token);
                    if (++titleCount > MaximumTitles)
                        throw new XboxServiceException("Xbox Live returned more game history than XBsync can safely load.");
                    var type = OptionalString(title, "type");
                    if (type is not null && !type.Equals("Game", StringComparison.OrdinalIgnoreCase)) continue;
                    var titleId = ReadTitleId(title);
                    var name = RequireString(title, "name");
                    var scid = NormalizeScid(OptionalString(title, "serviceConfigId"));
                    var pfn = NormalizePfn(OptionalString(title, "pfn"));
                    DateTimeOffset? lastPlayed = null;
                    if (title.TryGetProperty("titleHistory", out var history) && history.ValueKind != JsonValueKind.Null)
                    {
                        if (DateTimeOffset.TryParse(OptionalString(history, "lastTimePlayed"),
                            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                            lastPlayed = parsed;
                    }
                    if (identities.Add((titleId, scid, pfn?.ToUpperInvariant())))
                        titles.Add(new GameTitle(titleId, name, scid, pfn,
                            ReadImageUri(OptionalString(title, "displayImage")), lastPlayed, HoursPlayed: null));
                }
                nextPage = GetNextPage(root, firstPage);
                if (nextPage is null && titleCount >= MaximumTitles)
                    throw new XboxServiceException("Xbox Live reached the game-history limit without a continuation. Complete history could not be verified.");
            }
            CheckSession(session, operation.Token);
            return titles.AsReadOnly();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CheckSession(session, cancellationToken);
            throw new XboxServiceException("Loading the game library timed out. Try again.");
        }
    }

    private async Task<IReadOnlyList<GameTitle>> GetAchievementTitlesAsync(XboxSession session, string target,
        CancellationToken cancellationToken)
    {
        var firstPage = new Uri($"https://achievements.xboxlive.com/users/xuid({target})/achievements?maxItems={MaximumAchievementPageSize}");
        Uri? nextPage = firstPage;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var titles = new List<GameTitle>();
        var identities = new HashSet<(string TitleId, string? Scid)>();
        var itemCount = 0;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            while (nextPage is not null)
            {
                CheckSession(session, operation.Token);
                ValidateEndpoint(nextPage, firstPage);
                if (visited.Count >= MaximumAchievementPages || !visited.Add(nextPage.AbsoluteUri))
                    throw new XboxServiceException("Xbox Live returned repeated or excessive achievement pages. Refresh the game library.");

                using var document = await GetJsonAsync(nextPage, session, operation.Token).ConfigureAwait(false);
                var root = document.RootElement;
                var achievements = RequireArray(root, "achievements");
                ValidateResponseAccount(root, target);
                foreach (var achievement in achievements.EnumerateArray())
                {
                    CheckSession(session, operation.Token);
                    if (++itemCount > MaximumAchievements)
                        throw new XboxServiceException("Xbox Live returned more achievement history than XBsync can safely load.");
                    var scid = NormalizeScid(OptionalString(achievement, "serviceConfigId"));
                    if (!achievement.TryGetProperty("titleAssociations", out var associations) ||
                        associations.ValueKind != JsonValueKind.Array) continue;
                    foreach (var association in associations.EnumerateArray())
                    {
                        CheckSession(session, operation.Token);
                        if (association.ValueKind != JsonValueKind.Object) continue;
                        var titleId = ReadIdentifier(association, "id");
                        if (!uint.TryParse(titleId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
                            throw InvalidResponse();
                        var name = RequireString(association, "name");
                        if (identities.Add((titleId, scid)))
                            titles.Add(new GameTitle(titleId, name, scid, null, ArtworkUri: null, LastPlayed: null, HoursPlayed: null));
                    }
                }
                nextPage = GetAchievementNextPage(root, firstPage);
            }
            CheckSession(session, operation.Token);
            return titles.AsReadOnly();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CheckSession(session, cancellationToken);
            throw new XboxServiceException("Loading the game library timed out. Try again.");
        }
    }

    private static Uri? GetAchievementNextPage(JsonElement root, Uri firstPage)
    {
        var continuation = (string?)null;
        if (root.TryGetProperty("pagingInfo", out var paging) && paging.ValueKind == JsonValueKind.Object)
            continuation = OptionalString(paging, "continuationToken");
        if (continuation is null) return null;
        if (continuation.Length > 4096) throw InvalidResponse();
        var separator = firstPage.Query.Length == 0 ? "?" : "&";
        return new Uri(firstPage.AbsoluteUri + separator + "continuationToken=" + Uri.EscapeDataString(continuation));
    }

    private sealed record TitleMetadata(string? Scid, string? Pfn, Uri? Artwork);

    // Achievement history lacks artwork and PFN, so each title is resolved individually from titlehub.
    private async Task<IReadOnlyList<GameTitle>> EnrichTitlesAsync(XboxSession session, string target,
        IReadOnlyList<GameTitle> titles, CancellationToken cancellationToken)
    {
        var wanted = titles.Where(title => title.ArtworkUri is null || title.PackageFamilyName is null)
            .Select(title => title.TitleId).Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0) return titles;

        var resolved = new Dictionary<string, TitleMetadata>(StringComparer.Ordinal);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(ArtworkBudgetSeconds));
        try
        {
            await Parallel.ForEachAsync(wanted,
                new ParallelOptions { MaxDegreeOfParallelism = ArtworkConcurrency, CancellationToken = operation.Token },
                async (titleId, token) =>
                {
                    TitleMetadata? metadata;
                    try { metadata = await GetTitleMetadataAsync(session, target, titleId, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { return; }
                    if (metadata is not null) lock (resolved) resolved[titleId] = metadata;
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CheckSession(session, cancellationToken);
        }
        if (resolved.Count == 0) return titles;

        var enriched = new GameTitle[titles.Count];
        for (var index = 0; index < titles.Count; index++)
        {
            var title = titles[index];
            enriched[index] = resolved.TryGetValue(title.TitleId, out var metadata)
                ? title with
                {
                    ServiceConfigurationId = title.ServiceConfigurationId ?? metadata.Scid,
                    PackageFamilyName = title.PackageFamilyName ?? metadata.Pfn,
                    ArtworkUri = title.ArtworkUri ?? metadata.Artwork
                }
                : title;
        }
        return enriched;
    }

    private async Task<TitleMetadata?> GetTitleMetadataAsync(XboxSession session, string target, string titleId,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://titlehub.xboxlive.com/users/xuid({target})/titles/titleid({titleId})/decoration/scid,image,detail");
        JsonDocument document;
        try
        {
            document = await GetJsonAsync(uri, session, cancellationToken).ConfigureAwait(false);
        }
        catch (XboxServiceException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            CheckSession(session, cancellationToken);
            return null;
        }
        using (document)
        {
            var root = document.RootElement;
            ValidateResponseAccount(root, target);
            foreach (var title in RequireArray(root, "titles").EnumerateArray())
            {
                CheckSession(session, cancellationToken);
                if (ReadTitleId(title) != titleId) continue;
                return new TitleMetadata(NormalizeScid(OptionalString(title, "serviceConfigId")),
                    NormalizePfn(OptionalString(title, "pfn")), ReadImageUri(OptionalString(title, "displayImage")));
            }
        }
        return null;
    }

    public async Task<GameTitle> ResolveStorageIdentityAsync(string xuid, GameTitle game, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        var session = GetSession(cancellationToken);
        var target = RequireXuid(xuid);
        if (!uint.TryParse(game.TitleId, NumberStyles.None, CultureInfo.InvariantCulture, out var titleId) || titleId == 0)
            throw new XboxServiceException("The Xbox title identity is invalid. Refresh the game library.");
        game = game with
        {
            TitleId = titleId.ToString(CultureInfo.InvariantCulture),
            ServiceConfigurationId = NormalizeScid(game.ServiceConfigurationId),
            PackageFamilyName = NormalizePfn(game.PackageFamilyName)
        };
        if (game.ServiceConfigurationId is not null && game.PackageFamilyName is not null)
        {
            CheckSession(session, cancellationToken);
            return game;
        }

        var monikers = new List<string>();
        if (game.PackageFamilyName is { } pfn) monikers.Add($"pfn({Uri.EscapeDataString(pfn)})");
        monikers.Add($"titleid({game.TitleId})");
        foreach (var moniker in monikers)
        {
            CheckSession(session, cancellationToken);
            var uri = new Uri($"https://titlehub.xboxlive.com/users/xuid({target})/titles/{moniker}/decoration/scid,image,detail");
            JsonDocument document;
            try
            {
                document = await GetJsonAsync(uri, session, cancellationToken).ConfigureAwait(false);
            }
            catch (XboxServiceException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                CheckSession(session, cancellationToken);
                continue;
            }
            using (document)
            {
                var root = document.RootElement;
                ValidateResponseAccount(root, target);
                var returnedTitles = RequireArray(root, "titles");
                if (returnedTitles.GetArrayLength() > MaximumTitles) throw InvalidResponse();
                var candidates = new HashSet<(string? Scid, string? Pfn)>();
                string? candidatePfn = null;
                var matchingTitle = false;
                foreach (var title in returnedTitles.EnumerateArray())
                {
                    CheckSession(session, cancellationToken);
                    if (ReadTitleId(title) != game.TitleId) continue;
                    matchingTitle = true;
                    var scid = NormalizeScid(OptionalString(title, "serviceConfigId"));
                    var returnedPfn = NormalizePfn(OptionalString(title, "pfn"));
                    if (game.ServiceConfigurationId is { } knownScid && scid != knownScid) continue;
                    if (game.PackageFamilyName is { } knownPfn &&
                        !string.Equals(returnedPfn, knownPfn, StringComparison.OrdinalIgnoreCase)) continue;
                    if (scid is null) continue;
                    candidates.Add((scid, returnedPfn?.ToUpperInvariant()));
                    candidatePfn = returnedPfn;
                }
                CheckSession(session, cancellationToken);
                if (returnedTitles.GetArrayLength() > 0 && !matchingTitle)
                    throw new XboxServiceException("Xbox Live returned metadata for an unexpected title. Refresh the game library.");
                if (candidates.Count == 1)
                {
                    var identity = candidates.Single();
                    return game with { ServiceConfigurationId = identity.Scid, PackageFamilyName = candidatePfn };
                }
            }
        }
        CheckSession(session, cancellationToken);
        return game;
    }

    private static void ValidateResponseAccount(JsonElement root, string xuid)
    {
        if (root.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        foreach (var name in new[] { "xuid", "xboxUserId" })
            if (root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null &&
                ReadIdentifier(root, name) != xuid)
                throw new XboxServiceException("Xbox Live returned game information for a different account. Check the XUID and try again.");
    }

    private static string RequireXuid(string xuid)
    {
        if (!ulong.TryParse(xuid, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value == 0)
            throw new XboxServiceException("Enter a valid numeric Xbox User ID (XUID).");
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string ReadTitleId(JsonElement title)
    {
        var value = ReadIdentifier(title, "titleId");
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var titleId) || titleId == 0)
            throw InvalidResponse();
        return value;
    }

    private static string? NormalizeScid(string? value) => Guid.TryParse(value, out var scid) && scid != Guid.Empty
        ? scid.ToString("D") : null;

    private static string? NormalizePfn(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 255 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_') ? value : null;

    private XboxSession GetSession(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = authentication.CurrentSession ??
            throw new XboxServiceException("Sign in to Xbox before loading your profile or games.");
        CheckSession(session, cancellationToken);
        if (!ulong.TryParse(session.Profile.Xuid, NumberStyles.None, CultureInfo.InvariantCulture, out var xuid) || xuid == 0)
            throw new XboxServiceException("The Xbox sign-in identity is invalid. Sign in again.");
        return session;
    }

    private void CheckSession(XboxSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(authentication.CurrentSession, session))
            throw new OperationCanceledException("The Xbox account changed while loading data. Retry the request.", cancellationToken);
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new XboxServiceException("The Xbox sign-in expired. Sign in again.", isPermissionDenied: true);
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, XboxSession session, CancellationToken cancellationToken, int retries = 0)
    {
        CheckSession(session, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var retryDelay = TimeSpan.Zero;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("XBL3.0", $"x={session.UserHash};{session.Token}");
            request.Headers.Add("x-xbl-contract-version", "2");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(
                string.IsNullOrEmpty(CultureInfo.CurrentUICulture.Name) ? "en-US" : CultureInfo.CurrentUICulture.Name));
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            CheckSession(session, cancellationToken);
            if (response.RequestMessage?.RequestUri is { } responseUri) ValidateEndpoint(responseUri, uri);
            if (!response.IsSuccessStatusCode)
            {
                retryDelay = RetryDelay(response);
                throw HttpFailure(response.StatusCode);
            }
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                throw new XboxServiceException("Xbox Live returned a response larger than XBsync can safely load.");

            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var payload = new MemoryStream();
            var buffer = new byte[81_920];
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
            {
                CheckSession(session, cancellationToken);
                if (payload.Length + count > MaximumResponseBytes)
                    throw new XboxServiceException("Xbox Live returned a response larger than XBsync can safely load.");
                payload.Write(buffer, 0, count);
            }
            CheckSession(session, timeout.Token);
            return JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (XboxServiceException exception) when (retries < ThrottleRetries && IsTransient(exception.StatusCode))
        {
            CheckSession(session, cancellationToken);
            await Task.Delay(retryDelay > TimeSpan.Zero ? retryDelay : TimeSpan.FromMilliseconds(500 * (1 << retries)),
                cancellationToken).ConfigureAwait(false);
            CheckSession(session, cancellationToken);
            return await GetJsonAsync(uri, session, cancellationToken, retries + 1).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CheckSession(session, cancellationToken);
            throw new XboxServiceException("Xbox Live did not respond in time. Try again.");
        }
        catch (HttpRequestException exception)
        {
            CheckSession(session, cancellationToken);
            throw exception.StatusCode is { } status ? HttpFailure(status) :
                new XboxServiceException("Xbox Live could not be reached. Check your connection and try again.");
        }
        catch (JsonException)
        {
            CheckSession(session, cancellationToken);
            throw InvalidResponse();
        }
        catch (Exception exception) when (exception is IOException or FormatException or InvalidOperationException)
        {
            CheckSession(session, cancellationToken);
            throw new XboxServiceException("The Xbox Live request could not be completed. Try again.");
        }
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero && delta <= TimeSpan.FromSeconds(30)) return delta;
        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero && wait <= TimeSpan.FromSeconds(30)) return wait;
        }
        return TimeSpan.Zero;
    }

    private static bool IsTransient(HttpStatusCode? status) => status is HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    private static Uri? GetNextPage(JsonElement root, Uri firstPage)
    {
        var nextLink = OptionalString(root, "nextLink");
        string? continuation = null;
        if (root.TryGetProperty("pagingInfo", out var paging) && paging.ValueKind != JsonValueKind.Null)
        {
            var pagingLink = OptionalString(paging, "nextLink");
            if (nextLink is not null && pagingLink is not null && nextLink != pagingLink) throw InvalidResponse();
            nextLink ??= pagingLink;
            continuation = OptionalString(paging, "continuationToken");
        }
        if (nextLink is not null)
        {
            if (nextLink.Length > 16_384 || !Uri.TryCreate(firstPage, nextLink, out var uri)) throw InvalidResponse();
            ValidateEndpoint(uri, firstPage);
            return uri;
        }
        if (continuation is null) return null;
        if (continuation.Length > 4096) throw InvalidResponse();
        var separator = firstPage.Query.Length == 0 ? "?" : "&";
        return new Uri(firstPage.AbsoluteUri + separator + "continuationToken=" + Uri.EscapeDataString(continuation));
    }

    private static void ValidateEndpoint(Uri candidate, Uri expected)
    {
        if (!candidate.IsAbsoluteUri || candidate.Scheme != Uri.UriSchemeHttps || candidate.Port != 443 ||
            !string.Equals(candidate.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            candidate.UserInfo.Length != 0 || candidate.Fragment.Length != 0 ||
            candidate.AbsolutePath != expected.AbsolutePath || candidate.AbsoluteUri.Length > 16_384)
            throw new XboxServiceException("Xbox Live returned an unexpected response location. Refresh and try again.");
    }

    private static JsonElement RequireArray(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
            throw InvalidResponse();
        return property;
    }

    private static string RequireString(JsonElement value, string name, bool allowEmpty = false)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            (!allowEmpty && string.IsNullOrWhiteSpace(property.GetString()))) throw InvalidResponse();
        return property.GetString()!;
    }

    private static string? OptionalString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) throw InvalidResponse();
        var text = property.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string ReadIdentifier(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property)) throw InvalidResponse();
        if (property.ValueKind == JsonValueKind.String &&
            ulong.TryParse(property.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            return parsed.ToString(CultureInfo.InvariantCulture);
        if (property.ValueKind == JsonValueKind.Number && property.TryGetUInt64(out parsed))
            return parsed.ToString(CultureInfo.InvariantCulture);
        throw InvalidResponse();
    }

    private static Uri? ReadImageUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.UserInfo.Length == 0 ? uri : null;

    private static XboxServiceException InvalidResponse() => new("Xbox Live returned an unexpected response. Refresh and try again.");

    private static XboxServiceException HttpFailure(HttpStatusCode status) => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        ? new XboxServiceException($"Xbox Live denied this request (HTTP {(int)status}). Sign in again or check the account's permissions.", true, status)
        : new XboxServiceException($"Xbox Live could not complete this request (HTTP {(int)status}). Try again later.", statusCode: status);
}

public sealed class XboxServiceException : Exception
{
    public bool IsPermissionDenied { get; }
    public HttpStatusCode? StatusCode { get; }

    public XboxServiceException(string message, bool isPermissionDenied = false, HttpStatusCode? statusCode = null) : base(message)
    {
        IsPermissionDenied = isPermissionDenied;
        StatusCode = statusCode;
    }
}
