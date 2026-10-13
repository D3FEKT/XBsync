using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XBsync.Models;
using XBsync.ViewModels;

namespace XBsync.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }
    private bool _compact;
    private bool _updatingFileSelection;

    public MainPage(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Version}";
        Navigation.SelectedItem = GamesNavigation;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateFileSelection();
        ApplyLayout(ActualWidth);
        await ViewModel.InitializeAsync();
    }
    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout(e.NewSize.Width);
    private void SaveFilesScroller_SizeChanged(object sender, SizeChangedEventArgs e) =>
        SaveFilesLayout.Height = Math.Max(520, e.NewSize.Height);
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedFiles)) UpdateFileSelection();
        if (e.PropertyName == nameof(MainViewModel.Section))
            Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(item => item.Tag?.ToString() == ViewModel.Section.ToString());
        if (e.PropertyName is nameof(MainViewModel.ShowCompactDetails) or nameof(MainViewModel.SelectedGame) or nameof(MainViewModel.Section)) ApplyLayout(ActualWidth);
    }
    private void ApplyLayout(double width)
    {
        if (BrowserPane is null) return;
        _compact = width < 1240;
        Navigation.PaneDisplayMode = _compact ? NavigationViewPaneDisplayMode.LeftCompact : NavigationViewPaneDisplayMode.Left;
        Navigation.IsPaneOpen = !_compact;
        SidebarCard.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        ConnectionHeader.Visibility = width < 1050 ? Visibility.Collapsed : Visibility.Visible;
        BrandSubtitle.Visibility = width < 800 ? Visibility.Collapsed : Visibility.Visible;
        BrowserColumn.Width = _compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0.9, GridUnitType.Star);
        DetailsColumn.Width = _compact ? new GridLength(0) : new GridLength(1.1, GridUnitType.Star);
        Grid.SetColumn(DetailsPane, _compact ? 0 : 1);
        BrowserPane.Visibility = !_compact || !ViewModel.ShowCompactDetails ? Visibility.Visible : Visibility.Collapsed;
        DetailsPane.Visibility = !_compact || ViewModel.ShowCompactDetails ? Visibility.Visible : Visibility.Collapsed;
        CompactControls.Visibility = _compact ? Visibility.Visible : Visibility.Collapsed;
        ShowDetailsButton.Visibility = _compact && !ViewModel.ShowCompactDetails && ViewModel.LibraryVisibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        ShowDetailsButton.IsEnabled = ViewModel.SelectedGame is not null;
    }
    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && Enum.TryParse<MainSection>(item.Tag?.ToString(), out var section))
        {
            ViewModel.Section = section;
            ViewModel.ShowCompactDetails = section == MainSection.CloudSaves && ViewModel.SelectedGame is not null;
        }
    }
    private void Files_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingFileSelection) ViewModel.SetSelectedFiles(((ListView)sender).SelectedItems.OfType<CloudSaveFile>());
    }
    private void UpdateFileSelection()
    {
        if (FilesList is null) return;
        _updatingFileSelection = true;
        try
        {
            var selected = ViewModel.SelectedFiles;
            foreach (var item in FilesList.SelectedItems.OfType<CloudSaveFile>().Where(file => !selected.Contains(file)).ToArray())
                FilesList.SelectedItems.Remove(item);
            foreach (var file in selected)
                if (!FilesList.SelectedItems.Contains(file)) FilesList.SelectedItems.Add(file);
        }
        finally { _updatingFileSelection = false; }
    }
    private void ShowDetails_Click(object sender, RoutedEventArgs e) => ViewModel.ShowCompactDetails = true;
    private void BackToGames_Click(object sender, RoutedEventArgs e) => ViewModel.ShowCompactDetails = false;
}
