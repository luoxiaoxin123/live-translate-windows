using System.Diagnostics;
using LiveTranslate.App.Localization;
using LiveTranslate.App.Services;
using LiveTranslate.App.ViewModels;
using LiveTranslate.App.Views;
using LiveTranslate.Core.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIEx;

namespace LiveTranslate.App;

public sealed partial class MainWindow : WindowEx
{
    public MainWindow()
    {
        InitializeComponent();
        Title = L.AppTitle;
        SubtitlesItem.Content = L.NavSubtitles;
        SettingsItem.Content = L.NavSettings;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);

        this.SetWindowSize(960, 700);
        this.CenterOnScreen();

        Nav.SelectedItem = SubtitlesItem;
        ContentFrame.Navigate(typeof(SubtitlePage));

        // ContentDialog needs a XamlRoot, which only exists once the content is loaded.
        Nav.Loaded += async (_, _) => await CheckForUpdateAsync();

        Closed += async (_, _) =>
        {
            var session = App.Services.GetRequiredService<SubtitleSessionService>();
            await session.ShutdownAsync();
        };
    }

    private bool _updateChecked;

    private async Task CheckForUpdateAsync()
    {
        if (_updateChecked) return;
        _updateChecked = true;

        var current = SettingsViewModel.ReadVersion();
        var update = await UpdateChecker.CheckAsync(current);
        if (update is null || Content?.XamlRoot is not { } root) return;

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = L.UpdateTitle,
            Content = L.UpdateSummary(update.Version, current),
            PrimaryButtonText = L.UpdateDownload,
            CloseButtonText = L.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            // Code-created dialogs don't pick up the Fluent style on their own.
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                Process.Start(new ProcessStartInfo(update.Url) { UseShellExecute = true });
            }
        }
        catch (Exception)
        {
            // Another dialog is already open (only one ContentDialog may show at a time) — skip this launch.
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        var target = (string?)item.Tag switch
        {
            "settings" => typeof(SettingsPage),
            _ => typeof(SubtitlePage),
        };
        if (ContentFrame.CurrentSourcePageType != target)
        {
            ContentFrame.Navigate(target);
        }
    }
}
