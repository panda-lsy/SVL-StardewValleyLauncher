using Avalonia;
using Avalonia.Controls;
using System.Collections.Generic;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class ModSearchListControl : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ModSearchListControl, string>(nameof(Title), "搜索结果");

    public static readonly StyledProperty<IEnumerable<string>> ItemsProperty =
        AvaloniaProperty.Register<ModSearchListControl, IEnumerable<string>>(nameof(Items), new List<string>());

    public static readonly StyledProperty<string?> SelectedItemProperty =
        AvaloniaProperty.Register<ModSearchListControl, string?>(nameof(SelectedItem));

    public static readonly StyledProperty<ICommand?> RefreshCommandProperty =
        AvaloniaProperty.Register<ModSearchListControl, ICommand?>(nameof(RefreshCommand));

    public static readonly StyledProperty<ICommand?> OpenDetailsCommandProperty =
        AvaloniaProperty.Register<ModSearchListControl, ICommand?>(nameof(OpenDetailsCommand));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public IEnumerable<string> Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public string? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public ICommand? RefreshCommand
    {
        get => GetValue(RefreshCommandProperty);
        set => SetValue(RefreshCommandProperty, value);
    }

    public ICommand? OpenDetailsCommand
    {
        get => GetValue(OpenDetailsCommandProperty);
        set => SetValue(OpenDetailsCommandProperty, value);
    }

    public ModSearchListControl()
    {
        InitializeComponent();
    }
}
