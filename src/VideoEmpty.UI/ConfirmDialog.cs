using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace VideoEmpty.UI;

internal sealed class ConfirmDialog : Window
{
    public static Task<bool> ShowAsync(Window owner, string title, string message)
    {
        var dlg = new ConfirmDialog(title, message);
        return dlg.ShowDialog<bool>(owner);
    }

    private ConfirmDialog(string title, string message)
    {
        Title = title;
        Width = 480;
        Height = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var ok = new Button { Content = "Install", IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Not now", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);

        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok }
                },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
            }
        };
    }
}
