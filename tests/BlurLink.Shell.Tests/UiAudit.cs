using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace BlurLink.Shell.Tests;

public static class UiAudit
{
    /// <summary>Accessibility floor: every button named, every text input labelled,
    /// every interactive control tab-stoppable. Returns human-readable violations.</summary>
    public static IReadOnlyList<string> Audit(Visual root)
    {
        var violations = new List<string>();
        foreach (var el in root.GetVisualDescendants().OfType<Control>())
        {
            switch (el)
            {
                case Button b when string.IsNullOrWhiteSpace(AutomationProperties.GetName(b)):
                    violations.Add($"Button without AutomationProperties.Name (content: '{(b.Content as string) ?? b.Content?.GetType().Name}')");
                    break;
                case TextBox t when string.IsNullOrWhiteSpace(AutomationProperties.GetName(t))
                    && string.IsNullOrWhiteSpace(t.PlaceholderText):
                    violations.Add("TextBox without Name or PlaceholderText");
                    break;
                case ComboBox c when string.IsNullOrWhiteSpace(AutomationProperties.GetName(c)):
                    violations.Add("ComboBox without AutomationProperties.Name");
                    break;
            }

            if (el is Button or TextBox or ComboBox or ListBoxItem or MenuItem && !el.Focusable)
            {
                violations.Add($"{el.GetType().Name} is not focusable");
            }
        }

        return violations;
    }
}
