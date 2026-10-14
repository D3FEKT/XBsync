using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using XBsync.Models;
using XBsync.Services;

namespace XBsync.ViewModels;

public sealed class AppInfo
{
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var value = informational ?? assembly.GetName().Version?.ToString() ?? "1.0.0";
        var plus = value.IndexOf('+');
        return plus >= 0 ? value[..plus] : value;
    }
}

public sealed partial class MainViewModel(IXboxAuthenticationService authentication, IXboxApiService api,
    ICloudSaveService cloud, IDesktopUiService desktop) : ObservableObject
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _accountRequest;
    private CancellationTokenSource? _gameRequest;
    private CancellationTokenSource? _fileRequest;
    private CancellationTokenSource? _transferRequest;
    private IReadOnlyList<GameTitle> _allGames = [];
    private IReadOnlyList<CloudSaveFile> _selectedFiles = [];
    private GameTitle? _resolvedStorageIdentity;
    private int _accountGeneration;
    private int _saveGeneration;
    private int _transferProgressGeneration;
    private const int DownloadConcurrency = 8;
    private bool _updatingContainers;
    private bool _initialized;
    private string? _loadedXuid;
    private string? _signedInXuid;

    public RangeObservableCollection<GameTitle> Games { get; } = [];
    public RangeObservableCollection<CloudSaveContainer> Containers { get; } = [];
    public RangeObservableCollection<CloudSaveFile> Files { get; } = [];
    public ObservableCollection<ActivityEntry> Activity { get; } = [];
    public string[] Filters { get; } = ["All games", "Storage identity known"];
    public string[] Sorts { get; } = ["Recently played", "Name"];
    public IReadOnlyList<CloudSaveFile> SelectedFiles => _selectedFiles;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LibraryVisibility), nameof(ProfileVisibility), nameof(SettingsVisibility))]
    public partial MainSection Section { get; set; } = MainSection.Games;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Gamertag), nameof(Xuid), nameof(AvatarUri), nameof(ConnectionText), nameof(AuthenticationVisibility))]
    [NotifyCanExecuteChangedFor(nameof(CopyXuidCommand), nameof(DisconnectCommand), nameof(RefreshSavesCommand), nameof(DownloadSelectedCommand), nameof(DownloadPackedCommand))]
    public partial XboxProfile? Profile { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand), nameof(RetrySilentSignInCommand), nameof(RefreshCommand), nameof(RefreshSavesCommand), nameof(DownloadSelectedCommand),
        nameof(LoadXuidCommand), nameof(UseMyAccountCommand), nameof(DownloadPackedCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyGamesVisibility))]
    public partial bool IsLoadingGames { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectFiles))]
    [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand), nameof(DownloadPackedCommand))]
    public partial bool IsLoadingFiles { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; } = "Checking local Xbox sign-in…";
    [ObservableProperty] public partial string ErrorMessage { get; set; } = "";
    [ObservableProperty] public partial bool HasError { get; set; }
    [ObservableProperty] public partial string LastCheckText { get; set; } = "No successful check yet";
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowsingAnotherAccount), nameof(XuidSourceText))]
    [NotifyCanExecuteChangedFor(nameof(LoadXuidCommand), nameof(UseMyAccountCommand))]
    public partial string TargetXuid { get; set; } = "";
    [ObservableProperty] public partial int FilterIndex { get; set; }
    [ObservableProperty] public partial int SortIndex { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListVisibility), nameof(GridVisibility))]
    public partial bool IsGridView { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameDetailsVisibility), nameof(NoGameVisibility))]
    [NotifyCanExecuteChangedFor(nameof(DownloadPackedCommand), nameof(RefreshSavesCommand), nameof(DownloadSelectedCommand))]
    public partial GameTitle? SelectedGame { get; set; }
    [ObservableProperty] public partial CloudSaveContainer? SelectedContainer { get; set; }
    [ObservableProperty] public partial CloudSaveFile? FocusedFile { get; set; }
    [ObservableProperty] public partial string FileStateMessage { get; set; } = "Choose a game to view its cloud save files.";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    public partial int SelectedFileCount { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransferVisibility), nameof(CanSelectFiles))]
    [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand), nameof(CancelTransferCommand), nameof(RefreshSavesCommand), nameof(RefreshCommand), nameof(DownloadPackedCommand))]
    public partial bool IsTransferring { get; set; }
    [ObservableProperty] public partial string TransferText { get; set; } = "";
    [ObservableProperty] public partial double TransferPercent { get; set; }
    [ObservableProperty] public partial bool IsTransferIndeterminate { get; set; }
    [ObservableProperty]
    public partial bool ShowCompactDetails { get; set; }

    public string Gamertag => Profile?.Gamertag ?? "Not connected";
    public string Xuid => Profile?.Xuid ?? "—";
    public bool IsBrowsingAnotherAccount => _loadedXuid is not null && _loadedXuid != _signedInXuid;
    public string XuidSourceText => IsBrowsingAnotherAccount
        ? "Browsing XUID " + _loadedXuid + " using the signed-in Xbox connection."
        : "Browsing the signed-in Xbox account.";
    private string? EffectiveXuid => _loadedXuid ?? _signedInXuid;
    public Uri? AvatarUri => Profile?.AvatarUri;
    public string ConnectionText => Profile is null ? "Disconnected" : "Xbox connected";
    public Visibility AuthenticationVisibility => Visible(Profile is null);
    public Visibility LibraryVisibility => Visible(Section is MainSection.Games or MainSection.CloudSaves);
    public Visibility ProfileVisibility => Visible(Section == MainSection.Profiles);
    public Visibility SettingsVisibility => Visible(Section == MainSection.Settings);
    public Visibility ListVisibility => Visible(!IsGridView);
    public Visibility GridVisibility => Visible(IsGridView);
    public Visibility EmptyGamesVisibility => Visible(!IsLoadingGames && Games.Count == 0);
    public Visibility GameDetailsVisibility => Visible(SelectedGame is not null);
    public Visibility NoGameVisibility => Visible(SelectedGame is null);
    public Visibility TransferVisibility => Visible(IsTransferring);
    public Visibility XuidNoticeVisibility => Visible(IsBrowsingAnotherAccount);
    public bool CanSelectFiles => !IsLoadingFiles && !IsTransferring;
    public string SelectionText => SelectedFileCount == 0 ? "Select one or more files to download."
        : $"{SelectedFileCount} file{(SelectedFileCount == 1 ? "" : "s")} selected";
    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public Task InitializeAsync()
    {
        if (_initialized) return Task.CompletedTask;
        _initialized = true;
        return AuthenticateAsync(false);
    }

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private Task SignInAsync() => AuthenticateAsync(true);
    [RelayCommand(CanExecute = nameof(CanAuthenticate))]
    private Task RetrySilentSignInAsync() => AuthenticateAsync(false);
    private bool CanAuthenticate() => !IsBusy;
    private bool CanSignIn() => !IsBusy;

    private async Task AuthenticateAsync(bool interactive)
    {
        var generation = ++_accountGeneration;
        _accountRequest?.Cancel();
        _accountRequest?.Dispose();
        _accountRequest = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _accountRequest.Token;
        IsBusy = true;
        ClearError();
        ClearLibrary();
        Profile = null;
        _signedInXuid = null;
        _loadedXuid = null;
        NotifyTargetState();
        StatusMessage = interactive ? "Waiting for Microsoft sign-in…" : "Checking local Xbox sign-in…";
        try
        {
            var result = interactive ? await authentication.SignInInteractivelyAsync(token) : await authentication.TrySignInSilentlyAsync(token);
            if (generation != _accountGeneration || token.IsCancellationRequested) return;
            StatusMessage = result.Message;
            Profile = result.Profile;
            _signedInXuid = result.Profile?.Xuid;
            TargetXuid = _signedInXuid ?? "";
            if (!result.IsSuccess)
            {
                if (result.Status is AuthenticationStatus.AccountMismatch or AuthenticationStatus.ServiceFailure) SetError(result.Message);
                return;
            }
            AddActivity("Xbox sign-in completed.");
            await LoadLibraryAsync(generation, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || generation != _accountGeneration) { }
        catch (OperationCanceledException)
        {
            if (generation == _accountGeneration)
            {
                StatusMessage = "Sign-in timed out. Check your connection and retry.";
                SetError(StatusMessage);
            }
        }
        catch (Exception exception)
        {
            if (generation == _accountGeneration && !token.IsCancellationRequested)
            {
                ReportServiceError(exception, "Sign-in failed. Retry or choose Sign in.");
                StatusMessage = ErrorMessage;
            }
        }
        finally { if (generation == _accountGeneration) IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (Profile is null) { await AuthenticateAsync(false); return; }
        var generation = _accountGeneration;
        var token = _accountRequest?.Token ?? _lifetime.Token;
        IsBusy = true;
        ClearError();
        try
        {
            await LoadLibraryAsync(generation, token);
            if (generation == _accountGeneration && !token.IsCancellationRequested && SelectedGame is { } game && EffectiveXuid is { } xuid)
                await LoadContainersAsync(game, xuid, generation, SelectedContainer?.Id, _selectedFiles.Select(file => file.Id).ToArray());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { if (generation == _accountGeneration && !token.IsCancellationRequested) ReportServiceError(exception, "Could not refresh your games. Try again."); }
        finally { if (generation == _accountGeneration) IsBusy = false; }
    }
    private bool CanRefresh() => !IsBusy && !IsTransferring;

    [RelayCommand(CanExecute = nameof(CanLoadXuid))]
    private async Task LoadXuidAsync()
    {
        if (authentication.CurrentProfile is null) { await AuthenticateAsync(false); return; }
        string target;
        try { target = RequireXuid(TargetXuid); }
        catch (Exception exception) { SetError(exception.Message); return; }
        await SwitchTargetAsync(target, "Browsing XUID " + target + ".");
    }
    private bool CanLoadXuid() => !IsBusy && !IsTransferring && _signedInXuid is not null;

    [RelayCommand(CanExecute = nameof(CanUseMyAccount))]
    private Task UseMyAccountAsync() => SwitchTargetAsync(_signedInXuid, "Returning to the signed-in Xbox account.");
    private bool CanUseMyAccount() => !IsBusy && !IsTransferring && _signedInXuid is not null && IsBrowsingAnotherAccount;

    private async Task SwitchTargetAsync(string? xuid, string activity)
    {
        _loadedXuid = xuid;
        TargetXuid = xuid ?? "";
        NotifyTargetState();
        IsBusy = true;
        ClearError();
        ClearLibrary();
        var generation = ++_accountGeneration;
        _accountRequest?.Cancel();
        _accountRequest?.Dispose();
        _accountRequest = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _accountRequest.Token;
        StatusMessage = activity;
        AddActivity(activity);
        try { await LoadLibraryAsync(generation, token); }
        catch (OperationCanceledException) when (generation != _accountGeneration || token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (generation == _accountGeneration)
            {
                ReportServiceError(exception, "Could not load games and saves for that account. Check the XUID and your connection, then retry.");
                StatusMessage = ErrorMessage;
            }
        }
        finally { if (generation == _accountGeneration) IsBusy = false; }
    }

    private void NotifyTargetState()
    {
        OnPropertyChanged(nameof(IsBrowsingAnotherAccount));
        OnPropertyChanged(nameof(XuidSourceText));
        OnPropertyChanged(nameof(XuidNoticeVisibility));
        LoadXuidCommand.NotifyCanExecuteChanged();
        UseMyAccountCommand.NotifyCanExecuteChanged();
    }

    private static string RequireXuid(string xuid)
    {
        if (!ulong.TryParse(xuid?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value == 0)
            throw new XboxServiceException("Enter a valid numeric Xbox User ID (XUID).");
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private async Task LoadLibraryAsync(int generation, CancellationToken token)
    {
        IsLoadingGames = true;
        try
        {
            if (EffectiveXuid is not { } target) return;
            var profileTask = api.GetProfileAsync(target, token);
            var gamesTask = api.GetPlayedGamesAsync(target, token);
            try { await Task.WhenAll(profileTask, gamesTask); }
            finally
            {
                if (generation == _accountGeneration && !token.IsCancellationRequested)
                {
                    if (profileTask.IsCompletedSuccessfully) Profile = profileTask.Result;
                    if (gamesTask.IsCompletedSuccessfully)
                    {
                        _allGames = gamesTask.Result;
                        ApplyGameFilter();
                        StatusMessage = $"Loaded {_allGames.Count} played games. Choose a game to browse its saves.";
                        AddActivity($"Loaded {_allGames.Count} played games.");
                    }
                    if (profileTask.IsCompletedSuccessfully || gamesTask.IsCompletedSuccessfully)
                        LastCheckText = $"Last check: {DateTimeOffset.Now:g}";
                }
            }
        }
        finally { if (generation == _accountGeneration) IsLoadingGames = false; }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void Disconnect()
    {
        ++_accountGeneration;
        _accountRequest?.Cancel();
        authentication.SignOut();
        Profile = null;
        _signedInXuid = null;
        _loadedXuid = null;
        NotifyTargetState();
        IsBusy = false;
        IsLoadingGames = false;
        ClearLibrary();
        ClearError();
        StatusMessage = "Disconnected. Saved XBsync sign-in remains available for a later retry.";
        AddActivity("Disconnected from Xbox.");
    }
    private bool CanDisconnect() => Profile is not null;
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void CopyXuid()
    {
        try { if (Profile is not null) desktop.CopyText(Profile.Xuid); }
        catch (Exception) { SetError("Windows could not copy the XUID. Select it and copy it manually."); }
    }

    partial void OnSearchTextChanged(string value) => ApplyGameFilter();
    partial void OnFilterIndexChanged(int value) => ApplyGameFilter();
    partial void OnSortIndexChanged(int value) => ApplyGameFilter();

    private void ApplyGameFilter()
    {
        var previous = SelectedGame;
        var query = _allGames.Where(game => game.Name.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (FilterIndex == 1) query = query.Where(game => game.HasStorageIdentity);
        var filtered = SortIndex == 1 ? query.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()
            : query.OrderByDescending(game => game.LastPlayed ?? DateTimeOffset.MinValue).ThenBy(game => game.Name).ToArray();
        Games.ReplaceAll(filtered);
        SelectedGame = filtered.FirstOrDefault(game => previous is not null && game.TitleId == previous.TitleId &&
            game.ServiceConfigurationId == previous.ServiceConfigurationId && game.PackageFamilyName == previous.PackageFamilyName) ?? filtered.FirstOrDefault();
        OnPropertyChanged(nameof(EmptyGamesVisibility));
    }

    partial void OnProfileChanged(XboxProfile? oldValue, XboxProfile? newValue)
    {
        if (oldValue?.Xuid != newValue?.Xuid) ClearLibrary();
    }

    partial void OnSelectedGameChanged(GameTitle? value)
    {
        _resolvedStorageIdentity = null;
        InvalidateTransfer();
        _gameRequest?.Cancel();
        _fileRequest?.Cancel();
        _gameRequest = null;
        _fileRequest = null;
        SetContainer(null);
        Containers.Clear();
        ClearFiles();
        ClearError();
        ShowCompactDetails = false;
        FileStateMessage = value is null ? "Choose a game to view its cloud save files." : "Loading save containers...";
        RefreshSavesCommand.NotifyCanExecuteChanged();
        if (value is not null && EffectiveXuid is { } xuid) _ = LoadContainersAsync(value, xuid, _accountGeneration);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshSaves))]
    private Task RefreshSavesAsync()
    {
        if (!CanRefreshSaves() || SelectedGame is not { } game || EffectiveXuid is not { } xuid) return Task.CompletedTask;
        ClearError();
        return LoadContainersAsync(game, xuid, _accountGeneration, SelectedContainer?.Id, _selectedFiles.Select(file => file.Id).ToArray());
    }
    private bool CanRefreshSaves() => !IsBusy && !IsTransferring && Profile is not null && SelectedGame is not null;

    private async Task LoadContainersAsync(GameTitle game, string xuid, int generation, string? preferredContainer = null,
        IReadOnlyList<string>? preferredFiles = null)
    {
        _gameRequest?.Cancel();
        _fileRequest?.Cancel();
        _fileRequest = null;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_accountRequest?.Token ?? _lifetime.Token);
        _gameRequest = request;
        var token = request.Token;
        SetContainer(null);
        Containers.Clear();
        ClearFiles();
        IsLoadingFiles = true;
        FileStateMessage = "Loading save containers...";
        try
        {
            if (!Matches(game, xuid, generation, token) || _gameRequest != request) return;
            var storageGame = _resolvedStorageIdentity ?? game;
            if (!game.HasStorageIdentity && _resolvedStorageIdentity is null)
            {
                FileStateMessage = "Resolving the selected game's connected-storage identity...";
                storageGame = await api.ResolveStorageIdentityAsync(xuid, game, token);
                if (!Matches(game, xuid, generation, token) || _gameRequest != request) return;
                if (storageGame.HasStorageIdentity) _resolvedStorageIdentity = storageGame;
            }
            if (!storageGame.HasStorageIdentity)
            {
                FileStateMessage = "Xbox Live did not return a valid connected-storage identity for this game. This title may not support Xbox PC cloud saves. Use Refresh saves to retry.";
                SetError(FileStateMessage);
                return;
            }
            FileStateMessage = "Loading save containers...";
            var containers = await cloud.GetContainersAsync(xuid, storageGame, token);
            if (!Matches(game, xuid, generation, token) || _gameRequest != request) return;
            Containers.ReplaceAll(containers);
            var selected = Containers.FirstOrDefault(container => container.Id == preferredContainer) ?? Containers.FirstOrDefault();
            SetContainer(selected);
            if (selected is null) FileStateMessage = "No cloud saves were returned. Play and sync this game with this account, then Refresh saves.";
            else await LoadFilesAsync(game, selected, xuid, generation, selected.Id == preferredContainer ? preferredFiles : null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (Matches(game, xuid, generation, token) && _gameRequest == request)
            {
                FileStateMessage = "Could not load save containers. Use Refresh saves to retry.";
                ReportServiceError(exception, "Could not load save containers. Check your connection, then Refresh saves.");
            }
        }
        finally
        {
            if (_gameRequest == request)
            {
                _gameRequest = null;
                if (_fileRequest is null) IsLoadingFiles = false;
            }
        }
    }

    private void SetContainer(CloudSaveContainer? container)
    {
        _updatingContainers = true;
        try { SelectedContainer = container; }
        finally { _updatingContainers = false; }
    }

    partial void OnSelectedContainerChanged(CloudSaveContainer? value)
    {
        if (!_updatingContainers) InvalidateTransfer();
        _fileRequest?.Cancel();
        _fileRequest = null;
        ClearFiles();
        FileStateMessage = value is null ? "Choose a save container." : "Loading save files...";
        if (!_updatingContainers && value is not null && SelectedGame is { } game && EffectiveXuid is { } xuid)
            _ = LoadFilesAsync(game, value, xuid, _accountGeneration);
    }

    private async Task LoadFilesAsync(GameTitle game, CloudSaveContainer container, string xuid, int generation,
        IReadOnlyList<string>? preferredFiles = null)
    {
        _fileRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_accountRequest?.Token ?? _lifetime.Token);
        _fileRequest = request;
        var token = request.Token;
        IsLoadingFiles = true;
        FileStateMessage = "Loading save files...";
        try
        {
            var files = await cloud.GetFilesAsync(xuid, _resolvedStorageIdentity ?? game, container, token);
            if (!Matches(game, xuid, generation, token) || _fileRequest != request || SelectedContainer != container) return;
            Files.ReplaceAll(files);
            FileStateMessage = files.Count == 0 ? "This container has no files. Choose another container or Refresh saves." : $"{files.Count} files in {container.Name}.";
            if (preferredFiles is not null) SetSelectedFiles(Files.Where(file => preferredFiles.Contains(file.Id)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (Matches(game, xuid, generation, token) && _fileRequest == request && SelectedContainer == container)
            {
                FileStateMessage = "Could not load save files. Use Refresh saves to retry.";
                ReportServiceError(exception, "Could not load save files. Check your connection, then Refresh saves.");
            }
        }
        finally
        {
            if (_fileRequest == request)
            {
                _fileRequest = null;
                IsLoadingFiles = false;
            }
        }
    }

    private bool Matches(GameTitle game, string xuid, int generation, CancellationToken token) =>
        !token.IsCancellationRequested && generation == _accountGeneration && SelectedGame == game && EffectiveXuid == xuid;

    public void SetSelectedFiles(IEnumerable<CloudSaveFile> files)
    {
        _selectedFiles = files.Where(file => Files.Contains(file) && file.ContainerId == SelectedContainer?.Id).DistinctBy(file => file.Id).ToArray();
        SelectedFileCount = _selectedFiles.Count;
        FocusedFile = _selectedFiles.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedFiles));
        DownloadSelectedCommand.NotifyCanExecuteChanged();
    }
    private bool CanDownload() => !IsBusy && !IsLoadingFiles && !IsTransferring && Profile is not null && SelectedGame is not null &&
        SelectedContainer is not null && _selectedFiles.Count > 0 && _selectedFiles.All(file => Files.Contains(file) && file.ContainerId == SelectedContainer.Id);
    //private bool CanReplace() => CanDownload() && _selectedFiles.Count == 1;

    private bool CanDownloadAll() => !IsBusy && !IsLoadingFiles && !IsTransferring && Profile is not null && SelectedGame is not null;

    [RelayCommand(CanExecute = nameof(CanDownloadAll))]
    private async Task DownloadPackedAsync()
    {
        if (!CanDownloadAll() || EffectiveXuid is not { } xuid || SelectedGame is not { } game) return;
        var generation = _accountGeneration;
        var selection = _saveGeneration;
        using var request = BeginTransfer("Choose a download folder.");
        var token = request.Token;
        string? folder = null;
        string? pack = null;
        var extracted = 0;
        try
        {
            folder = await desktop.ChooseDownloadFolderAsync(token);
            if (!Matches(game, xuid, generation, token) || selection != _saveGeneration) return;
            if (folder is null) { TransferText = "Download cancelled before any files were written."; return; }
            folder = Path.GetFullPath(folder);
            var storageGame = await ResolveStorageGameAsync(game, xuid, generation, token);
            if (storageGame is null) return;
            var list = await cloud.GetContainersAsync(xuid, storageGame, token);
            if (!TransferMatches(game, xuid, generation, selection, token)) return;
            var ordered = list.OrderBy(container => container.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (ordered.Length == 0)
            {
                TransferText = StatusMessage = "No cloud saves were returned for this game.";
                return;
            }
            pack = Path.Combine(folder, $".xbsync-{Guid.NewGuid():N}.xbs");
            var result = await DownloadPackedToFileAsync(xuid, storageGame, ordered, pack, request, game, generation, selection, token);
            if (!TransferMatches(game, xuid, generation, selection, token)) return;
            if (result.FileCount == 0)
            {
                StatusMessage = $"No save files were returned for this game. Nothing was written to {folder}.";
                TransferText = StatusMessage;
                TransferPercent = 100;
                AddActivity(StatusMessage);
                return;
            }
            TransferText = $"Extracting {result.FileCount} file{(result.FileCount == 1 ? "" : "s")} to {folder}...";
            await using (var packed = new FileStream(pack, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                (extracted, _) = await cloud.ExtractPackedAsync(packed, folder, token);
            if (!TransferMatches(game, xuid, generation, selection, token)) return;
            TransferPercent = 100;
            StatusMessage = $"Downloaded and extracted {extracted} file{(extracted == 1 ? "" : "s")} to {folder}.";
            TransferText = StatusMessage;
            AddActivity(StatusMessage);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (TransferMatches(game, xuid, generation, selection, token))
            {
                TransferText = StatusMessage = $"Download cancelled. {extracted} file{(extracted == 1 ? "" : "s")} extracted{(folder is null ? "" : $" to {folder}")}; extracted files are kept.";
                AddActivity(StatusMessage);
            }
        }
        catch (Exception exception)
        {
            if (TransferMatches(game, xuid, generation, selection, token))
            {
                var fallback = $"Could not finish packing this game's saves. {extracted} file{(extracted == 1 ? "" : "s")} extracted and kept{(folder is null ? "" : $" in {folder}")}. Check your connection, available disk space, and folder permissions, then retry.";
                ReportTransferError(exception, fallback);
                TransferText = ErrorMessage;
                AddActivity(ErrorMessage);
            }
        }
        finally
        {
            if (pack is not null)
            {
                try { File.Delete(pack); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            EndTransfer(request, selection);
        }
    }

    private async Task<PackedDownloadResult> DownloadPackedToFileAsync(string xuid, GameTitle game,
        IReadOnlyList<CloudSaveContainer> containers, string path, CancellationTokenSource request, GameTitle selected,
        int generation, int selection, CancellationToken token)
    {
        await using var packed = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var progress = new Progress<TransferProgress>(value =>
        {
            if (request.IsCancellationRequested || !TransferMatches(selected, xuid, generation, selection, token)) return;
            var total = value.TotalBytes is >= 0 ? value.TotalBytes.Value : 0;
            IsTransferIndeterminate = true;
            TransferPercent = 0;
            TransferText = total > 0
                ? $"Downloading save - {Math.Max(0, value.BytesTransferred):N0} / {total:N0} bytes"
                : $"Downloading save - {Math.Max(0, value.BytesTransferred):N0} bytes";
        });
        var result = await cloud.DownloadPackedAsync(xuid, game, containers, packed, DownloadConcurrency, progress, token);
        token.ThrowIfCancellationRequested();
        await packed.FlushAsync(token);
        await Task.Run(() => packed.Flush(true), token);
        return result;
    }

    private async Task<GameTitle?> ResolveStorageGameAsync(GameTitle game, string xuid, int generation, CancellationToken token)
    {
        var storageGame = _resolvedStorageIdentity ?? game;
        if (!game.HasStorageIdentity && _resolvedStorageIdentity is null)
        {
            TransferText = "Resolving the selected game's connected-storage identity...";
            storageGame = await api.ResolveStorageIdentityAsync(xuid, game, token);
            if (!Matches(game, xuid, generation, token)) return null;
            if (storageGame.HasStorageIdentity) _resolvedStorageIdentity = storageGame;
        }
        if (storageGame.HasStorageIdentity) return storageGame;
        SetError("Xbox Live did not return a valid connected-storage identity for this game. This title may not support Xbox PC cloud saves. Use Refresh saves to retry.");
        TransferText = ErrorMessage;
        return null;
    }

    private bool TransferMatches(GameTitle game, string xuid, int generation, int selection, CancellationToken token) =>
        !token.IsCancellationRequested && generation == _accountGeneration && selection == _saveGeneration && SelectedGame == game && EffectiveXuid == xuid;

    private static string SafeFolderName(string name)
    {
        var safe = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) || character is '.' or ' ' ? '_' : character)).Trim();
        return safe.Length == 0 ? "container" : safe.Length > 60 ? safe[..60] : safe;
    }

    private static string DescribeError(Exception exception) => exception is XboxServiceException service ? service.Message
        : exception is OperationCanceledException ? "The request timed out." : exception.Message;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadSelectedAsync()
    {
        if (!CanDownload() || EffectiveXuid is not { } xuid || SelectedGame is not { } game || SelectedContainer is not { } container) return;
        var storageGame = _resolvedStorageIdentity ?? game;
        var files = _selectedFiles.ToArray();
        var generation = _accountGeneration;
        var selection = _saveGeneration;
        using var request = BeginTransfer("Choose a download folder.");
        var token = request.Token;
        var completed = 0;
        string? folder = null;
        var currentFile = files[0];
        try
        {
            folder = await desktop.ChooseDownloadFolderAsync(token);
            if (!Matches(game, xuid, generation, token) || selection != _saveGeneration) return;
            if (folder is null) { TransferText = "Download cancelled before any files were written."; return; }
            folder = Path.GetFullPath(folder);
            folder = Path.Combine(folder, SafeFolderName(container.Name));
            Directory.CreateDirectory(folder);
            for (var index = 0; index < files.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                currentFile = files[index];
                TransferText = $"Downloading {index + 1}/{files.Length}: {currentFile.Name}";
                var progress = CreateTransferProgress(request, game, container, xuid, generation, selection,
                    "Downloading", currentFile, index, files.Length, currentFile.Size);
                var destination = await DownloadFileAsync(xuid, storageGame, currentFile, folder, progress, token);
                completed++;
                ++_transferProgressGeneration;
                if (!TransferContextMatches(game, container, xuid, generation, selection)) return;
                TransferPercent = completed * 100d / files.Length;
                AddActivity($"Downloaded {currentFile.Name} to {destination}.");
            }
            token.ThrowIfCancellationRequested();
            TransferText = StatusMessage = $"Downloaded {completed} file{(completed == 1 ? "" : "s")} to {folder}.";
            AddActivity(StatusMessage);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (TransferContextMatches(game, container, xuid, generation, selection))
            {
                TransferText = StatusMessage = $"Download cancelled. {completed} of {files.Length} files completed{(completed > 0 ? $" in {folder}" : "")}; completed files are kept.";
                AddActivity(StatusMessage);
            }
        }
        catch (Exception exception)
        {
            if (TransferContextMatches(game, container, xuid, generation, selection))
            {
                var fallback = $"Could not download {currentFile.Name}. {completed} of {files.Length} files completed and are kept. Check your connection, available disk space, and folder permissions, then retry the remaining files.";
                ReportTransferError(exception, fallback);
                TransferText = ErrorMessage;
                AddActivity(ErrorMessage);
            }
        }
        finally { EndTransfer(request, selection); }
    }

    // Upload & Replace was removed from the UI. This backend stays disabled until the replace transport is verified.
    //[RelayCommand(CanExecute = nameof(CanReplace))]
    //private async Task UploadReplaceAsync()
    //{
        //if (!CanReplace() || Profile is not { } user || SelectedGame is not { } game || SelectedContainer is not { } container) return;
        //var storageGame = _resolvedStorageIdentity ?? game;
        //var file = _selectedFiles[0];
        //var generation = _accountGeneration;
        //var selection = _saveGeneration;
        //using var request = BeginTransfer("Choose the replacement file.");
        //var token = request.Token;
        //ReplacementResult? completedResult = null;
        //try
        //{
            //var path = await desktop.ChooseReplacementFileAsync(token);
            //if (!Matches(game, user.Xuid, generation, token) || selection != _saveGeneration) return;
            //if (path is null) { TransferText = "Replacement cancelled before upload."; return; }
            //await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            //TransferText = $"Confirm replacement of {file.Name}.";
            //if (!await desktop.ConfirmReplacementAsync(file.Name, token)) { TransferText = "Replacement cancelled before upload."; return; }
            //token.ThrowIfCancellationRequested();
            //if (!TransferContextMatches(game, container, user.Xuid, generation, selection)) return;
            //TransferText = $"Backing up and replacing {file.Name}...";
            //var result = await cloud.ReplaceAsync(user.Xuid, storageGame, container, file, source,
                //CreateTransferProgress(request, game, container, user.Xuid, generation, selection, "Replacing", file, 0, 1, source.Length), token);
            //token.ThrowIfCancellationRequested();
            //if (!TransferContextMatches(game, container, user.Xuid, generation, selection)) return;
            //if (!result.Verified)
            //{
                //var backup = string.IsNullOrWhiteSpace(result.BackupPath) ? BackupFolder : result.BackupPath;
                //throw new XboxServiceException($"The replacement could not be verified. Original backup: {backup}.");
            //}
            //completedResult = result;
            //++_transferProgressGeneration;
            //TransferPercent = 100;
            //TransferText = StatusMessage = $"Replacement verified for {file.Name}. Original backup: {(string.IsNullOrWhiteSpace(result.BackupPath) ? BackupFolder : result.BackupPath)}.";
            //AddActivity(StatusMessage);
        //}
        //catch (OperationCanceledException) when (token.IsCancellationRequested)
        //{
            //if (TransferContextMatches(game, container, user.Xuid, generation, selection))
            //{
                //TransferText = StatusMessage = $"Replacement cancelled. The cloud may have changed; Refresh saves before retrying. Originals backed up before upload remain in {BackupFolder} (%LOCALAPPDATA%/XBsync/Backups).";
                //AddActivity(StatusMessage);
            //}
        //}
        //catch (Exception exception)
        //{
            //if (TransferContextMatches(game, container, user.Xuid, generation, selection))
            //{
                //ReportTransferError(exception, $"Replacement failed or could not be verified. Check the local file is readable and the game is closed, then Refresh saves before retrying. Originals backed up before upload remain in {BackupFolder} (%LOCALAPPDATA%/XBsync/Backups).");
                //TransferText = ErrorMessage;
                //AddActivity(ErrorMessage);
            //}
        //}
        //finally { EndTransfer(request, selection); }
        //if (completedResult is not null && Matches(game, user.Xuid, generation, token) && selection == _saveGeneration)
            //await LoadContainersAsync(game, user.Xuid, generation, container.Id, [completedResult.File.Id]);
    //}

    // TODO: unverified, needs further development - only used by the disabled replacement path.
    //private static string BackupFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XBsync", "Backups");

    private CancellationTokenSource BeginTransfer(string text)
    {
        var request = CancellationTokenSource.CreateLinkedTokenSource(_accountRequest?.Token ?? _lifetime.Token);
        _transferRequest = request;
        ClearError();
        TransferText = text;
        TransferPercent = 0;
        IsTransferIndeterminate = true;
        IsTransferring = true;
        return request;
    }

    private void EndTransfer(CancellationTokenSource request, int selection)
    {
        if (_transferRequest != request) return;
        _transferRequest = null;
        ++_transferProgressGeneration;
        if (selection != _saveGeneration) { TransferText = ""; TransferPercent = 0; }
        IsTransferIndeterminate = false;
        IsTransferring = false;
    }

    private void InvalidateTransfer()
    {
        ++_saveGeneration;
        var wasActive = _transferRequest is { IsCancellationRequested: false };
        _transferRequest?.Cancel();
        ++_transferProgressGeneration;
        TransferPercent = 0;
        TransferText = _transferRequest is null ? "" : "Cancelling the previous transfer.";
        if (wasActive) AddActivity("Cancelled the previous transfer after changing the game, container, or account. The cloud may have changed; refresh the affected game saves.");
        CancelTransferCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCancelTransfer))]
    private void CancelTransfer()
    {
        _transferRequest?.Cancel();
        TransferText = "Cancelling transfer...";
        CancelTransferCommand.NotifyCanExecuteChanged();
    }
    private bool CanCancelTransfer() => IsTransferring && _transferRequest is { IsCancellationRequested: false };

    private bool TransferContextMatches(GameTitle game, CloudSaveContainer? container, string xuid, int generation, int selection) =>
        generation == _accountGeneration && selection == _saveGeneration && SelectedGame == game && EffectiveXuid == xuid &&
        (container is null || SelectedContainer == container);

    private IProgress<TransferProgress> CreateTransferProgress(CancellationTokenSource request, GameTitle game, CloudSaveContainer? container,
        string xuid, int generation, int selection, string action, CloudSaveFile file, int index, int count, long fallbackTotal)
    {
        var phase = ++_transferProgressGeneration;
        return new Progress<TransferProgress>(progress =>
        {
            if (_transferRequest != request || request.IsCancellationRequested || phase != _transferProgressGeneration ||
                !TransferContextMatches(game, container, xuid, generation, selection)) return;
            var total = progress.TotalBytes is >= 0 ? progress.TotalBytes.Value : fallbackTotal;
            var bytes = Math.Max(0, progress.BytesTransferred);
            IsTransferIndeterminate = total <= 0;
            var fraction = total > 0 ? Math.Clamp((double)bytes / total, 0, 1) : 0;
            TransferPercent = (index + fraction) * 100 / count;
            TransferText = $"{action} {index + 1}/{count}: {file.Name} - {bytes:N0}{(total >= 0 ? $" / {total:N0}" : "")} bytes";
        });
    }

    private async Task<string> DownloadFileAsync(string xuid, GameTitle game, CloudSaveFile file, string folder,
        IProgress<TransferProgress> progress, CancellationToken token)
    {
        string? temporary = Path.Combine(folder, $".xbsync-{Guid.NewGuid():N}.partial");
        var temporaryCreated = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                temporaryCreated = true;
                await cloud.DownloadAsync(xuid, game, file, stream, progress, token);
                token.ThrowIfCancellationRequested();
                if (file.Size >= 0 && stream.Length != file.Size)
                    throw new XboxServiceException($"The downloaded size of {file.Name} did not match its cloud metadata. Refresh saves and retry.");
                await stream.FlushAsync(token);
                await Task.Run(() => stream.Flush(true), token);
            }
            var name = SafeDownloadName(file.Name);
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            for (var suffix = 0; suffix < 10000; suffix++)
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(folder, suffix == 0 ? name : $"{stem} ({suffix}){extension}");
                try
                {
                    File.Move(temporary!, destination, false);
                    temporary = null;
                    return destination;
                }
                catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
            }
            throw new XboxServiceException("Too many files share the download name. Choose a different folder and retry.");
        }
        finally
        {
            if (temporary is not null && temporaryCreated)
            {
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new XboxServiceException($"Could not remove the incomplete download at {temporary}. Delete that partial file before retrying.");
                }
            }
        }
    }

    private static string SafeDownloadName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        name = string.Concat(name.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character)).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name)) name = "save";
        var device = name.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
            (device[3] is >= '1' and <= '9' or '¹' or '²' or '³')) name = "_" + name;
        if (name.Length > 160)
        {
            var extension = Path.GetExtension(name);
            if (extension.Length > 32) extension = extension[..32];
            name = name[..(160 - extension.Length)].TrimEnd('.', ' ') + extension;
        }
        return name;
    }

    private void ReportTransferError(Exception exception, string fallback) => SetError(exception is XboxServiceException service
        ? $"{service.Message} {fallback}" : exception is OperationCanceledException ? $"The transfer timed out. {fallback}" : fallback);

    [RelayCommand]
    private async Task OpenDownloadFolderAsync()
    {
        try { await desktop.OpenDownloadFolderAsync(); }
        catch (Exception) { SetError("Could not open the download folder. Check that it exists and Windows can access it, then try again."); }
    }

    private void ClearFiles() { Files.Clear(); SetSelectedFiles([]); IsLoadingFiles = false; }
    private void ClearLibrary()
    {
        _resolvedStorageIdentity = null;
        InvalidateTransfer();
        _gameRequest?.Cancel();
        _fileRequest?.Cancel();
        _gameRequest = null;
        _fileRequest = null;
        _allGames = [];
        Games.Clear();
        SelectedGame = null;
        Containers.Clear();
        SelectedContainer = null;
        ClearFiles();
        OnPropertyChanged(nameof(EmptyGamesVisibility));
    }
    private void ClearError() { HasError = false; ErrorMessage = ""; }
    private void SetError(string message) { ErrorMessage = message; HasError = true; }
    private void ReportServiceError(Exception exception, string fallback) => SetError(exception is XboxServiceException service
        ? service.Message : exception is OperationCanceledException ? "The service request timed out. Check your connection and retry." : fallback);
    private void AddActivity(string text)
    {
        Activity.Insert(0, new ActivityEntry(DateTimeOffset.Now, text));
        if (Activity.Count > 100) Activity.RemoveAt(Activity.Count - 1);
    }
    public void Stop()
    {
        _resolvedStorageIdentity = null;
        ++_accountGeneration;
        _lifetime.Cancel();
        _accountRequest?.Cancel();
        _gameRequest?.Cancel();
        _fileRequest?.Cancel();
        _transferRequest?.Cancel();
    }
}
