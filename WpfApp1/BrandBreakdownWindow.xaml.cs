using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;

namespace WpfApp1;

public sealed class BrandSourceBreakdownItem
{
    public string DisplayName { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public int ConfigCount { get; init; }
    public int ReviewCount { get; init; }
    public IReadOnlyList<string> ConfigNames { get; init; } = Array.Empty<string>();
    public string ConfigNamesText => ConfigNames.Count == 0 ? "No configs listed" : string.Join(Environment.NewLine, ConfigNames);
}

public partial class BrandBreakdownWindow : MetroWindow
{
    public ObservableCollection<BrandSourceBreakdownItem> Sources { get; } = new();

    public BrandBreakdownWindow(string brand, IReadOnlyList<BrandSourceBreakdownItem> sources)
    {
        InitializeComponent();
        DataContext = this;

        HeaderTitleTextBlock.Text = $"{brand} breakdown";
        HeaderSummaryTextBlock.Text = sources.Count == 0
            ? "No mod archives are currently associated with this brand."
            : $"Showing {sources.Count} mod(s) currently assigned to {brand}. Select any row to inspect its config list.";
        FooterTextBlock.Text = sources.Count == 0
            ? "No matching mods found."
            : $"{sources.Sum(x => x.ConfigCount)} config(s) across {sources.Count} mod(s).";

        foreach (var source in sources)
        {
            Sources.Add(source);
        }

        SourcesGrid.ItemsSource = Sources;
        if (Sources.Count > 0)
        {
            SourcesGrid.SelectedIndex = 0;
        }
    }

    private void SourcesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourcesGrid.SelectedItem is BrandSourceBreakdownItem item)
        {
            FooterTextBlock.Text = $"{item.DisplayName} · {item.ConfigCount} config(s)" +
                                   (item.ReviewCount > 0 ? $" · {item.ReviewCount} flagged" : string.Empty);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
