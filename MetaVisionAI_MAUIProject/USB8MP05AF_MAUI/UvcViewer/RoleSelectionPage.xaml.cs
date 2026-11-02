using System;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace UvcViewer;

public partial class RoleSelectionPage : ContentPage
{
    private int _selectedRoleIndex;

    public RoleSelectionPage()
    {
        InitializeComponent();
        _selectedRoleIndex = Array.FindIndex(WorkflowProfiles.Roles, r => r.Id == WorkflowSession.RoleId);
        if (_selectedRoleIndex < 0) _selectedRoleIndex = 0;
        RefreshRoleSelection();
    }

    private void RefreshRoleSelection()
    {
        RoleSelectionLabel.Text = WorkflowProfiles.Roles[_selectedRoleIndex].Name;
        RoleOptionsPanel.Children.Clear();
        for (int i = 0; i < WorkflowProfiles.Roles.Length; i++)
        {
            var role = WorkflowProfiles.Roles[i];
            var selected = i == _selectedRoleIndex;
            var row = new Border
            {
                BackgroundColor = Color.FromArgb(selected ? "#21496B" : "#172F49"),
                Stroke = new SolidColorBrush(Color.FromArgb(selected ? "#1688FF" : "#3A5876")),
                StrokeThickness = selected ? 1.5 : 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(12) },
                Padding = new Thickness(12, 2)
            };
            var content = new Grid { HeightRequest = 52 };
            content.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            content.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(32)));
            var title = new Label { Text = role.Name, TextColor = Colors.White, FontSize = 16,
                                    VerticalOptions = LayoutOptions.Center };
            Grid.SetColumn(title, 0);
            content.Children.Add(title);
            if (selected)
            {
                var tick = new Label { Text = "✓", TextColor = Color.FromArgb("#6CD6FF"), FontSize = 23,
                                       HorizontalTextAlignment = TextAlignment.Center, VerticalOptions = LayoutOptions.Center };
                Grid.SetColumn(tick, 1);
                content.Children.Add(tick);
            }
            var tap = new Button { BackgroundColor = Colors.Transparent, CommandParameter = i, Text = "" };
            tap.Clicked += OnRoleOptionClicked;
            Grid.SetColumnSpan(tap, 2);
            content.Children.Add(tap);
            row.Content = content;
            RoleOptionsPanel.Children.Add(row);
        }
    }

    private void OnRoleSelectorClicked(object sender, EventArgs e) => RoleChoicesOverlay.IsVisible = true;
    private void OnRoleSelectorCancelClicked(object sender, EventArgs e) => RoleChoicesOverlay.IsVisible = false;

    private void OnRoleOptionClicked(object sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: int index } &&
            index >= 0 && index < WorkflowProfiles.Roles.Length)
        {
            _selectedRoleIndex = index;
            RefreshRoleSelection();
            RoleChoicesOverlay.IsVisible = false;
        }
    }

    private void OnStartClicked(object sender, EventArgs e)
    {
        if (Window == null) return;
        var role = WorkflowProfiles.Roles[_selectedRoleIndex];
        WorkflowSession.RoleId = role.Id;
        WorkflowSession.TaskId = WorkflowProfiles.Task(role, WorkflowSession.TaskId).Id;
        Window.Page = new MainPage();
    }
}
