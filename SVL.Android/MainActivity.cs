using global::Android.App;
using global::Android.Content.PM;
using Avalonia.Android;

namespace SVL.Android;

[Activity(
    Label = "Stardew Valley Launcher",
    Theme = "@style/Theme.AppCompat.Light.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity<MobileApp>
{
}
