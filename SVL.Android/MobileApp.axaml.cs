using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace SVL.Android;

public sealed partial class MobileApp : global::Avalonia.Application
{
    private static WeakReference<MobileMainView>? s_mainView;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            var view = new MobileMainView(new CinderboxBridge());
            s_mainView = new WeakReference<MobileMainView>(view);
            singleView.MainView = view;
        }
        base.OnFrameworkInitializationCompleted();
    }

    internal static void RefreshRuntimeStatus()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (s_mainView?.TryGetTarget(out var view) == true)
                view.RefreshRuntimeStatus();
        });
    }
}
