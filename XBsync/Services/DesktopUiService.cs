using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using XBsync.Models;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using Launcher = Windows.System.Launcher;

namespace XBsync.Services;

public interface IDesktopUiService
{
    Task<AuthenticationResult> WithDeviceCodeDialogAsync(DeviceCodePrompt prompt, Func<CancellationToken, Task<AuthenticationResult>> authenticate, CancellationToken cancellationToken);
    void CopyText(string text);
    Task<string?> ChooseDownloadFolderAsync(CancellationToken cancellationToken);
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
    //Task<string?> ChooseReplacementFileAsync(CancellationToken cancellationToken);
    //Task<bool> ConfirmReplacementAsync(string name, CancellationToken cancellationToken);
    Task OpenDownloadFolderAsync();
}

public class DesktopUiService : IDesktopUiService
{
    private readonly SemaphoreSlim _modalGate = new(1, 1);
    private readonly CancellationTokenSource _windowClosed = new();
    private Window? _window;
    private FrameworkElement? _root;
    private DispatcherQueue? _dispatcher;

    public string DownloadFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "XBsync");

    public void Attach(Window window, FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(root);
        if (!window.DispatcherQueue.HasThreadAccess || !root.DispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("Attach must run on the window UI thread.");
        if (_window is not null)
        {
            if (ReferenceEquals(_window, window) && ReferenceEquals(_root, root)) return;
            throw new InvalidOperationException("The desktop UI service is already attached to a window.");
        }

        _window = window;
        _root = root;
        window.Closed += (_, _) => _windowClosed.Cancel();
        Volatile.Write(ref _dispatcher, window.DispatcherQueue);
    }

    public Task<AuthenticationResult> WithDeviceCodeDialogAsync(DeviceCodePrompt prompt, Func<CancellationToken, Task<AuthenticationResult>> authenticate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(authenticate);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt.UserCode);
        var uri = prompt.VerificationUri;
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !(uri.Host.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("login.live.com", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The sign-in link must be a Microsoft HTTPS address.", nameof(prompt));

        return WithModalAsync(token => ShowDeviceCodeDialogAsync(prompt, authenticate, token), cancellationToken);
    }

    public void CopyText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _windowClosed.Token.ThrowIfCancellationRequested();
        var dispatcher = Volatile.Read(ref _dispatcher) ?? throw new InvalidOperationException("Attach a window before using the desktop UI service.");
        if (!dispatcher.HasThreadAccess)
            throw new InvalidOperationException("CopyText must run on the window UI thread.");
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetText(text);
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return OnUiAsync(() => { action(); return Task.FromResult(true); }, cancellationToken);
    }

    public Task<string?> ChooseDownloadFolderAsync(CancellationToken cancellationToken) => WithModalAsync<string?>(async token =>
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window!));
        var operation = picker.PickSingleFolderAsync();
        var folder = await AwaitModalOperationAsync(operation, operation.Cancel, token);
        token.ThrowIfCancellationRequested();
        if (folder is null) return null;
        if (string.IsNullOrWhiteSpace(folder.Path))
            throw new IOException("The selected folder must have a local file system path.");
        DownloadFolder = folder.Path;
        return folder.Path;
    }, cancellationToken);

    //public Task<string?> ChooseReplacementFileAsync(CancellationToken cancellationToken) => WithModalAsync<string?>(async token =>
    //{
        //var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        //picker.FileTypeFilter.Add("*");
        //WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window!));
        //var operation = picker.PickSingleFileAsync();
        //var file = await AwaitModalOperationAsync(operation, operation.Cancel, token);
        //token.ThrowIfCancellationRequested();
        //if (file is not null && string.IsNullOrWhiteSpace(file.Path))
            //throw new IOException("The selected file must have a local file system path.");
        //return file?.Path;
    //}, cancellationToken);

    //public Task<bool> ConfirmReplacementAsync(string name, CancellationToken cancellationToken)
    //{
        //ArgumentException.ThrowIfNullOrWhiteSpace(name);
        //return WithModalAsync(async token =>
        //{
            //var closedGame = new CheckBox { Content = "I have closed the game" };
            //var content = new StackPanel { Spacing = 12 };
            //content.Children.Add(new TextBlock
            //{
                //Text = $"Replace the cloud save file \"{name}\" with the selected local file? Close the game first to prevent it from overwriting the replacement. The original is backed up before upload in %LOCALAPPDATA%/XBsync/Backups. The replacement and the other file mappings are checked before success is reported. A cancelled or failed request may still have changed the cloud; keep the backup and refresh before retrying.",
                //TextWrapping = TextWrapping.Wrap
            //});
            //content.Children.Add(closedGame);
            //var dialog = new ContentDialog
            //{
                //XamlRoot = GetXamlRoot(),
                //Title = "Replace cloud save file?",
                //Content = content,
                //PrimaryButtonText = "Replace",
                //IsPrimaryButtonEnabled = false,
                //CloseButtonText = "Cancel",
                //DefaultButton = ContentDialogButton.Close
            //};
            //closedGame.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
            //closedGame.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
            //return await ShowDialogAsync(dialog, token) == ContentDialogResult.Primary;
        //}, cancellationToken);
    //}

    public Task OpenDownloadFolderAsync() => OnUiAsync(async () =>
    {
        var token = _windowClosed.Token;
        var path = DownloadFolder;
        await Task.Run(() => Directory.CreateDirectory(path), token);
        token.ThrowIfCancellationRequested();
        var folder = await StorageFolder.GetFolderFromPathAsync(path).AsTask(token);
        if (!await Launcher.LaunchFolderAsync(folder).AsTask(token))
            throw new IOException("Windows could not open the download folder.");
        return true;
    }, _windowClosed.Token);

    private async Task<AuthenticationResult> ShowDeviceCodeDialogAsync(DeviceCodePrompt prompt, Func<CancellationToken, Task<AuthenticationResult>> authenticate, CancellationToken token)
    {
        if (prompt.ExpiresAt <= DateTimeOffset.UtcNow)
            return new(AuthenticationStatus.InteractionRequired, null, "The sign-in code expired. Start Sign in again.");
        var copyStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var copyButton = new Button { Content = "Copy code" };
        copyButton.Click += (_, _) =>
        {
            try
            {
                CopyText(prompt.UserCode);
                copyStatus.Text = "Code copied.";
            }
            catch (Exception exception) when (exception is COMException or UnauthorizedAccessException)
            {
                copyStatus.Text = "Windows could not copy the code. Select it and copy it manually.";
            }
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "Open Microsoft sign-in and enter this code. Keep this dialog open while you sign in.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = prompt.UserCode, IsTextSelectionEnabled = true, FontSize = 28 });
        content.Children.Add(copyButton);
        content.Children.Add(new HyperlinkButton { Content = "Open Microsoft sign-in", NavigateUri = prompt.VerificationUri });
        content.Children.Add(new TextBlock { Text = $"Code expires at {prompt.ExpiresAt.LocalDateTime:t}.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(copyStatus);
        var dialog = new ContentDialog
        {
            XamlRoot = GetXamlRoot(),
            Title = "Sign in to Xbox",
            Content = content,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.None
        };
        var authenticationCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var dialogCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dismissed = false;
        dialog.Opened += (_, _) => opened.TrySetResult(true);
        dialog.Closing += (_, _) =>
        {
            dismissed = true;
            authenticationCancellation.Cancel();
        };
        Task<ContentDialogResult>? showing = null;
        Task<AuthenticationResult>? authentication = null;
        try
        {
            showing = ShowDialogAsync(dialog, dialogCancellation.Token);
            if (await Task.WhenAny(showing, opened.Task).WaitAsync(token) == showing)
            {
                await showing;
                token.ThrowIfCancellationRequested();
                return new(AuthenticationStatus.InteractionRequired, null, "Sign-in cancelled.");
            }
            token.ThrowIfCancellationRequested();
            if (dismissed) return new(AuthenticationStatus.InteractionRequired, null, "Sign-in cancelled.");
            authentication = Task.Run(() => authenticate(authenticationCancellation.Token), authenticationCancellation.Token);
            var completed = await Task.WhenAny(authentication, showing).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (completed == showing || dismissed)
            {
                await showing;
                token.ThrowIfCancellationRequested();
                return new(AuthenticationStatus.InteractionRequired, null, "Sign-in cancelled.");
            }
            return await authentication.WaitAsync(token);
        }
        catch (OperationCanceledException) when (dismissed && !token.IsCancellationRequested)
        {
            return new(AuthenticationStatus.InteractionRequired, null, "Sign-in cancelled.");
        }
        finally
        {
            authenticationCancellation.Cancel();
            dialogCancellation.Cancel();
            try
            {
                if (showing is not null)
                {
                    try { await showing; }
                    catch (OperationCanceledException) when (dialogCancellation.IsCancellationRequested) { }
                }
            }
            finally
            {
                if (authentication is null) authenticationCancellation.Dispose();
                else _ = authentication.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    authenticationCancellation.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private XamlRoot GetXamlRoot() => _root?.XamlRoot ?? throw new InvalidOperationException("The attached window content must be loaded before showing a dialog.");

    private async Task<T> WithModalAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _windowClosed.Token);
        await _modalGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var result = await OnUiAsync(() => action(linked.Token), linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally { _modalGate.Release(); }
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, CancellationToken token)
    {
        void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            if (token.IsCancellationRequested) sender.Hide();
        }
        dialog.Opened += OnOpened;
        try
        {
            token.ThrowIfCancellationRequested();
            return await AwaitModalOperationAsync(dialog.ShowAsync(), dialog.Hide, token);
        }
        finally { dialog.Opened -= OnOpened; }
    }

    private async Task<T> AwaitModalOperationAsync<T>(IAsyncOperation<T> operation, Action dismiss, CancellationToken token)
    {
        var pending = operation.AsTask();
        try { return await pending.WaitAsync(token); }
        finally
        {
            if (!pending.IsCompleted)
            {
                await OnUiAsync(() =>
                {
                    dismiss();
                    return Task.FromResult(true);
                }, CancellationToken.None);
                try { await pending; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
        }
    }

    private async Task<T> OnUiAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dispatcher = Volatile.Read(ref _dispatcher) ?? throw new InvalidOperationException("Attach a window before using the desktop UI service.");
        if (dispatcher.HasThreadAccess) return await action();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        if (!dispatcher.TryEnqueue(async () =>
        {
            if (completion.Task.IsCompleted) return;
            registration.Dispose();
            try
            {
                token.ThrowIfCancellationRequested();
                completion.TrySetResult(await action());
            }
            catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }))
            throw new InvalidOperationException("The window UI dispatcher is no longer available.");
        return await completion.Task.ConfigureAwait(false);
    }
}
