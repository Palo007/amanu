using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amanu.App;
using Xunit;

namespace Amanu.Live.Tests;

public sealed class WheelScrollTests
{
    [Fact]
    public void Small_wheel_events_scroll_as_far_as_one_notch_together() => OnSta(() =>
    {
        var viewer = Viewer();
        var notch = SystemParameters.WheelScrollLines * WheelScrollViewer.LinePixels;
        // A high-resolution wheel sends one notch as four events of 30.
        for (var i = 0; i < 4; i++) Wheel(viewer, -30);
        viewer.UpdateLayout();
        Assert.Equal(notch, viewer.VerticalOffset, 3);
        Wheel(viewer, -120);
        viewer.UpdateLayout();
        Assert.Equal(2 * notch, viewer.VerticalOffset, 3);
        Wheel(viewer, 240);
        viewer.UpdateLayout();
        Assert.Equal(0, viewer.VerticalOffset, 3);
    });

    [Fact]
    public void The_offset_stops_at_the_end_of_the_content() => OnSta(() =>
    {
        var viewer = Viewer();
        for (var i = 0; i < 100; i++) Wheel(viewer, -120);
        viewer.UpdateLayout();
        Assert.Equal(viewer.ScrollableHeight, viewer.VerticalOffset, 3);
    });

    private static ScrollViewer Viewer()
    {
        var viewer = Ui.Scroll(new Border { Height = 2000, Width = 100 });
        Assert.IsType<WheelScrollViewer>(viewer);
        viewer.Measure(new Size(200, 300));
        viewer.Arrange(new Rect(0, 0, 200, 300));
        viewer.UpdateLayout();
        return viewer;
    }

    private static void Wheel(UIElement target, int delta) =>
        target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent });

    private static void OnSta(Action check)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { check(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The scroll check did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
