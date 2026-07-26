using LiveTranslate.App.Localization;
using LiveTranslate.App.Services;
using LiveTranslate.App.Views;
using Microsoft.Extensions.DependencyInjection;
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

        this.SetWindowSize(960, 700);
        this.CenterOnScreen();

        Nav.SelectedItem = SubtitlesItem;
        ContentFrame.Navigate(typeof(SubtitlePage));

        Closed += async (_, _) =>
        {
            var session = App.Services.GetRequiredService<SubtitleSessionService>();
            await session.ShutdownAsync();
        };
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
