using Avalonia;
using Avalonia.Controls;
using System.Collections.Generic;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class ModUpdateConfirmDialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ModUpdateConfirmDialog, string>(nameof(Title), "确认更新 Mod");

    public static readonly StyledProperty<string> SummaryProperty =
        AvaloniaProperty.Register<ModUpdateConfirmDialog, string>(nameof(Summary), "以下 Mod 将执行更新。\n");

    public static readonly StyledProperty<IEnumerable<string>> UpdateItemsProperty =
        AvaloniaProperty.Register<ModUpdateConfirmDialog, IEnumerable<string>>(nameof(UpdateItems), new List<string>());

    public static readonly StyledProperty<ICommand?> ConfirmCommandProperty =
        AvaloniaProperty.Register<ModUpdateConfirmDialog, ICommand?>(nameof(ConfirmCommand));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<ModUpdateConfirmDialog, ICommand?>(nameof(CancelCommand));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Summary
    {
        get => GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    public IEnumerable<string> UpdateItems
    {
        get => GetValue(UpdateItemsProperty);
        set => SetValue(UpdateItemsProperty, value);
    }

    public ICommand? ConfirmCommand
    {
        get => GetValue(ConfirmCommandProperty);
        set => SetValue(ConfirmCommandProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public ModUpdateConfirmDialog()
    {
        InitializeComponent();
    }
}
