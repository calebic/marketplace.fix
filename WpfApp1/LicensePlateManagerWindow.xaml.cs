using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace WpfApp1;

public partial class LicensePlateManagerWindow : Window
{
    private readonly IReadOnlyList<VehicleConfigItem> _allConfigs;
    private readonly VehicleConfigItem? _selected;
    private readonly ObservableCollection<PlatePreviewItem> _previewItems = new();
    private readonly List<PlateModChoiceItem> _modChoices = new();
    private readonly List<PlateConfigChoiceItem> _configChoices = new();

    public LicensePlateManagerWindow(IReadOnlyList<VehicleConfigItem> allConfigs, VehicleConfigItem? selected, string initialScope)
    {
        InitializeComponent();
        _allConfigs = (allConfigs ?? Array.Empty<VehicleConfigItem>())
            .Where(x => x != null)
            .OrderBy(x => x.ModName)
            .ThenBy(x => x.ConfigKey)
            .ToList();
        _selected = selected;

        PreviewDataGrid.ItemsSource = _previewItems;

        BuildSelectionSources();
        ConfigureInitialState(initialScope);
        RefreshScopeSelectors();
        RefreshPreview();
    }

    public string SelectedScope
    {
        get
        {
            if (ConfigScopeRadioButton?.IsChecked == true)
            {
                return "Config";
            }

            if (ModScopeRadioButton?.IsChecked == true)
            {
                return "Mod";
            }

            return "Workspace";
        }
    }

    public string SelectedScopeDisplayLabel
    {
        get
        {
            return SelectedScope switch
            {
                "Config" => "Selected config(s)",
                "Mod" => GetSelectedModChoice()?.DisplayLabel ?? "Selected mod",
                _ => "Everything"
            };
        }
    }

    public bool ClearMode => ClearPlateRadioButton?.IsChecked == true;
    public string PlateText => PlateTextBox?.Text?.Trim() ?? string.Empty;
    public IReadOnlyList<VehicleConfigItem> ActionableItems => ResolveActionableItems().ToList();

    private void BuildSelectionSources()
    {
        _modChoices.Clear();
        _modChoices.AddRange(_allConfigs
            .GroupBy(x => x.SourcePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PlateModChoiceItem
            {
                SourcePath = g.Key,
                ModName = g.FirstOrDefault()?.ModName ?? "Unknown mod",
                ConfigCount = g.Count(),
                DisplayLabel = $"{(g.FirstOrDefault()?.ModName ?? "Unknown mod")} ({g.Count()} config{(g.Count() == 1 ? string.Empty : "s")})"
            })
            .OrderBy(x => x.ModName, StringComparer.OrdinalIgnoreCase));

        _configChoices.Clear();
        _configChoices.AddRange(_allConfigs.Select(item => new PlateConfigChoiceItem
        {
            Item = item,
            DisplayLabel = $"{item.ModName} / {item.ConfigKey}"
        }));

        ModPickerComboBox.ItemsSource = _modChoices;
        ConfigPickerListBox.ItemsSource = _configChoices;
    }

    private void ConfigureInitialState(string initialScope)
    {
        if (_selected != null)
        {
            var modChoice = _modChoices.FirstOrDefault(x => string.Equals(x.SourcePath, _selected.SourcePath, StringComparison.OrdinalIgnoreCase));
            if (modChoice != null)
            {
                ModPickerComboBox.SelectedItem = modChoice;
            }

            var configChoice = _configChoices.FirstOrDefault(x => ReferenceEquals(x.Item, _selected));
            if (configChoice != null)
            {
                ConfigPickerListBox.SelectedItems.Add(configChoice);
            }
        }

        if (string.Equals(initialScope, "Config", StringComparison.OrdinalIgnoreCase))
        {
            ConfigScopeRadioButton.IsChecked = true;
        }
        else if (string.Equals(initialScope, "Mod", StringComparison.OrdinalIgnoreCase))
        {
            ModScopeRadioButton.IsChecked = true;
        }
        else
        {
            WorkspaceScopeRadioButton.IsChecked = true;
        }

        if (ConfigPickerListBox.SelectedItems.Count == 0 && _configChoices.Count > 0 && ConfigScopeRadioButton.IsChecked == true)
        {
            ConfigPickerListBox.SelectedItem = _configChoices[0];
        }

        if (ModPickerComboBox.SelectedItem == null && _modChoices.Count > 0)
        {
            ModPickerComboBox.SelectedIndex = 0;
        }
    }

    private PlateModChoiceItem? GetSelectedModChoice() => ModPickerComboBox.SelectedItem as PlateModChoiceItem;

    private IEnumerable<VehicleConfigItem> ResolveTargetItems()
    {
        if (ConfigScopeRadioButton?.IsChecked == true)
        {
            return ConfigPickerListBox.SelectedItems.Cast<PlateConfigChoiceItem>().Select(x => x.Item).Distinct();
        }

        if (ModScopeRadioButton?.IsChecked == true)
        {
            var selectedMod = GetSelectedModChoice();
            if (selectedMod == null)
            {
                return Enumerable.Empty<VehicleConfigItem>();
            }

            return _allConfigs.Where(x => string.Equals(x.SourcePath, selectedMod.SourcePath, StringComparison.OrdinalIgnoreCase));
        }

        return _allConfigs;
    }

    private IEnumerable<VehicleConfigItem> ResolveActionableItems()
    {
        var plateText = PlateText;
        foreach (var item in ResolveTargetItems())
        {
            if (!item.HasConfigPc)
            {
                continue;
            }

            if (ClearMode)
            {
                if (item.HasHardcodedPlate)
                {
                    yield return item;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(plateText))
            {
                continue;
            }

            if (!string.Equals(item.CurrentLicensePlate ?? string.Empty, plateText, StringComparison.Ordinal))
            {
                yield return item;
            }
        }
    }

    private void RefreshScopeSelectors()
    {
        var isMod = ModScopeRadioButton?.IsChecked == true;
        var isConfig = ConfigScopeRadioButton?.IsChecked == true;

        ModSelectionPanel.Visibility = isMod ? Visibility.Visible : Visibility.Collapsed;
        ConfigSelectionPanel.Visibility = isConfig ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshPreview()
    {
        if (PreviewSummaryTextBlock == null || FooterStatusTextBlock == null || ApplyButton == null)
        {
            return;
        }

        RefreshScopeSelectors();

        _previewItems.Clear();
        var plateText = PlateText;
        var targetItems = ResolveTargetItems().Where(x => x != null).OrderBy(x => x.ModName).ThenBy(x => x.ConfigKey).ToList();
        var actionableCount = 0;
        var missingPcCount = 0;
        var alreadyOkayCount = 0;

        foreach (var item in targetItems)
        {
            var currentPlate = string.IsNullOrWhiteSpace(item.CurrentLicensePlate) ? "(blank / dynamic)" : item.CurrentLicensePlate!;
            var resultPlate = ClearMode ? "(blank / dynamic)" : (string.IsNullOrWhiteSpace(plateText) ? "(enter plate text)" : plateText);
            string status;

            if (!item.HasConfigPc)
            {
                status = "No .pc file found";
                missingPcCount++;
            }
            else if (ClearMode)
            {
                if (item.HasHardcodedPlate)
                {
                    status = "Will remove hardcoded plate";
                    actionableCount++;
                }
                else
                {
                    status = "Already blank / dynamic";
                    alreadyOkayCount++;
                }
            }
            else if (string.IsNullOrWhiteSpace(plateText))
            {
                status = "Enter plate text to apply";
            }
            else if (string.Equals(item.CurrentLicensePlate ?? string.Empty, plateText, StringComparison.Ordinal))
            {
                status = "Already set";
                alreadyOkayCount++;
            }
            else
            {
                status = item.HasHardcodedPlate ? "Will replace hardcoded plate" : "Will add hardcoded plate";
                actionableCount++;
            }

            _previewItems.Add(new PlatePreviewItem
            {
                ModName = item.ModName,
                ConfigKey = item.ConfigKey,
                CurrentPlateDisplay = currentPlate,
                ResultPlateDisplay = resultPlate,
                Status = status
            });
        }

        var selectionProblem = SelectedScope switch
        {
            "Mod" when GetSelectedModChoice() == null => "Choose a mod before applying.",
            "Config" when ConfigPickerListBox.SelectedItems.Count == 0 => "Choose at least one config before applying.",
            _ => string.Empty
        };

        var scopeLabel = SelectedScope switch
        {
            "Config" => "the selected config list",
            "Mod" => GetSelectedModChoice()?.DisplayLabel ?? "the selected mod",
            _ => "everything in the workspace"
        };

        PreviewSummaryTextBlock.Text = string.IsNullOrWhiteSpace(selectionProblem)
            ? $"You are editing {scopeLabel}. {targetItems.Count} config(s) are in range. {actionableCount} change(s) are ready. {alreadyOkayCount} already match. {missingPcCount} do not have a .pc file available."
            : selectionProblem;

        FooterStatusTextBlock.Text = ClearMode
            ? "Remove mode deletes the hardcoded licenseName entry from each selected .pc file."
            : "Set mode writes the same licenseName text to each selected .pc file.";

        ApplyButton.IsEnabled = string.IsNullOrWhiteSpace(selectionProblem) && actionableCount > 0 && (ClearMode || !string.IsNullOrWhiteSpace(plateText));
        PlateTextBox.IsEnabled = !ClearMode;
    }

    private void ScopeRadioButton_Checked(object sender, RoutedEventArgs e) => RefreshPreview();
    private void ModeRadioButton_Checked(object sender, RoutedEventArgs e) => RefreshPreview();
    private void PlateTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        MainWindowPlateTextLimitHelper.Enforce(PlateTextBox);
        RefreshPreview();
    }
    private void ModPickerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPreview();
    private void ConfigPickerListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPreview();

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyButton.IsEnabled)
        {
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}


public static class MainWindowPlateTextLimitHelper
{
    public static void Enforce(System.Windows.Controls.TextBox? textBox)
    {
        if (textBox is null) return;

        const int maxPlateLength = 10;
        var text = textBox.Text ?? string.Empty;
        if (text.Length <= maxPlateLength) return;

        var caret = textBox.CaretIndex;
        textBox.Text = text.Substring(0, maxPlateLength);
        textBox.CaretIndex = Math.Min(caret, maxPlateLength);
    }
}

public sealed class PlatePreviewItem
{
    public string ModName { get; init; } = string.Empty;
    public string ConfigKey { get; init; } = string.Empty;
    public string CurrentPlateDisplay { get; init; } = string.Empty;
    public string ResultPlateDisplay { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}

public sealed class PlateModChoiceItem
{
    public string SourcePath { get; init; } = string.Empty;
    public string ModName { get; init; } = string.Empty;
    public int ConfigCount { get; init; }
    public string DisplayLabel { get; init; } = string.Empty;

    public override string ToString() => string.IsNullOrWhiteSpace(DisplayLabel) ? ModName : DisplayLabel;
}

public sealed class PlateConfigChoiceItem
{
    public VehicleConfigItem Item { get; init; } = null!;
    public string DisplayLabel { get; init; } = string.Empty;

    public override string ToString() => DisplayLabel;
}
