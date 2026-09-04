using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using System;
using System.Windows.Input;

namespace SVL.Avalonia.Controls;

public partial class DownloadFloatingButton : UserControl
{
    public static readonly StyledProperty<int> PendingTaskCountProperty =
        AvaloniaProperty.Register<DownloadFloatingButton, int>(nameof(PendingTaskCount), 0);

    public static readonly StyledProperty<ICommand?> OpenQueueCommandProperty =
        AvaloniaProperty.Register<DownloadFloatingButton, ICommand?>(nameof(OpenQueueCommand));

    public static readonly DirectProperty<DownloadFloatingButton, bool> HasPendingTasksProperty =
        AvaloniaProperty.RegisterDirect<DownloadFloatingButton, bool>(
            nameof(HasPendingTasks),
            button => button.HasPendingTasks);

    private bool _hasPendingTasks;
    private bool _isDragging;
    private Point _dragStartPoint;
    private bool _dragHandled;
    private const double DragThreshold = 5.0;

    public int PendingTaskCount
    {
        get => GetValue(PendingTaskCountProperty);
        set => SetValue(PendingTaskCountProperty, value);
    }

    public ICommand? OpenQueueCommand
    {
        get => GetValue(OpenQueueCommandProperty);
        set => SetValue(OpenQueueCommandProperty, value);
    }

    public bool HasPendingTasks
    {
        get => _hasPendingTasks;
        private set => SetAndRaise(HasPendingTasksProperty, ref _hasPendingTasks, value);
    }

    public DownloadFloatingButton()
    {
        InitializeComponent();

        HasPendingTasks = PendingTaskCount > 0;

        var button = this.FindControl<Button>("FloatingBtn");
        if (button != null)
        {
            // Button.OnPointerPressed marks the event handled, so subscribe with
            // handledEventsToo to keep receiving pointer events for drag tracking.
            button.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, handledEventsToo: true);
            button.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, handledEventsToo: true);
            button.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, handledEventsToo: true);
            button.Click += OnButtonClick;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PendingTaskCountProperty)
        {
            HasPendingTasks = PendingTaskCount > 0;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.Pointer.IsPrimary)
        {
            return;
        }

        _dragStartPoint = e.GetPosition(this);
        _isDragging = false;
        _dragHandled = false;

        if (sender is IInputElement inputElement)
        {
            e.Pointer.Capture(inputElement);
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Captured == null)
        {
            return;
        }

        if (Parent is not Visual parent)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var delta = pos - _dragStartPoint;

        if (!_isDragging && (delta.X * delta.X + delta.Y * delta.Y) > DragThreshold * DragThreshold)
        {
            _isDragging = true;
            _dragHandled = true;

            // The host uses Right/Bottom alignment + Margin. Switch to Left/Top so the
            // Margin acts as an absolute position, preserving the current visual spot to
            // avoid a jump when the alignment changes.
            var currentInParent = e.GetPosition(parent);
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Top;
            Margin = new Thickness(
                currentInParent.X - _dragStartPoint.X,
                currentInParent.Y - _dragStartPoint.Y,
                0,
                0);
        }

        if (_isDragging)
        {
            var currentPos = e.GetPosition(parent);
            var maxX = Math.Max(0, parent.Bounds.Width - Bounds.Width);
            var maxY = Math.Max(0, parent.Bounds.Height - Bounds.Height);
            var x = Math.Clamp(currentPos.X - _dragStartPoint.X, 0, maxX);
            var y = Math.Clamp(currentPos.Y - _dragStartPoint.Y, 0, maxY);
            Margin = new Thickness(x, y, 0, 0);
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Captured != null)
        {
            e.Pointer.Capture(null);
        }

        _isDragging = false;
        // _dragHandled stays set so the subsequent Click handler can suppress the command.
    }

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        // Button.OnClick raises the Click event, then runs the bound Command only when
        // Handled is false. Marking the event handled suppresses the command after a drag.
        if (_dragHandled)
        {
            e.Handled = true;
            _dragHandled = false;
        }
    }
}
