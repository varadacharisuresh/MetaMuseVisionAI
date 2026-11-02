using System;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace UvcViewer;

// Presentation only. The original ResolutionPicker / FrameRatePicker remain the
// source of truth for the existing USB camera mode selection and Apply logic.
public partial class MainPage
{
    private bool _choosingVideoResolution;

    private void OnVideoResolutionSelectorClicked(object sender, EventArgs e) =>
        OpenVideoOptions(ResolutionPicker, true);

    private void OnVideoFpsSelectorClicked(object sender, EventArgs e) =>
        OpenVideoOptions(FrameRatePicker, false);

    private void OpenVideoOptions(Picker picker, bool resolution)
    {
        _choosingVideoResolution = resolution;
        VideoOptionsTitle.Text = resolution ? "Choose resolution" : "Choose frame rate";
        VideoOptionsPanel.Children.Clear();

        if (picker.Items.Count == 0)
        {
            VideoOptionsPanel.Children.Add(new Label
            {
                Text = "Connect a USB camera to load the available MJPEG modes.",
                TextColor = Color.FromArgb("#BCD5EA"),
                FontSize = 14
            });
        }
        else
        {
            for (int i = 0; i < picker.Items.Count; i++)
            {
                bool chosen = picker.SelectedIndex == i;
                var row = new Border
                {
                    BackgroundColor = Color.FromArgb(chosen ? "#21496B" : "#172F49"),
                    Stroke = new SolidColorBrush(Color.FromArgb(chosen ? "#1688FF" : "#3A5876")),
                    StrokeThickness = chosen ? 1.5 : 1,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle
                    { CornerRadius = new CornerRadius(12) },
                    Padding = new Thickness(12, 2)
                };
                var content = new Grid { HeightRequest = 50 };
                content.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                content.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(32)));
                content.Children.Add(new Label
                {
                    Text = picker.Items[i],
                    TextColor = Colors.White,
                    FontSize = 16,
                    VerticalOptions = LayoutOptions.Center
                });
                if (chosen)
                {
                    var tick = new Label
                    {
                        Text = "✓", TextColor = Color.FromArgb("#6CD6FF"), FontSize = 23,
                        HorizontalTextAlignment = TextAlignment.Center,
                        VerticalOptions = LayoutOptions.Center
                    };
                    Grid.SetColumn(tick, 1);
                    content.Children.Add(tick);
                }
                var tap = new Button
                {
                    Text = "", BackgroundColor = Colors.Transparent, CommandParameter = i
                };
                tap.Clicked += OnVideoOptionSelected;
                Grid.SetColumnSpan(tap, 2);
                content.Children.Add(tap);
                row.Content = content;
                VideoOptionsPanel.Children.Add(row);
            }
        }
        VideoOptionsOverlay.IsVisible = true;
    }

    private void OnVideoOptionSelected(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: int index }) return;
        var picker = _choosingVideoResolution ? ResolutionPicker : FrameRatePicker;
        if (index < 0 || index >= picker.Items.Count) return;

        // Changing the resolution invokes the existing OnResolutionChanged handler,
        // which repopulates the camera's supported FPS choices.
        picker.SelectedIndex = index;
        VideoOptionsOverlay.IsVisible = false;
    }

    private void OnVideoOptionsCancelClicked(object sender, EventArgs e) =>
        VideoOptionsOverlay.IsVisible = false;
}
