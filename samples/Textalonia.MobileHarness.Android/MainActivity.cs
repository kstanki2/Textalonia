using Android.App;
using Android.Content.PM;
using Android.Runtime;
using Android.Views;
using Avalonia;
using Avalonia.Android;

namespace Textalonia.MobileHarness.Android;

[Activity(Label = "Textalonia qualification", Theme = "@style/QualificationTheme", MainLauncher = true,
    Exported = true, WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity { }

[global::Android.App.Application]
public sealed class MainApplication(nint javaReference, JniHandleOwnership transfer)
    : AvaloniaAndroidApplication<App>(javaReference, transfer)
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).WithInterFont();
}
