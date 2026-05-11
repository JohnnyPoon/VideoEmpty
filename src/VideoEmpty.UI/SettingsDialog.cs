using System;
using Avalonia.Controls;
using Avalonia.Layout;

namespace VideoEmpty.UI;

public sealed class SettingsDialog : Window
{
    private readonly CheckBox _enableDelete;
    private readonly TextBox _daysBox;

    public bool? EnableAutoDelete { get; private set; }
    public int? AutoDeleteDays { get; private set; }

    public SettingsDialog(UiSettings settings)
    {
        Title = "Settings";
        Width = 420;
        Height = 220;

        _enableDelete = new CheckBox
        {
            Content = "Auto-delete backup files after N days",
            IsChecked = settings.AutoDeleteBackupsEnabled
        };
        _daysBox = new TextBox { Text = settings.AutoDeleteBackupsDays.ToString(), Width = 80 };

        var ok = new Button { Content = "Save", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };

        ok.Click += (_, _) =>
        {
            EnableAutoDelete = _enableDelete.IsChecked == true;
            AutoDeleteDays = int.TryParse(_daysBox.Text, out var d) ? Math.Max(1, d) : 90;
            Close(true);
        };
        cancel.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(12),
            Spacing = 10,
            Children =
            {
                _enableDelete,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { new TextBlock { Text = "Days:" }, _daysBox }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { ok, cancel }
                }
            }
        };
    }
}
