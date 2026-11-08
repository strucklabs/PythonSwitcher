using System.Windows;
using System.Windows.Controls;

namespace PythonSwitcher;

/// <summary>Kleines Fenster: Eintrag festhalten, mit optionalem Grund.</summary>
public sealed class HoldDialog : Window
{
    private readonly TextBox _reason = new() { Margin = new Thickness(0, 6, 0, 12), MinWidth = 360 };

    public string Reason => _reason.Text.Trim();

    public HoldDialog(string entry, string? version)
    {
        Title = "Hold this version";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var ok = new Button { Content = "Hold", IsDefault = true, FontWeight = FontWeights.SemiBold };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 0, 8) };

        _reason.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Reason");
        Content = new StackPanel
        {
            Margin = new Thickness(14),
            Children =
            {
                new TextBlock
                {
                    Text = $"Hold {entry} at version {version ?? "(installed)"}.\n\n" +
                           "Held entries are not offered as updates and are left out of the summary command until you release them.",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 420,
                },
                new TextBlock { Text = "Reason (optional), e.g. \"torch 2.3 needs numpy 1.x\":", Margin = new Thickness(0, 12, 0, 0) },
                _reason,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        Loaded += (_, _) => _reason.Focus();
    }
}
