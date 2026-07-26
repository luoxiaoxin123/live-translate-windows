using LiveTranslate.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace LiveTranslate.App.Views;

public sealed partial class SubtitlePage : Page
{
    public SubtitleViewModel Vm { get; }

    public SubtitlePage()
    {
        Vm = App.Services.GetRequiredService<SubtitleViewModel>();
        InitializeComponent();
    }
}
