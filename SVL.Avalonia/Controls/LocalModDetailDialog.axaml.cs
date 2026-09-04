using Avalonia;
using Avalonia.Controls;
using System.Collections.Generic;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class LocalModDetailDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, string>(nameof(Title), "本地 Mod 详情");

    public static readonly StyledProperty<string> ModNameProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, string>(nameof(ModName), string.Empty);

    public static readonly StyledProperty<string> VersionProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, string>(nameof(Version), string.Empty);

    public static readonly StyledProperty<string> AuthorProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, string>(nameof(Author), string.Empty);

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, string>(nameof(Description), string.Empty);

    public static readonly StyledProperty<string> UniqueIdProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, string>(nameof(UniqueId), string.Empty);

    public static readonly StyledProperty<string> ModPathProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, string>(nameof(ModPath), string.Empty);

    public static readonly StyledProperty<string> SourceFileNameProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, string>(nameof(SourceFileName), "无");

    public static readonly StyledProperty<bool> HasUpdateProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, bool>(nameof(HasUpdate), false);

    public static readonly StyledProperty<string> IsEnabledBackgroundProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, string>(nameof(IsEnabledBackground), "#D2A679");

    public static readonly StyledProperty<string> IsEnabledTextProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, string>(nameof(IsEnabledText), "已启用");

    public static readonly StyledProperty<IEnumerable<object>?> DependenciesProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, IEnumerable<object>?>(nameof(Dependencies));

    public static readonly StyledProperty<bool> HasDependenciesProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, bool>(nameof(HasDependencies), false);

    public static readonly StyledProperty<bool> CanOpenFolderProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, bool>(nameof(CanOpenFolder), true);

    public static readonly StyledProperty<ICommand?> OpenFolderCommandProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, ICommand?>(nameof(OpenFolderCommand));

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, ICommand?>(nameof(CloseCommand));

    public static readonly StyledProperty<ICommand?> OpenDependencyCommandProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, ICommand?>(nameof(OpenDependencyCommand));

    public static readonly StyledProperty<ICommand?> OpenLocalizationContributionCommandProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, ICommand?>(nameof(OpenLocalizationContributionCommand));

    public static readonly StyledProperty<ICommand?> ShowLocalizationContributorInfoCommandProperty =
        AvaloniaProperty.Register<LocalModDetailDialog, ICommand?>(nameof(ShowLocalizationContributorInfoCommand));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string ModName
    {
        get => GetValue(ModNameProperty);
        set => SetValue(ModNameProperty, value);
    }

    public string Version
    {
        get => GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    public string Author
    {
        get => GetValue(AuthorProperty);
        set => SetValue(AuthorProperty, value);
    }

    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string UniqueId
    {
        get => GetValue(UniqueIdProperty);
        set => SetValue(UniqueIdProperty, value);
    }

    public string ModPath
    {
        get => GetValue(ModPathProperty);
        set => SetValue(ModPathProperty, value);
    }

    public string SourceFileName
    {
        get => GetValue(SourceFileNameProperty);
        set => SetValue(SourceFileNameProperty, value);
    }

    public bool HasUpdate
    {
        get => GetValue(HasUpdateProperty);
        set => SetValue(HasUpdateProperty, value);
    }

    public string IsEnabledBackground
    {
        get => GetValue(IsEnabledBackgroundProperty);
        set => SetValue(IsEnabledBackgroundProperty, value);
    }

    public string IsEnabledText
    {
        get => GetValue(IsEnabledTextProperty);
        set => SetValue(IsEnabledTextProperty, value);
    }

    public IEnumerable<object>? Dependencies
    {
        get => GetValue(DependenciesProperty);
        set => SetValue(DependenciesProperty, value);
    }

    public bool HasDependencies
    {
        get => GetValue(HasDependenciesProperty);
        set => SetValue(HasDependenciesProperty, value);
    }

    public bool CanOpenFolder
    {
        get => GetValue(CanOpenFolderProperty);
        set => SetValue(CanOpenFolderProperty, value);
    }

    public ICommand? OpenFolderCommand
    {
        get => GetValue(OpenFolderCommandProperty);
        set => SetValue(OpenFolderCommandProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public ICommand? OpenDependencyCommand
    {
        get => GetValue(OpenDependencyCommandProperty);
        set => SetValue(OpenDependencyCommandProperty, value);
    }

    public ICommand? OpenLocalizationContributionCommand
    {
        get => GetValue(OpenLocalizationContributionCommandProperty);
        set => SetValue(OpenLocalizationContributionCommandProperty, value);
    }

    public ICommand? ShowLocalizationContributorInfoCommand
    {
        get => GetValue(ShowLocalizationContributorInfoCommandProperty);
        set => SetValue(ShowLocalizationContributorInfoCommandProperty, value);
    }

    public LocalModDetailDialog()
    {
        InitializeComponent();
    }
}
