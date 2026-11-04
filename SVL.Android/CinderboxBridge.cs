using global::Android.Content;

namespace SVL.Android;

/// <summary>Android 预览版只调用运行器公开的启动入口，不读取其他应用的私有数据。</summary>
internal sealed class CinderboxBridge
{
    internal const string PackageName = "com.game.cinderbox";
    internal const string ReleaseUrl = "https://github.com/Ekyso/Cinderbox/releases";
    internal const string GuideUrl = "https://github.com/Ekyso/Cinderbox/wiki";

    private static Context Context => global::Android.App.Application.Context;

    public bool IsAvailable()
    {
        try { return Context.PackageManager?.GetLaunchIntentForPackage(PackageName) is not null; }
        catch (Exception) { return false; }
    }

    public bool TryOpenRunner()
    {
        try
        {
            var intent = Context.PackageManager?.GetLaunchIntentForPackage(PackageName);
            if (intent is null) return false;
            intent.AddFlags(ActivityFlags.NewTask);
            Context.StartActivity(intent);
            return true;
        }
        catch (Exception) { return false; }
    }

    public bool TryOpenReleasePage() => TryOpenWebPage(ReleaseUrl);
    public bool TryOpenGuide() => TryOpenWebPage(GuideUrl);

    private static bool TryOpenWebPage(string url)
    {
        try
        {
            using var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url));
            intent.AddFlags(ActivityFlags.NewTask);
            Context.StartActivity(intent);
            return true;
        }
        catch (Exception) { return false; }
    }
}
