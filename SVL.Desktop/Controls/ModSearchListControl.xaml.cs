using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SVL.Desktop.Controls;

public partial class ModSearchListControl : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable),
        typeof(ModSearchListControl),
        new PropertyMetadata(null));

    public static readonly DependencyProperty DetailsCommandProperty = DependencyProperty.Register(
        nameof(DetailsCommand),
        typeof(ICommand),
        typeof(ModSearchListControl),
        new PropertyMetadata(null));

    public static readonly DependencyProperty ShowDetailsButtonProperty = DependencyProperty.Register(
        nameof(ShowDetailsButton),
        typeof(bool),
        typeof(ModSearchListControl),
        new PropertyMetadata(false));

    public ModSearchListControl()
    {
        InitializeComponent();
    }

    public IEnumerable ItemsSource
    {
        get => (IEnumerable)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ICommand DetailsCommand
    {
        get => (ICommand)GetValue(DetailsCommandProperty);
        set => SetValue(DetailsCommandProperty, value);
    }

    public bool ShowDetailsButton
    {
        get => (bool)GetValue(ShowDetailsButtonProperty);
        set => SetValue(ShowDetailsButtonProperty, value);
    }
}
