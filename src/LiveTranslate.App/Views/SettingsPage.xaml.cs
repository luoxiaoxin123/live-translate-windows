using LiveTranslate.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace LiveTranslate.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel Vm { get; }

    public SettingsPage()
    {
        Vm = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();

        // Drag-selecting in a TextBox raises BringIntoView on every mouse move.
        // Let the field scroll itself; don't relayout the whole settings page.
        BringIntoViewRequested += (_, e) =>
        {
            if (e.OriginalSource is TextBox or PasswordBox) e.Handled = true;
        };
    }
}
