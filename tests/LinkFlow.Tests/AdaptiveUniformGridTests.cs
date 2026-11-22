using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkFlow.App.Controls;

namespace LinkFlow.Tests;

public sealed class AdaptiveUniformGridTests
{
    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(5, 3)]
    public Task Choices_fill_each_row_with_equal_widths_and_an_eight_pixel_gap(int count, int columns) => OnSta(() =>
    {
        var panel = Choices(count);
        Layout(panel, 384);
        AssertRowsFill(panel, 384, columns);
    });

    [Fact]
    public Task Resizing_reflows_existing_choices_without_leaving_an_empty_slot_in_the_final_row() => OnSta(() =>
    {
        var panel = Choices(5);
        foreach (var (width, columns) in new[] { (256.0, 2), (600.0, 3), (900.0, 5), (1200.0, 5), (256.0, 2) })
        {
            Layout(panel, width);
            AssertRowsFill(panel, width, columns);
        }
    });

    [Fact]
    public Task Collapsing_and_restoring_a_choice_reclaims_its_space_and_preserves_order() => OnSta(() =>
    {
        var panel = Choices(3);
        Layout(panel, 384);
        panel.Children[1].Visibility = Visibility.Collapsed;
        Layout(panel, 384);
        AssertRowsFill(panel, 384, 2);
        Assert.True(panel.Children[2].TranslatePoint(new Point(), panel).X > 0);
        panel.Children[1].Visibility = Visibility.Visible;
        Layout(panel, 384);
        AssertRowsFill(panel, 384, 3);
    });

    [Fact]
    public Task Final_row_text_is_measured_at_its_expanded_width_before_height_is_chosen() => OnSta(() =>
    {
        var panel = new AdaptiveUniformGrid { MinItemWidth = 128, MaxColumns = 3, StretchLastRow = true };
        for (int i = 0; i < 5; i++)
            panel.Children.Add(new TextBlock
            {
                Text = "alpha beta gamma delta epsilon zeta eta theta",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 14, TextWrapping = TextWrapping.Wrap
            });
        panel.Measure(new Size(384, double.PositiveInfinity));
        double firstHeight = panel.Children[0].DesiredSize.Height;
        double lastHeight = panel.Children[4].DesiredSize.Height;
        Assert.True(lastHeight < firstHeight, $"Expanded text should wrap less: first={firstHeight}, last={lastHeight}.");
        AssertClose(firstHeight + lastHeight, panel.DesiredSize.Height);
        panel.Arrange(new Rect(0, 0, 384, panel.DesiredSize.Height));
        AssertClose(192, panel.Children[4].RenderSize.Width);
        AssertClose(lastHeight, panel.Children[4].RenderSize.Height);
    });

    [Fact]
    public Task Default_layout_keeps_the_shared_grid_final_row_in_its_original_column() => OnSta(() =>
    {
        var panel = Choices(5);
        panel.StretchLastRow = false;
        Layout(panel, 384);
        AssertClose(panel.Children[0].RenderSize.Width, panel.Children[4].RenderSize.Width);
        Assert.True(panel.Children[4].TranslatePoint(new Point(), panel).X + panel.Children[4].RenderSize.Width < 384);
    });

    [Fact]
    public Task Stacked_cards_keep_their_column_widths_when_the_last_row_option_is_set() => OnSta(() =>
    {
        var panel = Choices(5);
        panel.StackColumns = true;
        Layout(panel, 384);
        AssertClose(panel.Children[0].RenderSize.Width, panel.Children[4].RenderSize.Width);
        Assert.True(panel.Children[4].TranslatePoint(new Point(), panel).X + panel.Children[4].RenderSize.Width < 384);
    });

    private static AdaptiveUniformGrid Choices(int count)
    {
        var panel = new AdaptiveUniformGrid { MinItemWidth = 128, MaxColumns = 6, StretchLastRow = true };
        for (int i = 0; i < count; i++)
            panel.Children.Add(new Border { Height = 34, Margin = new Thickness(0, 0, 8, 8) });
        return panel;
    }

    private static void Layout(AdaptiveUniformGrid panel, double contentWidth)
    {
        // The page compensates the trailing child margin with -8 on the host.
        panel.Measure(new Size(contentWidth + 8, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, contentWidth + 8, panel.DesiredSize.Height));
    }

    private static void AssertRowsFill(AdaptiveUniformGrid panel, double width, int columns)
    {
        var children = panel.Children.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToArray();
        for (int offset = 0; offset < children.Length; offset += columns)
        {
            var row = children.Skip(offset).Take(columns).ToArray();
            double expectedWidth = (width + 8) / row.Length - 8;
            double right = 0;
            foreach (UIElement child in row)
            {
                Point position = child.TranslatePoint(new Point(), panel);
                AssertClose(expectedWidth, child.RenderSize.Width);
                AssertClose(right == 0 ? 0 : right + 8, position.X);
                AssertClose(children[offset].TranslatePoint(new Point(), panel).Y, position.Y);
                right = position.X + child.RenderSize.Width;
            }
            AssertClose(width, right);
        }
    }

    private static void AssertClose(double expected, double actual) => Assert.InRange(Math.Abs(expected - actual), 0, 0.001);

    private static Task OnSta(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); done.TrySetResult(); }
            catch (Exception exception) { done.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
