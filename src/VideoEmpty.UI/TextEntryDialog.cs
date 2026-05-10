using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VideoEmpty.UI;

public class TextEntryDialog : Window
{
    private readonly TextBox _box;
    public TextEntryDialog(string prompt)
    {
        Title = "Enter text";
        Width = 420; Height = 220;
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        _box = new TextBox { AcceptsReturn = true, Height = 100 };

        var panel = new DockPanel { Margin = new Avalonia.Thickness(10) };
        var label = new TextBlock { Text = prompt, Margin = new Avalonia.Thickness(0,0,0,6) };
        DockPanel.SetDock(label, Dock.Top);
        panel.Children.Add(label);

        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
                                       HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                       Spacing = 6 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(_box);

        Content = panel;
        ok.Click += (_, _) => Close(_box.Text ?? "");
        cancel.Click += (_, _) => Close(null);
    }
}
