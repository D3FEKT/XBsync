using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XBsync.Models;

namespace XBsync.Services;

public interface ICloudSaveService
{
    bool IsImplemented { get; }
    Task<IReadOnlyList<CloudSaveContainer>> GetContainersAsync(string xuid, GameTitle game, CancellationToken cancellationToken);
    Task<IReadOnlyList<CloudSaveFile>> GetFilesAsync(string xuid, GameTitle game, CloudSaveContainer container, CancellationToken cancellationToken);
    Task DownloadAsync(string xuid, GameTitle game, CloudSaveFile file, Stream destination,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken);
    // TODO: unverified, needs further development - the upload/replace transport stays disabled until it is verified end to end.
    // Task<ReplacementResult> ReplaceAsync(string xuid, GameTitle game, CloudSaveContainer container,
    //     CloudSaveFile file, Stream replacement, IProgress<TransferProgress>? progress, CancellationToken cancellationToken);
}

public sealed class CloudSaveService(HttpClient httpClient, XboxAuthenticationService authentication) : ICloudSaveService, IDisposable
{
    private const int MaximumJsonBytes = 8 * 1024 * 1024;
    private const int BlockSize = 4 * 1024 * 1024;
    private const int MaximumItems = 100_000;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public bool IsImplemented => true;

    public async Task<IReadOnlyList<CloudSaveContainer>> GetContainersAsync(string xuid, GameTitle game, CancellationToken cancellationToken)
    {
        using var query = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        query.CancelAfter(TimeSpan.FromMinutes(3));
        cancellationToken = query.Token;
        var session = GetSession(xuid, game, cancellationToken);
        try
        {
            var blobs = await GetMetadataAsync(session, game, cancellationToken).ConfigureAwait(false);
            return blobs.Select(blob => new CloudSaveContainer(blob.Path, blob.DisplayName ?? blob.Path, blob.ETag))
                .OrderBy(container => container.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (XboxServiceException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new XboxServiceException(
                "Xbox did not expose connected storage for this game (HTTP 404). Confirm this account has synced PC saves for the title, then Refresh saves.",
                statusCode: HttpStatusCode.NotFound);
        }
    }

    public async Task<IReadOnlyList<CloudSaveFile>> GetFilesAsync(string xuid, GameTitle game, CloudSaveContainer container,
        CancellationToken cancellationToken)
    {
        using var query = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        query.CancelAfter(TimeSpan.FromMinutes(3));
        cancellationToken = query.Token;
        var session = GetSession(xuid, game, cancellationToken);
        var snapshot = await GetSnapshotAsync(session, game, container.Id, cancellationToken).ConfigureAwait(false);
        var files = new CloudSaveFile[snapshot.Atoms.Count];
        await Parallel.ForEachAsync(snapshot.Atoms.Select((pair, index) => (pair, index)),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (entry, token) =>
            {
                var metadata = await GetAtomMetadataAsync(session, game, entry.pair.Value, token).ConfigureAwait(false);
                files[entry.index] = MakeFile(snapshot, entry.pair.Key, entry.pair.Value, metadata);
            }).ConfigureAwait(false);
        CheckSession(session, cancellationToken);
        return files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task DownloadAsync(string xuid, GameTitle game, CloudSaveFile file, Stream destination,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        if (!destination.CanWrite) throw new ArgumentException("The download destination must be writable.", nameof(destination));
        var session = GetSession(xuid, game, cancellationToken);
        var snapshot = await GetSnapshotAsync(session, game, file.ContainerId, cancellationToken).ConfigureAwait(false);
        var atom = RequireCurrentFile(snapshot, file);
        await CopyAtomAsync(session, game, atom, destination, file.ETag, progress, cancellationToken).ConfigureAwait(false);
    }

    // Unverified upload/replace transport: needs further development before it can be re-enabled.
    // TODO: unverified, needs further development.
    //public async Task<ReplacementResult> ReplaceAsync(string xuid, GameTitle game, CloudSaveContainer container,
        //CloudSaveFile file, Stream replacement, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    //{
        //if (!replacement.CanRead || !replacement.CanSeek)
            //throw new XboxServiceException("Choose a readable local file for replacement.");
        //var size = replacement.Length - replacement.Position;
        //if (size < 0 || size > int.MaxValue)
            //throw new XboxServiceException("The connected-storage upload protocol supports replacement files up to 2 GB.");
        //if (file.ContainerId != container.Id) throw Changed();
        //var session = GetSession(xuid, game, cancellationToken);
        //await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        //string? backupPath = null;
        //var commitStarted = false;
        //try
        //{
            //var before = await GetSnapshotAsync(session, game, container.Id, cancellationToken).ConfigureAwait(false);
            //var atom = RequireCurrentFile(before, file);
            //var atomId = AtomId(atom);
            //if (before.Atoms.Values.Count(value => AtomId(value) == atomId) != 1)
                //throw new XboxServiceException("This storage atom is shared by multiple files. Replacement is unavailable because it could alter another file.");
            //var originalMetadata = await GetAtomMetadataAsync(session, game, atom, cancellationToken).ConfigureAwait(false);
            //if (file.ETag is not null && file.ETag != originalMetadata.ETag) throw Changed();
            //if (originalMetadata.ETag is null || originalMetadata.ETag.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
                //throw new XboxServiceException("Xbox did not provide a strong file version tag. This file can be downloaded, but replacement is unavailable because concurrent cloud changes could be overwritten. Refresh saves to retry.");
            //await RequireExclusiveAtomAsync(session, game, before, atomId, cancellationToken).ConfigureAwait(false);
//
            //var backupDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                //"XBsync", "Backups", Guid.NewGuid().ToString("N"));
            //Directory.CreateDirectory(backupDirectory);
            //var originalPath = Path.Combine(backupDirectory, SafeFileName(file.Name));
            //var partialPath = originalPath + ".partial";
            //try
            //{
                //await using (var backup = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    //await CopyAtomAsync(session, game, atom, backup, originalMetadata.ETag,
                        //null, cancellationToken).ConfigureAwait(false);
                //File.Move(partialPath, originalPath);
                //backupPath = originalPath;
            //}
            //finally
            //{
                //if (File.Exists(partialPath)) File.Delete(partialPath);
            //}
//
            //var allocationBody = JsonSerializer.Serialize(new { size = (int)size });
            //using var allocation = await RequestJsonAsync(session, game, "atoms/" + atomId, HttpMethod.Post,
                //allocationBody, originalMetadata.ETag, cancellationToken).ConfigureAwait(false);
            //var blobUri = RequireString(allocation.RootElement, "blobUri");
            //var uploadUri = ValidateUploadUri(blobUri);
            //var blocks = new List<string>();
            //var firstBlockId = RandomNumberGenerator.GetInt32(int.MaxValue - 512);
            //var buffer = new byte[BlockSize];
            //var remaining = size;
            //var transferred = 0L;
            //using var uploadedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            //progress?.Report(new TransferProgress(0, size));
            //while (remaining > 0)
            //{
                //CheckSession(session, cancellationToken);
                //var count = (int)Math.Min(buffer.Length, remaining);
                //await replacement.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                //var blockNumber = new byte[4];
                //BinaryPrimitives.WriteInt32LittleEndian(blockNumber, firstBlockId + blocks.Count);
                //var blockId = Convert.ToBase64String(blockNumber);
                //var blockUri = new Uri(uploadUri.GetLeftPart(UriPartial.Path) + "?comp=block&blockId=" +
                    //Uri.EscapeDataString(blockId) + "&" + uploadUri.Query.TrimStart('?'));
                //using var request = new HttpRequestMessage(HttpMethod.Put, blockUri);
                //request.Headers.Add("x-ms-blob-type", "BlockBlob");
                //request.Content = new ByteArrayContent(buffer, 0, count);
                //using var response = await SendAsync(request, session, cancellationToken).ConfigureAwait(false);
                //uploadedHash.AppendData(buffer, 0, count);
                //blocks.Add(blockId);
                //remaining -= count;
                //transferred += count;
                //progress?.Report(new TransferProgress(transferred, size));
            //}
            //if (replacement.Position != replacement.Length)
                //throw new XboxServiceException("The local replacement file changed while it was being uploaded. Retry with a stable copy.");
//
            //var latest = await GetSnapshotAsync(session, game, container.Id, cancellationToken).ConfigureAwait(false);
            //RequireUnchangedSnapshot(before, latest);
            //await RequireExclusiveAtomAsync(session, game, latest, atomId, cancellationToken).ConfigureAwait(false);
            //var currentMetadata = await GetAtomMetadataAsync(session, game, atom, cancellationToken).ConfigureAwait(false);
            //if (currentMetadata.ETag != originalMetadata.ETag) throw Changed();
            //CheckSession(session, cancellationToken);
            //var commitBody = JsonSerializer.Serialize(new { BlockIds = blocks, Size = (int)size });
            //using (var request = CreateRequest(session, game, "atoms/" + atomId + "?commit=true", HttpMethod.Post,
                //commitBody, currentMetadata.ETag))
            //{
                //commitStarted = true;
                //using var response = await SendAsync(request, session, cancellationToken).ConfigureAwait(false);
            //}
//
            //var actualHash = await CopyAtomAsync(session, game, atom, Stream.Null, null, null, cancellationToken).ConfigureAwait(false);
            //if (!CryptographicOperations.FixedTimeEquals(uploadedHash.GetHashAndReset(), actualHash))
                //throw new XboxServiceException("The committed cloud bytes differ from the uploaded file. The replacement cannot be reported as successful.");
            //var after = await GetSnapshotAsync(session, game, container.Id, cancellationToken).ConfigureAwait(false);
            //if (!SameAtoms(before.Atoms, after.Atoms))
                //throw new XboxServiceException("The container's file references changed during replacement. Refresh and review the save before continuing.");
            //var finalMetadata = await GetAtomMetadataAsync(session, game, atom, cancellationToken).ConfigureAwait(false);
            //CheckSession(session, cancellationToken);
            //return new ReplacementResult(MakeFile(after, file.Id, atom, finalMetadata) with { Size = size }, true)
                //{ BackupPath = backupPath };
        //}
        //catch (OperationCanceledException) { throw; }
        //catch (XboxServiceException exception) when (backupPath is not null)
        //{
            //throw new XboxServiceException(exception.Message + (commitStarted ?
                //" The commit may have reached the service; refresh before retrying." : " No file commit was requested.") +
                //" Original backup: " + backupPath, exception.IsPermissionDenied, exception.StatusCode);
        //}
        //catch (Exception exception) when (exception is IOException or HttpRequestException)
        //{
            //throw new XboxServiceException("Replacement could not complete. " + (commitStarted ?
                //"The commit may have reached the service; refresh before retrying. " : "No file commit was requested. ") +
                //(backupPath is null ? "Check your connection and local disk space." : "Original backup: " + backupPath));
        //}
        //finally { _writeGate.Release(); }
    //}

    private async Task<List<BlobMetadata>> GetMetadataAsync(XboxSession session, GameTitle game, CancellationToken cancellationToken)
    {
        var blobs = new List<BlobMetadata>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var continuations = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;
        do
        {
            var suffix = continuation is null ? "" : "?continuationToken=" + Uri.EscapeDataString(continuation);
            using var document = await RequestJsonAsync(session, game, suffix, HttpMethod.Get, null, null,
                cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!TryProperty(root, "blobs", out var entries) || entries.ValueKind != JsonValueKind.Array) throw InvalidResponse();
            foreach (var item in entries.EnumerateArray())
            {
                var path = RequireString(item, "fileName");
                ValidatePath(path);
                if (!paths.Add(path) || blobs.Count >= MaximumItems) throw InvalidResponse();
                var modified = OptionalString(item, "clientFileTime");
                DateTimeOffset? time = DateTimeOffset.TryParse(modified, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
                blobs.Add(new BlobMetadata(path, OptionalString(item, "displayName"), OptionalString(item, "eTag"), time));
            }
            continuation = TryProperty(root, "pagingInfo", out var paging) && paging.ValueKind != JsonValueKind.Null
                ? OptionalString(paging, "continuationToken") : null;
            if (continuation is not null && (continuation.Length > 4096 || continuations.Count >= 1000 || !continuations.Add(continuation)))
                throw new XboxServiceException("Xbox storage returned repeated or excessive pages. Refresh the save list.");
        } while (continuation is not null);
        CheckSession(session, cancellationToken);
        return blobs;
    }

    private async Task<ContainerSnapshot> GetSnapshotAsync(XboxSession session, GameTitle game, string path, CancellationToken cancellationToken)
    {
        ValidatePath(path);
        var metadata = (await GetMetadataAsync(session, game, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Path == path) ?? throw Changed();
        return await GetSnapshotAsync(session, game, metadata, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ContainerSnapshot> GetSnapshotAsync(XboxSession session, GameTitle game, BlobMetadata metadata,
        CancellationToken cancellationToken)
    {
        using var document = await RequestJsonAsync(session, game, EncodePath(metadata.Path), HttpMethod.Get, null, null,
            cancellationToken).ConfigureAwait(false);
        if (!TryProperty(document.RootElement, "atoms", out var atoms) || atoms.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var atom in atoms.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(atom.Name) || atom.Name.Length > 4096 || atom.Value.ValueKind != JsonValueKind.String) throw InvalidResponse();
            var reference = atom.Value.GetString()!;
            _ = AtomId(reference);
            if (references.Count >= MaximumItems || !references.TryAdd(atom.Name, reference)) throw InvalidResponse();
        }
        CheckSession(session, cancellationToken);
        return new ContainerSnapshot(metadata, references);
    }

    //private async Task RequireExclusiveAtomAsync(XboxSession session, GameTitle game, ContainerSnapshot selected,
        //string atomId, CancellationToken cancellationToken)
    //{
        //var containers = await GetMetadataAsync(session, game, cancellationToken).ConfigureAwait(false);
        //var current = containers.SingleOrDefault(container => container.Path == selected.Metadata.Path) ?? throw Changed();
        //if (current.ETag != selected.Metadata.ETag) throw Changed();
        //foreach (var container in containers.Where(container => container.Path != selected.Metadata.Path))
        //{
            //var snapshot = await GetSnapshotAsync(session, game, container, cancellationToken).ConfigureAwait(false);
            //if (snapshot.Atoms.Values.Any(reference => AtomId(reference) == atomId))
                //throw new XboxServiceException("This storage atom is shared with another save container. Replacement is unavailable because it could alter another save.");
        //}
    //}

    private async Task<AtomMetadata> GetAtomMetadataAsync(XboxSession session, GameTitle game, string atom, CancellationToken cancellationToken)
    {
        _ = AtomId(atom);
        using var request = CreateRequest(session, game, Uri.EscapeDataString(atom), HttpMethod.Head, null, null);
        using var response = await SendAsync(request, session, cancellationToken, allowUnsupportedHead: true).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is { } size)
            return new AtomMetadata(size, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
        using var fallback = CreateRequest(session, game, Uri.EscapeDataString(atom), HttpMethod.Get, null, null);
        fallback.Headers.Accept.Clear();
        fallback.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var headers = await SendAsync(fallback, session, cancellationToken).ConfigureAwait(false);
        return new AtomMetadata(headers.Content.Headers.ContentLength ?? -1, headers.Headers.ETag?.ToString(),
            headers.Content.Headers.LastModified);
    }

    private async Task<byte[]> CopyAtomAsync(XboxSession session, GameTitle game, string atom, Stream destination,
        string? etag, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        _ = AtomId(atom);
        using var request = CreateRequest(session, game, Uri.EscapeDataString(atom), HttpMethod.Get, null, etag);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await SendAsync(request, session, cancellationToken).ConfigureAwait(false);
        if (etag is not null && response.Headers.ETag is { } actual && actual.ToString() != etag) throw Changed();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            var total = response.Content.Headers.ContentLength;
            long copied = 0;
            progress?.Report(new TransferProgress(0, total));
            while (true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                var count = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                CheckSession(session, cancellationToken);
                if (count == 0) break;
                copied += count;
                if (total is { } length && copied > length) throw InvalidResponse();
                hash.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), idle.Token).ConfigureAwait(false);
                progress?.Report(new TransferProgress(copied, total));
            }
            if (total is { } expected && copied != expected)
                throw new XboxServiceException("The cloud download ended before all file bytes arrived. Retry the download.");
            CheckSession(session, cancellationToken);
            return hash.GetHashAndReset();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CheckSession(session, cancellationToken);
            throw new XboxServiceException("The cloud file transfer stalled. Check your connection and retry.");
        }
        catch (HttpRequestException)
        {
            CheckSession(session, cancellationToken);
            throw new XboxServiceException("The cloud download was interrupted. Check your connection and retry.");
        }
    }

    private async Task<JsonDocument> RequestJsonAsync(XboxSession session, GameTitle game, string suffix,
        HttpMethod method, string? body, string? etag, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(session, game, suffix, method, body, etag);
        using var response = await SendAsync(request, session, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is > MaximumJsonBytes) throw InvalidResponse();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var payload = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                CheckSession(session, cancellationToken);
                if (payload.Length + count > MaximumJsonBytes) throw InvalidResponse();
                payload.Write(buffer, 0, count);
            }
            CheckSession(session, cancellationToken);
            return JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException) { throw InvalidResponse(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CheckSession(session, cancellationToken);
            throw new XboxServiceException("Xbox storage did not respond in time. Try again.");
        }
    }

    private HttpRequestMessage CreateRequest(XboxSession session, GameTitle game, string suffix,
        HttpMethod method, string? body, string? etag)
    {
        var root = "https://titlestorage.xboxlive.com/connectedstorage/users/xuid(" + session.Profile.Xuid +
            ")/scids/" + Uri.EscapeDataString(game.ServiceConfigurationId!);
        var uri = new Uri(root + (suffix.StartsWith('?') || suffix.Length == 0 ? suffix : "/" + suffix));
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("XBL3.0", $"x={session.UserHash};{session.Token}");
        request.Headers.Add("x-xbl-contract-version", "107");
        if (!string.IsNullOrWhiteSpace(game.PackageFamilyName)) request.Headers.Add("x-xbl-pfn", game.PackageFamilyName);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Add("Signature", Sign(uri, body));
        }
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, XboxSession session,
        CancellationToken cancellationToken, bool allowUnsupportedHead = false)
    {
        CheckSession(session, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        HttpResponseMessage response;
        try { response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CheckSession(session, cancellationToken);
            throw new XboxServiceException("Xbox storage did not respond in time. Refresh before retrying.");
        }
        catch (HttpRequestException)
        {
            CheckSession(session, cancellationToken);
            throw new XboxServiceException("Xbox storage could not be reached. Check your connection and retry.");
        }
        try
        {
            CheckSession(session, cancellationToken);
            if (response.RequestMessage?.RequestUri is { } actual && actual != request.RequestUri)
                throw new XboxServiceException("Xbox storage returned an unexpected response location.");
            if (!response.IsSuccessStatusCode && !(allowUnsupportedHead && response.StatusCode is
                HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)) throw HttpFailure(response.StatusCode);
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    private XboxSession GetSession(string xuid, GameTitle game, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = authentication.CurrentSession ?? throw new XboxServiceException("Sign in before browsing cloud saves.");
        CheckSession(session, cancellationToken);
        if (!ulong.TryParse(xuid, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0 || xuid != session.Profile.Xuid)
            throw new XboxServiceException("The selected profile does not match the authenticated Xbox account. Sign in again.");
        if (!game.HasStorageIdentity || game.PackageFamilyName is { } pfn &&
            (pfn.Length > 256 || pfn.Any(char.IsControl)))
            throw new XboxServiceException("This title has no valid connected-storage identity. It may not support Xbox PC cloud saves.");
        return session;
    }

    private void CheckSession(XboxSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(authentication.CurrentSession, session))
            throw new OperationCanceledException("The Xbox account changed during this request.", cancellationToken);
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new XboxServiceException("Your Xbox sign-in expired. Sign in again before continuing.", true);
    }

    private string Sign(Uri uri, string body)
    {
        var timestamp = checked((ulong)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 11644473600L) * 10000000UL);
        var text = Encoding.ASCII.GetBytes("POST\0" + uri.PathAndQuery + "\0\0" + body + "\0");
        var payload = new byte[14 + text.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload, 1);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(5), timestamp);
        text.CopyTo(payload, 14);
        var signature = _signingKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var header = new byte[12 + signature.Length];
        BinaryPrimitives.WriteInt32BigEndian(header, 1);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(4), timestamp);
        signature.CopyTo(header, 12);
        return Convert.ToBase64String(header);
    }

    private static CloudSaveFile MakeFile(ContainerSnapshot snapshot, string name, string atom, AtomMetadata metadata)
    {
        var type = atom.IndexOf(',') is var index && index >= 0 ? atom[(index + 1)..] : "Save data";
        return new CloudSaveFile(snapshot.Metadata.Path, name, name, metadata.Size, metadata.Modified ?? snapshot.Metadata.Modified,
            metadata.ETag, type) { AtomReference = atom, ContainerETag = snapshot.Metadata.ETag };
    }

    private static string RequireCurrentFile(ContainerSnapshot snapshot, CloudSaveFile file)
    {
        if (file.ContainerId != snapshot.Metadata.Path || !snapshot.Atoms.TryGetValue(file.Id, out var atom) ||
            file.AtomReference is null || file.AtomReference != atom ||
            (file.ContainerETag is not null && file.ContainerETag != snapshot.Metadata.ETag)) throw Changed();
        return atom;
    }

    private static void RequireUnchangedSnapshot(ContainerSnapshot before, ContainerSnapshot after)
    {
        if (before.Metadata.ETag != after.Metadata.ETag || !SameAtoms(before.Atoms, after.Atoms)) throw Changed();
    }

    private static bool SameAtoms(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static string AtomId(string reference)
    {
        if (reference.Length > 1024 || reference.Any(char.IsControl) || reference.Contains('/') || reference.Contains('\\')) throw InvalidResponse();
        var id = reference.Split(',')[0];
        if (!Guid.TryParse(id, out var parsed)) throw InvalidResponse();
        return parsed.ToString("D");
    }

    private static Uri ValidateUploadUri(string value)
    {
        if (value.Length > 16384 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length == 0 ||
            !(uri.IdnHost.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase) ||
              uri.IdnHost.EndsWith(".blob.storage.azure.net", StringComparison.OrdinalIgnoreCase)))
            throw new XboxServiceException("Xbox storage returned an unexpected upload location. No file commit was requested.");
        return uri;
    }

    private static void ValidatePath(string path)
    {
        if (path.Length > 4096 || string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || path.Contains('\\') ||
            path.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..")) throw InvalidResponse();
    }

    private static string EncodePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static string SafeFileName(string name)
    {
        var result = new string(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
            .Take(120).ToArray()).TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(result) || result is "." or "..") return "original.save";
        var stem = result.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(stem[3]))) result = "_" + result;
        return result;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw InvalidResponse();
        return string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString();
    }

    private static string RequireString(JsonElement element, string name) => OptionalString(element, name) ?? throw InvalidResponse();
    private static XboxServiceException InvalidResponse() => new("Xbox storage returned malformed or unsupported save metadata. Refresh and try again.");
    private static XboxServiceException Changed() => new("The cloud save changed since it was listed. Refresh the files before downloading.");
    private static XboxServiceException HttpFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new XboxServiceException(
            $"Xbox denied this storage request (HTTP {(int)status}). Sign in again. If a write is still denied, this account/client is not permitted to write cloud saves for this game.", true, status),
        HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict => new XboxServiceException(
            "Xbox reported a conflicting save update. Close the game and refresh before retrying.", statusCode: status),
        HttpStatusCode.NotFound => new XboxServiceException("The selected cloud container or file no longer exists. Refresh the save list.", statusCode: status),
        HttpStatusCode.TooManyRequests => new XboxServiceException("Xbox storage is throttling requests. Wait briefly before refreshing.", statusCode: status),
        _ => new XboxServiceException($"Xbox storage could not complete the request (HTTP {(int)status}). Refresh and try again later.", statusCode: status)
    };

    public void Dispose() { _signingKey.Dispose(); _writeGate.Dispose(); }
    private sealed record BlobMetadata(string Path, string? DisplayName, string? ETag, DateTimeOffset? Modified);
    private sealed record ContainerSnapshot(BlobMetadata Metadata, Dictionary<string, string> Atoms);
    private sealed record AtomMetadata(long Size, string? ETag, DateTimeOffset? Modified = null);
}

