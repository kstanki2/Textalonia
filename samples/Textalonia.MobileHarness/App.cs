using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

namespace Textalonia.MobileHarness;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Textalonia.MobileHarness/"))
        { Source = new Uri("avares://Textalonia/Themes/Generic.axaml") });
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IActivityApplicationLifetime activity) activity.MainViewFactory = () => new QualificationView();
        else if (ApplicationLifetime is ISingleViewApplicationLifetime mobile) mobile.MainView = new QualificationView();
        base.OnFrameworkInitializationCompleted();
    }
}

