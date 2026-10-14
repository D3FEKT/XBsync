using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using XBsync.Models;

namespace XBsync.Services;

public interface ICloudSaveService
{
    bool IsImplemented { get; }
    Task<IReadOnlyList<CloudSaveContainer>> GetContainersAsync(string xuid, GameTitle game, CancellationToken cancellationToken);
    Task<IReadOnlyList<CloudSaveFile>> GetFilesAsync(string xuid, GameTitle game, CloudSaveContainer container, CancellationToken cancellationToken);
    Task<CloudSaveBatch> GetAllFilesAsync(string xuid, GameTitle game, IReadOnlyCollection<string> containerIds,
        IProgress<DownloadAllProgress>? progress, CancellationToken cancellationToken);
    Task<PackedDownloadResult> DownloadPackedAsync(string xuid, GameTitle game, IReadOnlyList<CloudSaveContainer> containers,
        Stream destination, int concurrency, IProgress<TransferProgress>? progress, CancellationToken cancellationToken);
    Task<PackedExtractResult> ExtractPackedAsync(Stream packed, string destinationFolder, CancellationToken cancellationToken);
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
    private const int ListingConcurrency = 8;
    private const int MetadataConcurrency = 8;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public bool IsImplemented => true;

    public async Task<IReadOnlyList<CloudSaveContainer>> GetContainersAsync(string xuid, GameTitle game, CancellationToken cancellationToken)
    {
        using var query = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        query.CancelAfter(TimeSpan.FromMinutes(3));
        cancellationToken = query.Token;
        var target = GetSession(xuid, game, cancellationToken);
        try
        {
            var blobs = await GetMetadataAsync(target, game, cancellationToken).ConfigureAwait(false);
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
        var target = GetSession(xuid, game, cancellationToken);
        var snapshot = await GetSnapshotAsync(target, game, container.Id, cancellationToken).ConfigureAwait(false);
        // The mapping response carries each atom's name only; size and ETag still need one request per atom.
        return await BuildFilesAsync(target, game, snapshot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudSaveBatch> GetAllFilesAsync(string xuid, GameTitle game, IReadOnlyCollection<string> containerIds,
        IProgress<DownloadAllProgress>? progress, CancellationToken cancellationToken)
    {
        using var query = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        query.CancelAfter(TimeSpan.FromMinutes(3));
        cancellationToken = query.Token;
        var target = GetSession(xuid, game, cancellationToken);
        var wanted = containerIds.ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return new CloudSaveBatch([], []);
        var metadata = await GetMetadataAsync(target, game, cancellationToken).ConfigureAwait(false);
        var selected = metadata.Where(blob => wanted.Contains(blob.Path)).ToArray();
        var snapshots = new ContainerSnapshot?[selected.Length];
        var failures = new ContainerReadFailure?[selected.Length];
        var listed = 0;
        await Parallel.ForEachAsync(selected.Select((blob, index) => (blob, index)),
            new ParallelOptions { MaxDegreeOfParallelism = ListingConcurrency, CancellationToken = cancellationToken },
            async (entry, token) =>
            {
                var name = entry.blob.DisplayName ?? entry.blob.Path;
                try { snapshots[entry.index] = await GetSnapshotAsync(target, game, entry.blob, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    failures[entry.index] = new ContainerReadFailure(name,
                        exception is XboxServiceException service ? service.Message : exception.Message);
                }
                progress?.Report(new DownloadAllProgress(Interlocked.Increment(ref listed), selected.Length, name));
            }).ConfigureAwait(false);
        var pending = new List<CloudSaveFile>();
        foreach (var snapshot in snapshots.OfType<ContainerSnapshot>())
            pending.AddRange(await BuildFilesAsync(target, game, snapshot, cancellationToken).ConfigureAwait(false));
        CheckSession(target.Session, cancellationToken);
        return new CloudSaveBatch(pending, [.. failures.OfType<ContainerReadFailure>()]);
    }

    public async Task DownloadAsync(string xuid, GameTitle game, CloudSaveFile file, Stream destination,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        if (!destination.CanWrite) throw new ArgumentException("The download destination must be writable.", nameof(destination));
        var target = GetSession(xuid, game, cancellationToken);
        var snapshot = await GetSnapshotAsync(target, game, file.ContainerId, cancellationToken).ConfigureAwait(false);
        var atom = RequireCurrentFile(snapshot, file);
        await CopyAtomAsync(target, game, atom, destination, file.ETag, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PackedDownloadResult> DownloadPackedAsync(string xuid, GameTitle game,
        IReadOnlyList<CloudSaveContainer> containers, Stream destination, int concurrency,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        if (!destination.CanWrite) throw new ArgumentException("The download destination must be writable.", nameof(destination));
        if (concurrency < 1) throw new ArgumentOutOfRangeException(nameof(concurrency));
        using var query = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        query.CancelAfter(TimeSpan.FromMinutes(30));
        cancellationToken = query.Token;
        var target = GetSession(xuid, game, cancellationToken);
        var ordered = containers.OrderBy(container => container.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var metadata = await GetMetadataAsync(target, game, cancellationToken).ConfigureAwait(false);
        var entries = await PlanEntriesAsync(target, game, metadata, ordered, cancellationToken).ConfigureAwait(false);
        using var writeGate = new SemaphoreSlim(1, 1);
        var failures = new ConcurrentQueue<(string Name, Exception Error)>();
        long transferred = 0;
        var written = 0;
        progress?.Report(new TransferProgress(0, -1));
        var channel = Channel.CreateBounded<PackedEntry>(
            new BoundedChannelOptions(Math.Max(1, concurrency * 4)) { SingleWriter = true, SingleReader = false });
        async Task ReadAsync()
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var key = BitConverter.ToString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Path))).Replace("-", "");
                    var payload = new MemoryStream();
                    await CopyAtomAsync(target, game, item.Atom, payload, null, null, cancellationToken).ConfigureAwait(false);
                    if (payload.Length == 0) continue;
                    var header = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { name = item.Path, size = payload.Length }));
                    await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await destination.WriteAsync(Encoding.UTF8.GetBytes("XBSYNC1 " + key + " " + header.Length + "\n"), cancellationToken).ConfigureAwait(false);
                        await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                        payload.Position = 0;
                        await payload.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    }
                    finally { writeGate.Release(); }
                    Interlocked.Increment(ref written);
                    var done = Interlocked.Add(ref transferred, payload.Length);
                    progress?.Report(new TransferProgress(done, -1));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { failures.Enqueue((item.Name, exception)); }
            }
        }
        var readers = new Task[concurrency];
        for (var worker = 0; worker < concurrency; worker++) readers[worker] = Task.Run(ReadAsync, cancellationToken);
        try
        {
            foreach (var entry in entries)
                await channel.Writer.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
            channel.Writer.Complete();
            await Task.WhenAll(readers).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            channel.Writer.TryComplete();
            try { await Task.WhenAll(readers).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (failures.TryDequeue(out var failure))
            throw new XboxServiceException(failure.Error is XboxServiceException service
                ? service.Message : $"Could not pack {failure.Name}. Check your connection and retry.");
        cancellationToken.ThrowIfCancellationRequested();
        CheckSession(target.Session, cancellationToken);
        return new PackedDownloadResult(written, transferred);
    }

    public async Task<PackedExtractResult> ExtractPackedAsync(Stream packed, string destinationFolder, CancellationToken cancellationToken)
    {
        if (!packed.CanRead) throw new ArgumentException("The packed save must be readable.", nameof(packed));
        if (string.IsNullOrWhiteSpace(destinationFolder)) throw new ArgumentException("Choose a folder for the extracted saves.", nameof(destinationFolder));
        var root = Path.GetFullPath(destinationFolder);
        Directory.CreateDirectory(root);
        var header = new byte[64 * 1024];
        var body = new byte[1024 * 1024];
        var extracted = 0;
        var totalBytes = 0L;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (line, lineLength) = await ReadLineAsync(packed, cancellationToken).ConfigureAwait(false);
            if (lineLength == 0) break;
            var fields = line.Split(' ');
            if (fields.Length != 3 || fields[0] != "XBSYNC1" || fields[1].Length != 64)
                throw MalformedPack();
            if (!int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var headerLength) ||
                headerLength <= 0 || headerLength > header.Length)
                throw MalformedPack();
            await ReadExactlyAsync(packed, header.AsMemory(0, headerLength), cancellationToken).ConfigureAwait(false);
            string name;
            long size;
            using (var document = JsonDocument.Parse(header.AsMemory(0, headerLength)))
            {
                if (!TryProperty(document.RootElement, "name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String ||
                    !TryProperty(document.RootElement, "size", out var sizeElement) || !sizeElement.TryGetInt64(out size) || size < 0)
                    throw MalformedPack();
                name = nameElement.GetString()!;
            }
            var expected = BitConverter.ToString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).Replace("-", "");
            if (!expected.Equals(fields[1], StringComparison.Ordinal)) throw MalformedPack();
            var path = ExtractPath(root, name);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".xbsync-{Guid.NewGuid():N}.partial");
            try
            {
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var remaining = size;
                    while (remaining > 0)
                    {
                        var want = (int)Math.Min(remaining, body.Length);
                        await ReadExactlyAsync(packed, body.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                        await file.WriteAsync(body.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                        remaining -= want;
                    }
                    await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporary, UniquePath(path));
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            extracted++;
            totalBytes += size;
        }
        return new PackedExtractResult(extracted, totalBytes);
    }

    private static async Task<(string Line, int Length)> ReadLineAsync(Stream packed, CancellationToken token)
    {
        var buffer = new byte[1];
        var line = new StringBuilder();
        while (true)
        {
            var read = await packed.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0)
            {
                if (line.Length == 0) return (string.Empty, 0);
                throw MalformedPack();
            }
            if (buffer[0] == (byte)'\n') return (line.ToString(), line.Length + 1);
            if (line.Length >= 4096) throw MalformedPack();
            line.Append((char)buffer[0]);
        }
    }

    private static async Task ReadExactlyAsync(Stream packed, Memory<byte> destination, CancellationToken token)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await packed.ReadAsync(destination[read..], token).ConfigureAwait(false);
            if (count == 0) throw MalformedPack();
            read += count;
        }
    }

    private static string ExtractPath(string root, string name)
    {
        var segments = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) throw MalformedPack();
        var parts = new string[segments.Length];
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index].Trim();
            if (segment.Length == 0) throw MalformedPack();
            parts[index] = index == segments.Length - 1 ? SafeFileName(segment) : PackedSafeFolderName(segment);
        }
        var path = Path.GetFullPath(Path.Combine([root, .. parts]));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw MalformedPack();
        return path;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var suffix = 2; suffix < 100000; suffix++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw MalformedPack();
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

    private async Task<List<PackedEntry>> PlanEntriesAsync(CloudTarget target, GameTitle game,
        IReadOnlyList<BlobMetadata> metadata, IReadOnlyList<CloudSaveContainer> containers, CancellationToken cancellationToken)
    {
        var groups = new PackedEntry[]?[containers.Count];
        await Parallel.ForEachAsync(containers.Select((container, index) => (container, index)),
            new ParallelOptions { MaxDegreeOfParallelism = ListingConcurrency, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                var blob = metadata.SingleOrDefault(entry => entry.Path == item.container.Id) ?? throw Changed();
                var snapshot = (await GetSnapshotAsync(target, game, blob, token).ConfigureAwait(false)).Atoms;
                var folder = PackedSafeFolderName(item.container.Name);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var bucket = new List<PackedEntry>(snapshot.Count);
                foreach (var pair in snapshot.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var leaf = SafeFileName(pair.Key);
                    var stem = Path.GetFileNameWithoutExtension(leaf);
                    var extension = Path.GetExtension(leaf);
                    for (var suffix = 2; !used.Add(leaf); suffix++) leaf = $"{stem} ({suffix}){extension}";
                    bucket.Add(new PackedEntry(folder + "/" + leaf, pair.Value, item.container.Name));
                }
                groups[item.index] = [.. bucket];
            }).ConfigureAwait(false);
        return [.. groups.SelectMany(group => group ?? [])];
    }

    private async Task<List<BlobMetadata>> GetMetadataAsync(CloudTarget target, GameTitle game, CancellationToken cancellationToken)
    {
        var blobs = new List<BlobMetadata>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var continuations = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;
        do
        {
            var suffix = continuation is null ? "" : "?continuationToken=" + Uri.EscapeDataString(continuation);
            using var document = await RequestJsonAsync(target, game, suffix, HttpMethod.Get, null, null,
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
        CheckSession(target.Session, cancellationToken);
        return blobs;
    }

    private async Task<ContainerSnapshot> GetSnapshotAsync(CloudTarget target, GameTitle game, string path, CancellationToken cancellationToken)
    {
        ValidatePath(path);
        var metadata = (await GetMetadataAsync(target, game, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Path == path) ?? throw Changed();
        return await GetSnapshotAsync(target, game, metadata, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CloudSaveFile>> BuildFilesAsync(CloudTarget target, GameTitle game,
        ContainerSnapshot snapshot, CancellationToken cancellationToken)
    {
        var files = new CloudSaveFile[snapshot.Atoms.Count];
        await Parallel.ForEachAsync(snapshot.Atoms.Select((pair, index) => (pair, index)),
            new ParallelOptions { MaxDegreeOfParallelism = MetadataConcurrency, CancellationToken = cancellationToken },
            async (entry, token) =>
            {
                var metadata = await GetAtomMetadataAsync(target, game, entry.pair.Value, token).ConfigureAwait(false);
                files[entry.index] = MakeFile(snapshot, entry.pair.Key, entry.pair.Value, metadata);
            }).ConfigureAwait(false);
        CheckSession(target.Session, cancellationToken);
        return files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<ContainerSnapshot> GetSnapshotAsync(CloudTarget target, GameTitle game, BlobMetadata metadata,
        CancellationToken cancellationToken)
    {
        using var document = await RequestJsonAsync(target, game, EncodePath(metadata.Path), HttpMethod.Get, null, null,
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
        CheckSession(target.Session, cancellationToken);
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

    private async Task<AtomMetadata> GetAtomMetadataAsync(CloudTarget target, GameTitle game, string atom, CancellationToken cancellationToken)
    {
        _ = AtomId(atom);
        using var request = CreateRequest(target, game, Uri.EscapeDataString(atom), HttpMethod.Head, null, null);
        using var response = await SendAsync(request, target.Session, cancellationToken, allowUnsupportedHead: true).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is { } size)
            return new AtomMetadata(size, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
        using var fallback = CreateRequest(target, game, Uri.EscapeDataString(atom), HttpMethod.Get, null, null);
        fallback.Headers.Accept.Clear();
        fallback.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var headers = await SendAsync(fallback, target.Session, cancellationToken).ConfigureAwait(false);
        return new AtomMetadata(headers.Content.Headers.ContentLength ?? -1, headers.Headers.ETag?.ToString(),
            headers.Content.Headers.LastModified);
    }

    private async Task<byte[]> CopyAtomAsync(CloudTarget target, GameTitle game, string atom, Stream destination,
        string? etag, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        _ = AtomId(atom);
        using var request = CreateRequest(target, game, Uri.EscapeDataString(atom), HttpMethod.Get, null, etag);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await SendAsync(request, target.Session, cancellationToken).ConfigureAwait(false);
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
                CheckSession(target.Session, cancellationToken);
                if (count == 0) break;
                copied += count;
                if (total is { } length && copied > length) throw InvalidResponse();
                hash.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), idle.Token).ConfigureAwait(false);
                progress?.Report(new TransferProgress(copied, total));
            }
            if (total is { } expected && copied != expected)
                throw new XboxServiceException("The cloud download ended before all file bytes arrived. Retry the download.");
            CheckSession(target.Session, cancellationToken);
            return hash.GetHashAndReset();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CheckSession(target.Session, cancellationToken);
            throw new XboxServiceException("The cloud file transfer stalled. Check your connection and retry.");
        }
        catch (HttpRequestException)
        {
            CheckSession(target.Session, cancellationToken);
            throw new XboxServiceException("The cloud download was interrupted. Check your connection and retry.");
        }
    }

    private async Task<JsonDocument> RequestJsonAsync(CloudTarget target, GameTitle game, string suffix,
        HttpMethod method, string? body, string? etag, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(target, game, suffix, method, body, etag);
        using var response = await SendAsync(request, target.Session, cancellationToken).ConfigureAwait(false);
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
                CheckSession(target.Session, cancellationToken);
                if (payload.Length + count > MaximumJsonBytes) throw InvalidResponse();
                payload.Write(buffer, 0, count);
            }
            CheckSession(target.Session, cancellationToken);
            return JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException) { throw InvalidResponse(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CheckSession(target.Session, cancellationToken);
            throw new XboxServiceException("Xbox storage did not respond in time. Try again.");
        }
    }

    private HttpRequestMessage CreateRequest(CloudTarget target, GameTitle game, string suffix,
        HttpMethod method, string? body, string? etag)
    {
        var root = "https://titlestorage.xboxlive.com/connectedstorage/users/xuid(" + target.Xuid +
            ")/scids/" + Uri.EscapeDataString(game.ServiceConfigurationId!);
        var uri = new Uri(root + (suffix.StartsWith('?') || suffix.Length == 0 ? suffix : "/" + suffix));
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("XBL3.0", $"x={target.Session.UserHash};{target.Session.Token}");
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

    private CloudTarget GetSession(string xuid, GameTitle game, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = authentication.CurrentSession ?? throw new XboxServiceException("Sign in before browsing cloud saves.");
        CheckSession(session, cancellationToken);
        if (!ulong.TryParse(xuid, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
            throw new XboxServiceException("Enter a valid numeric Xbox User ID (XUID).");
        if (!game.HasStorageIdentity || game.PackageFamilyName is { } pfn &&
            (pfn.Length > 256 || pfn.Any(char.IsControl)))
            throw new XboxServiceException("This title has no valid connected-storage identity. It may not support Xbox PC cloud saves.");
        return new CloudTarget(id.ToString(CultureInfo.InvariantCulture), session);
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

    private static string PackedSafeFolderName(string name)
    {
        var safe = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) || character is '.' or ' ' ? '_' : character)).Trim();
        return safe.Length == 0 ? "container" : safe.Length > 60 ? safe[..60] : safe;
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
    private static XboxServiceException MalformedPack() => new("The packed save file is damaged or incomplete. Download it again before extracting.");
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
    private sealed record CloudTarget(string Xuid, XboxSession Session);
    private sealed record BlobMetadata(string Path, string? DisplayName, string? ETag, DateTimeOffset? Modified);
    private sealed record ContainerSnapshot(BlobMetadata Metadata, Dictionary<string, string> Atoms);
    private sealed record AtomMetadata(long Size, string? ETag, DateTimeOffset? Modified = null);
    private sealed record PackedEntry(string Path, string Atom, string Name);
}

