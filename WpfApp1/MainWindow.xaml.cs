using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using WpfApplication = System.Windows.Application;
using WpfControl = System.Windows.Controls.Control;
using WpfTextBox = System.Windows.Controls.TextBox;
using WinForms = System.Windows.Forms;

namespace WpfApp1;

public partial class MainWindow : Window
{
    private const string DefaultModsPath = @"D:\beamng progress\30\current\mods";
    private VehicleConfigItem? _selected;
    private ICollectionView? _configsView;
    private readonly AutoFillSettings _autoFillSettings = AutoFillSettings.CreateDefaults();
    private bool _hasUnsavedChanges;

    public ObservableCollection<VehicleConfigItem> Configs { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        if (Directory.Exists(DefaultModsPath))
        {
            ModsPathTextBox.Text = DefaultModsPath;
        }
        StatusTextBlock.Text = "Select a mods folder and click Scan.";
        SetupConfigsView();
        LoadAutoFillSettingsIntoUi();
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
            {
                Opacity = 1;
            }
        };
    }

    private void BrowseMods_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Select your BeamNG mods folder",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(ModsPathTextBox.Text) ? ModsPathTextBox.Text : string.Empty
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            ModsPathTextBox.Text = dialog.SelectedPath;
        }
    }

    private void ScanMods_Click(object sender, RoutedEventArgs e)
    {
        var modsPath = ModsPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath))
        {
            System.Windows.MessageBox.Show("Please select a valid mods folder.", "Scan Mods",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LoadConfigs(modsPath);
    }

    private void ConfigsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        
        if (ConfigsGrid.SelectedItem is not VehicleConfigItem item)
        {
            _selected = null;
            ClearForm();
            return;
        }
        _selected = item;
        SetDirty(false);
        LoadForm(item);
    }

    private void AutoFillMissing_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            System.Windows.MessageBox.Show("Select a config to edit first.", "Auto-Fill Missing",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var defaultYear = _autoFillSettings.Year ?? DateTime.Now.Year;

        if (_autoFillSettings.UseBrand && string.IsNullOrWhiteSpace(BrandTextBox.Text)) BrandTextBox.Text = _autoFillSettings.Brand;
        if (_autoFillSettings.UseCountry && string.IsNullOrWhiteSpace(CountryTextBox.Text)) CountryTextBox.Text = _autoFillSettings.Country;
        if (_autoFillSettings.UseType && string.IsNullOrWhiteSpace(TypeTextBox.Text)) TypeTextBox.Text = _autoFillSettings.Type;
        if (_autoFillSettings.UseBodyStyle && string.IsNullOrWhiteSpace(BodyStyleTextBox.Text)) BodyStyleTextBox.Text = _autoFillSettings.BodyStyle;
        if (_autoFillSettings.UseConfigType && string.IsNullOrWhiteSpace(ConfigTypeTextBox.Text)) ConfigTypeTextBox.Text = _autoFillSettings.ConfigType;

        if (_autoFillSettings.UseYear)
        {
            if (string.IsNullOrWhiteSpace(YearMinTextBox.Text)) YearMinTextBox.Text = defaultYear.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(YearMaxTextBox.Text)) YearMaxTextBox.Text = defaultYear.ToString(CultureInfo.InvariantCulture);
        }

        if (_autoFillSettings.UseValue && string.IsNullOrWhiteSpace(ValueTextBox.Text))
        {
            ValueTextBox.Text = _autoFillSettings.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (_autoFillSettings.UsePopulation && string.IsNullOrWhiteSpace(PopulationTextBox.Text))
        {
            PopulationTextBox.Text = _autoFillSettings.Population.ToString(CultureInfo.InvariantCulture);
        }

        UpdateMissingFromForm();
        SetDirty(true);
    }

    private void SaveChanges_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            System.Windows.MessageBox.Show("Select a config to edit first.", "Save Changes",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryBuildUpdatedConfig(out var updated, out var error))
        {
            System.Windows.MessageBox.Show(error, "Save Changes", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _selected.Json = updated;
        UpdateItemFromJson(_selected, updated);

        var json = updated.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        try
        {
            WriteConfig(_selected, json);
            _selected.NotifyChanges();
            LoadForm(_selected);
            SetDirty(false);
            StatusTextBlock.Text = $"Saved {_selected.ConfigKey} ({_selected.ModName}).";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to save config: {ex.Message}", "Save Changes",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReloadSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            System.Windows.MessageBox.Show("Select a config to reload first.", "Reload",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var jsonText = ReadConfigText(_selected);
            var root = ParseJson(jsonText);
            if (root == null)
            {
                System.Windows.MessageBox.Show("Failed to parse JSON for the selected config.", "Reload",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _selected.Json = root;
            UpdateItemFromJson(_selected, root);
            _selected.NotifyChanges();
            LoadForm(_selected);
            SetDirty(false);
            StatusTextBlock.Text = $"Reloaded {_selected.ConfigKey} ({_selected.ModName}).";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to reload config: {ex.Message}", "Reload",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadConfigs(string modsPath)
    {
        Configs.Clear();
        _selected = null;
        ClearForm();

        var added = 0;
        var errors = 0;

        foreach (var modDir in Directory.EnumerateDirectories(modsPath))
        {
            var dirName = Path.GetFileName(modDir);
            if (string.Equals(dirName, "unpacked", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            added += LoadFolderMod(modDir, ref errors);
        }

        var unpackedRoot = Path.Combine(modsPath, "unpacked");
        if (Directory.Exists(unpackedRoot))
        {
            foreach (var modDir in Directory.EnumerateDirectories(unpackedRoot))
            {
                added += LoadFolderMod(modDir, ref errors);
            }
        }

        foreach (var zipPath in Directory.EnumerateFiles(modsPath, "*.zip"))
        {
            added += LoadZipMod(zipPath, ref errors);
        }

        SetupConfigsView();
        StatusTextBlock.Text = $"Loaded {added} configs. Errors: {errors}.";
    }

    private void SetupConfigsView()
    {
        _configsView = CollectionViewSource.GetDefaultView(Configs);
        _configsView.Filter = ConfigFilter;
        ApplyGrouping();
        _configsView.Refresh();
        UpdateEmptyState();
    }

    private bool ConfigFilter(object obj)
    {
        if (obj is not VehicleConfigItem item)
        {
            return false;
        }

        if (MissingOnlyCheckBox?.IsChecked == true && !item.HasMissing)
        {
            return false;
        }

        var query = SearchTextBox?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.ToLowerInvariant();
            if (!(item.ModName.ToLowerInvariant().Contains(q) ||
                  item.ModelKey.ToLowerInvariant().Contains(q) ||
                  item.ConfigKey.ToLowerInvariant().Contains(q) ||
                  (item.VehicleName?.ToLowerInvariant().Contains(q) ?? false)))
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyGrouping()
    {
        if (_configsView == null)
        {
            return;
        }

        _configsView.GroupDescriptions?.Clear();
        if (GroupByModCheckBox?.IsChecked == true)
        {
            _configsView.GroupDescriptions?.Add(new PropertyGroupDescription(nameof(VehicleConfigItem.ModName)));
        }
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _configsView?.Refresh();
        UpdateEmptyState();
    }

    private void FilterControl_Changed(object sender, RoutedEventArgs e)
    {
        _configsView?.Refresh();
        UpdateEmptyState();
    }

    private void GroupByModCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        ApplyGrouping();
        _configsView?.Refresh();
        UpdateEmptyState();
    }

    private void SortControl_Changed(object sender, RoutedEventArgs e)
    {
        ApplySorting();
        _configsView?.Refresh();
    }

    private void UpdateEmptyState()
    {
        if (_configsView == null || EmptyStateTextBlock == null)
        {
            return;
        }

        EmptyStateTextBlock.Visibility = _configsView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplySorting()
    {
        if (_configsView == null)
        {
            return;
        }

        _configsView.SortDescriptions.Clear();
        if (MissingFirstCheckBox?.IsChecked == true)
        {
            _configsView.SortDescriptions.Add(new SortDescription(nameof(VehicleConfigItem.HasMissing), ListSortDirection.Descending));
        }

        _configsView.SortDescriptions.Add(new SortDescription(nameof(VehicleConfigItem.VehicleName), ListSortDirection.Ascending));
    }

    private int LoadFolderMod(string modDir, ref int errors)
    {
        var count = 0;
        var modName = Path.GetFileName(modDir);
        foreach (var filePath in Directory.EnumerateFiles(modDir, "info_*.json", SearchOption.AllDirectories))
        {
            if (!IsVehicleInfoPath(filePath))
            {
                continue;
            }

            try
            {
                var jsonText = File.ReadAllText(filePath);
                if (!TryCreateConfigItem(modName, modDir, filePath, false, jsonText, out var item))
                {
                    errors++;
                    continue;
                }

                Configs.Add(item);
                count++;
            }
            catch
            {
                errors++;
            }
        }

        return count;
    }

    private int LoadZipMod(string zipPath, ref int errors)
    {
        var count = 0;
        var modName = Path.GetFileNameWithoutExtension(zipPath);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (!IsVehicleInfoEntry(entry.FullName))
                {
                    continue;
                }

                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                var jsonText = reader.ReadToEnd();
                if (!TryCreateConfigItem(modName, zipPath, entry.FullName, true, jsonText, out var item))
                {
                    errors++;
                    continue;
                }

                Configs.Add(item);
                count++;
            }
        }
        catch
        {
            errors++;
        }

        return count;
    }

    private static bool TryCreateConfigItem(string modName, string sourcePath, string infoPath, bool isZip, string jsonText, out VehicleConfigItem item)
    {
        item = new VehicleConfigItem();

        if (!TryExtractModelAndConfig(infoPath, isZip, out var modelKey, out var configKey))
        {
            return false;
        }

        var root = ParseJson(jsonText);
        if (root == null)
        {
            return false;
        }

        item = new VehicleConfigItem
        {
            ModName = modName,
            SourcePath = sourcePath,
            InfoPath = infoPath,
            IsZip = isZip,
            ModelKey = modelKey,
            ConfigKey = configKey,
            Json = root
        };

        UpdateItemFromJson(item, root);
        return true;
    }

    private static JsonNode? ParseJson(string jsonText)
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        try
        {
            return JsonNode.Parse(jsonText, documentOptions: options);
        }
        catch
        {
            return null;
        }
    }

    private void LoadForm(VehicleConfigItem item)
    {
        ConfigPathText.Text = item.IsZip ? $"{item.SourcePath} :: {item.InfoPath}" : item.InfoPath;
        BrandTextBox.Text = item.Brand ?? string.Empty;
        CountryTextBox.Text = item.Country ?? string.Empty;
        TypeTextBox.Text = item.Type ?? string.Empty;
        BodyStyleTextBox.Text = item.BodyStyle ?? string.Empty;
        ConfigTypeTextBox.Text = item.ConfigType ?? string.Empty;
        ConfigurationTextBox.Text = item.Configuration ?? string.Empty;
        YearMinTextBox.Text = item.YearMin?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        YearMaxTextBox.Text = item.YearMax?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ValueTextBox.Text = item.Value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        PopulationTextBox.Text = item.Population?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        PartsAuditTextBox.Text = string.Empty;

        var missing = item.GetMissingFields();
        
        UpdateFieldHighlightingFromMissing(missing);
        UpdateSummary(item);
    }

    private void ClearForm()
    {
        ConfigPathText.Text = string.Empty;
        BrandTextBox.Text = string.Empty;
        CountryTextBox.Text = string.Empty;
        TypeTextBox.Text = string.Empty;
        BodyStyleTextBox.Text = string.Empty;
        ConfigTypeTextBox.Text = string.Empty;
        ConfigurationTextBox.Text = string.Empty;
        YearMinTextBox.Text = string.Empty;
        YearMaxTextBox.Text = string.Empty;
        ValueTextBox.Text = string.Empty;
        PopulationTextBox.Text = string.Empty;
        
        PartsAuditTextBox.Text = string.Empty;
        PopulationPresetComboBox.SelectedIndex = 0;
        ConfigSummaryText.Text = string.Empty;
        ResetFieldHighlighting();
        SetDirty(false);
    }

    private bool TryBuildUpdatedConfig(out JsonObject updated, out string error)
    {
        updated = new JsonObject();
        error = string.Empty;

        if (_selected?.Json is not JsonObject root)
        {
            error = "Selected config does not contain a JSON object.";
            return false;
        }

        var brand = BrandTextBox.Text.Trim();
        var country = CountryTextBox.Text.Trim();
        var type = TypeTextBox.Text.Trim();
        var bodyStyle = BodyStyleTextBox.Text.Trim();
        var configType = ConfigTypeTextBox.Text.Trim();
        var configuration = ConfigurationTextBox.Text.Trim();

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(brand)) errors.Add("Brand");
        if (string.IsNullOrWhiteSpace(country)) errors.Add("Country");
        if (string.IsNullOrWhiteSpace(type)) errors.Add("Type");
        if (string.IsNullOrWhiteSpace(bodyStyle)) errors.Add("Body Style");
        if (string.IsNullOrWhiteSpace(configType)) errors.Add("Config Type");
        if (string.IsNullOrWhiteSpace(configuration)) errors.Add("Configuration");

        if (!int.TryParse(YearMinTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var yearMin))
        {
            errors.Add("Year Min");
        }

        if (!int.TryParse(YearMaxTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var yearMax))
        {
            errors.Add("Year Max");
        }

        if (errors.Count == 0 && yearMin > yearMax)
        {
            errors.Add("Year Min must be <= Year Max");
        }

        if (!double.TryParse(ValueTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            errors.Add("Value");
        }

        if (errors.Count > 0)
        {
            error = "Fix the following fields:\n- " + string.Join("\n- ", errors);
            return false;
        }

        updated = root;
        updated["Brand"] = brand;
        updated["Country"] = country;
        updated["Type"] = type;
        updated["Body Style"] = bodyStyle;
        updated["Config Type"] = configType;
        updated["Configuration"] = configuration;
        updated["Years"] = new JsonObject
        {
            ["min"] = yearMin,
            ["max"] = yearMax
        };
        updated["Value"] = value;

        if (int.TryParse(PopulationTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var population))
        {
            updated["Population"] = population;
        }
        else
        {
            updated.Remove("Population");
        }

        return true;
    }

    private static void UpdateItemFromJson(VehicleConfigItem item, JsonNode root)
    {
        item.VehicleName = ReadString(root, "Name") ?? ReadString(root, "Configuration") ?? item.ConfigKey;
        item.Brand = ReadStringOrAggregate(root, "Brand");
        item.Country = ReadStringOrAggregate(root, "Country");
        item.Type = ReadStringOrAggregate(root, "Type");
        item.BodyStyle = ReadStringOrAggregate(root, "Body Style");
        item.ConfigType = ReadStringOrAggregate(root, "Config Type");
        item.Configuration = ReadString(root, "Configuration");

        var years = ReadYears(root);
        item.YearMin = years.min;
        item.YearMax = years.max;

        item.Value = ReadDouble(root, "Value");
        item.Population = ReadInt(root, "Population");
    }

    private void UpdateMissingFromForm()
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(BrandTextBox.Text)) missing.Add("Brand");
        if (string.IsNullOrWhiteSpace(CountryTextBox.Text)) missing.Add("Country");
        if (string.IsNullOrWhiteSpace(TypeTextBox.Text)) missing.Add("Type");
        if (string.IsNullOrWhiteSpace(BodyStyleTextBox.Text)) missing.Add("Body Style");
        if (string.IsNullOrWhiteSpace(ConfigTypeTextBox.Text)) missing.Add("Config Type");
        if (string.IsNullOrWhiteSpace(ConfigurationTextBox.Text)) missing.Add("Configuration");

        if (!int.TryParse(YearMinTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            missing.Add("Years");
        }

        if (!int.TryParse(YearMaxTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            if (!missing.Contains("Years")) missing.Add("Years");
        }

        if (!double.TryParse(ValueTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            missing.Add("Value");
        }

        if (!int.TryParse(PopulationTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            missing.Add("Population");
        }

        
        if (_selected != null)
        {
            UpdateFieldHighlightingFromMissing(missing);
            UpdateSummaryFromForm(missing);
        }
    }

    private void UpdateSummaryFromForm(IReadOnlyCollection<string> missing)
    {
        var missingText = missing.Count == 0 ? "OK" : $"{missing.Count} missing";
        var populationText = int.TryParse(PopulationTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pop)
            ? pop.ToString(CultureInfo.InvariantCulture)
            : "Missing";
        var valueText = double.TryParse(ValueTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var val)
            ? val.ToString("0", CultureInfo.InvariantCulture)
            : "Missing";

        ConfigSummaryText.Text = $"Missing: {missingText}  •  Population: {populationText}  •  Value: {valueText}";
    }

    private void UpdateSummary(VehicleConfigItem item)
    {
        var missing = item.GetMissingFields();
        var missingText = missing.Count == 0 ? "OK" : $"{missing.Count} missing";
        var populationText = item.Population.HasValue ? item.Population.Value.ToString(CultureInfo.InvariantCulture) : "Missing";
        var valueText = item.Value.HasValue ? item.Value.Value.ToString("0", CultureInfo.InvariantCulture) : "Missing";

        ConfigSummaryText.Text = $"Missing: {missingText}  •  Population: {populationText}  •  Value: {valueText}";
    }

    private void PopulationPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PopulationPresetComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var preset = item.Content?.ToString() ?? string.Empty;
        switch (preset)
        {
            case "Ultra-rare (1-50)":
                PopulationTextBox.Text = "25";
                break;
            case "Rare (50-200)":
                PopulationTextBox.Text = "100";
                break;
            case "Uncommon (200-800)":
                PopulationTextBox.Text = "400";
                break;
            case "Common (800-3000)":
                PopulationTextBox.Text = "1500";
                break;
            case "Very common (3000-10000)":
                PopulationTextBox.Text = "6000";
                break;
            default:
                return;
        }

        UpdateMissingFromForm();
        if (_selected != null)
        {
            UpdateMissingFromForm();
        }
        SetDirty(true);
    }


    private void AutoFillSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        LoadAutoFillSettingsIntoUi();
        AutoFillSettingsPopup.IsOpen = true;
    }

    private void AutoFillSettingsSave_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();

        var year = _autoFillSettings.Year ?? DateTime.Now.Year;
        var value = _autoFillSettings.Value;
        var population = _autoFillSettings.Population;

        if (AutoYearCheckBox.IsChecked == true &&
            !int.TryParse(AutoYearTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out year))
        {
            errors.Add("Default Year");
        }

        if (AutoValueCheckBox.IsChecked == true &&
            !double.TryParse(AutoValueTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            errors.Add("Value");
        }

        if (AutoPopulationCheckBox.IsChecked == true &&
            !int.TryParse(AutoPopulationTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out population))
        {
            errors.Add("Population");
        }

        if (errors.Count > 0)
        {
            System.Windows.MessageBox.Show("Fix the following fields:\n- " + string.Join("\n- ", errors),
                "Auto-fill settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _autoFillSettings.Brand = AutoBrandTextBox.Text.Trim();
        _autoFillSettings.Country = AutoCountryTextBox.Text.Trim();
        _autoFillSettings.Type = AutoTypeTextBox.Text.Trim();
        _autoFillSettings.BodyStyle = AutoBodyStyleTextBox.Text.Trim();
        _autoFillSettings.ConfigType = AutoConfigTypeTextBox.Text.Trim();
        _autoFillSettings.Year = year;
        _autoFillSettings.Value = value;
        _autoFillSettings.Population = population;
        _autoFillSettings.UseBrand = AutoBrandCheckBox.IsChecked == true;
        _autoFillSettings.UseCountry = AutoCountryCheckBox.IsChecked == true;
        _autoFillSettings.UseType = AutoTypeCheckBox.IsChecked == true;
        _autoFillSettings.UseBodyStyle = AutoBodyStyleCheckBox.IsChecked == true;
        _autoFillSettings.UseConfigType = AutoConfigTypeCheckBox.IsChecked == true;
        _autoFillSettings.UseYear = AutoYearCheckBox.IsChecked == true;
        _autoFillSettings.UseValue = AutoValueCheckBox.IsChecked == true;
        _autoFillSettings.UsePopulation = AutoPopulationCheckBox.IsChecked == true;

        AutoFillSettingsPopup.IsOpen = false;
    }

    private void FieldTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateMissingFromForm();
        SetDirty(true);
    }

    private void MissingFieldsText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_selected == null)
        {
            return;
        }

        var missing = _selected.GetMissingFields();
        if (missing.Count == 0)
        {
            return;
        }

        if (missing.Contains("Brand")) { FocusField(BrandTextBox); return; }
        if (missing.Contains("Country")) { FocusField(CountryTextBox); return; }
        if (missing.Contains("Type")) { FocusField(TypeTextBox); return; }
        if (missing.Contains("Body Style")) { FocusField(BodyStyleTextBox); return; }
        if (missing.Contains("Config Type")) { FocusField(ConfigTypeTextBox); return; }
        if (missing.Contains("Configuration")) { FocusField(ConfigurationTextBox); return; }
        if (missing.Contains("Years")) { FocusField(YearMinTextBox); return; }
        if (missing.Contains("Value")) { FocusField(ValueTextBox); return; }
        if (missing.Contains("Population")) { FocusField(PopulationTextBox); return; }
    }

    private static void FocusField(WpfControl control)
    {
        control.Focus();
        control.BringIntoView();
    }

    private void LoadAutoFillSettingsIntoUi()
    {
        AutoBrandTextBox.Text = _autoFillSettings.Brand;
        AutoCountryTextBox.Text = _autoFillSettings.Country;
        AutoTypeTextBox.Text = _autoFillSettings.Type;
        AutoBodyStyleTextBox.Text = _autoFillSettings.BodyStyle;
        AutoConfigTypeTextBox.Text = _autoFillSettings.ConfigType;
        AutoYearTextBox.Text = (_autoFillSettings.Year ?? DateTime.Now.Year).ToString(CultureInfo.InvariantCulture);
        AutoValueTextBox.Text = _autoFillSettings.Value.ToString(CultureInfo.InvariantCulture);
        AutoPopulationTextBox.Text = _autoFillSettings.Population.ToString(CultureInfo.InvariantCulture);
        AutoBrandCheckBox.IsChecked = _autoFillSettings.UseBrand;
        AutoCountryCheckBox.IsChecked = _autoFillSettings.UseCountry;
        AutoTypeCheckBox.IsChecked = _autoFillSettings.UseType;
        AutoBodyStyleCheckBox.IsChecked = _autoFillSettings.UseBodyStyle;
        AutoConfigTypeCheckBox.IsChecked = _autoFillSettings.UseConfigType;
        AutoYearCheckBox.IsChecked = _autoFillSettings.UseYear;
        AutoValueCheckBox.IsChecked = _autoFillSettings.UseValue;
        AutoPopulationCheckBox.IsChecked = _autoFillSettings.UsePopulation;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _ = AnimateMaximizeRestoreAsync();
            return;
        }

        DragMove();
    }

    private async void Minimize_Click(object sender, RoutedEventArgs e)
    {
        await AnimateWindowOpacityAsync(0.7, 120);
        SystemCommands.MinimizeWindow(this);
        Opacity = 1;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private async void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        await AnimateMaximizeRestoreAsync();
    }

    private async Task AnimateMaximizeRestoreAsync()
    {
        await AnimateWindowOpacityAsync(0.85, 120);
        ToggleMaximize();
        await AnimateWindowOpacityAsync(1, 120);
    }

    private Task AnimateWindowOpacityAsync(double targetOpacity, int durationMs)
    {
        var tcs = new TaskCompletionSource<bool>();
        var animation = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };

        animation.Completed += (_, _) =>
        {
            Opacity = targetOpacity;
            tcs.TrySetResult(true);
        };

        BeginAnimation(OpacityProperty, animation);
        return tcs.Task;
    }

    private sealed class AutoFillSettings
    {
        public string Brand { get; set; } = "Custom";
        public string Country { get; set; } = "United States";
        public string Type { get; set; } = "Car";
        public string BodyStyle { get; set; } = "Sedan";
        public string ConfigType { get; set; } = "Custom";
        public int? Year { get; set; } = DateTime.Now.Year;
        public double Value { get; set; } = 5000;
        public int Population { get; set; } = 1000;
        public bool UseBrand { get; set; } = true;
        public bool UseCountry { get; set; } = true;
        public bool UseType { get; set; } = true;
        public bool UseBodyStyle { get; set; } = true;
        public bool UseConfigType { get; set; } = true;
        public bool UseYear { get; set; } = true;
        public bool UseValue { get; set; } = true;
        public bool UsePopulation { get; set; } = true;

        public static AutoFillSettings CreateDefaults() => new();
    }

    private void UpdateFieldHighlightingFromMissing(IReadOnlyCollection<string> missing)
    {
        ResetFieldHighlighting();
        var badBrush = (Brush)(WpfApplication.Current?.TryFindResource("BadText") ?? System.Windows.Media.Brushes.Red);
        var defaultBorder = (Brush)(WpfApplication.Current?.TryFindResource("CardBorder") ?? System.Windows.Media.Brushes.Gray);

        SetFieldHighlight(BrandLabel, BrandTextBox, missing.Contains("Brand"), badBrush, defaultBorder);
        SetFieldHighlight(CountryLabel, CountryTextBox, missing.Contains("Country"), badBrush, defaultBorder);
        SetFieldHighlight(TypeLabel, TypeTextBox, missing.Contains("Type"), badBrush, defaultBorder);
        SetFieldHighlight(BodyStyleLabel, BodyStyleTextBox, missing.Contains("Body Style"), badBrush, defaultBorder);
        SetFieldHighlight(ConfigTypeLabel, ConfigTypeTextBox, missing.Contains("Config Type"), badBrush, defaultBorder);
        SetFieldHighlight(ConfigurationLabel, ConfigurationTextBox, missing.Contains("Configuration"), badBrush, defaultBorder);
        SetFieldHighlight(ValueLabel, ValueTextBox, missing.Contains("Value"), badBrush, defaultBorder);
        SetFieldHighlight(PopulationLabel, PopulationTextBox, missing.Contains("Population"), badBrush, defaultBorder);
        if (missing.Contains("Years"))
        {
            SetFieldHighlight(YearMinLabel, YearMinTextBox, true, badBrush, defaultBorder);
            SetFieldHighlight(YearMaxLabel, YearMaxTextBox, true, badBrush, defaultBorder);
        }
    }

    private void ResetFieldHighlighting()
    {
        var defaultBrush = (Brush)(WpfApplication.Current?.TryFindResource("TextPrimary") ?? System.Windows.Media.Brushes.White);
        var defaultBorder = (Brush)(WpfApplication.Current?.TryFindResource("CardBorder") ?? System.Windows.Media.Brushes.Gray);

        BrandLabel.Foreground = defaultBrush;
        CountryLabel.Foreground = defaultBrush;
        TypeLabel.Foreground = defaultBrush;
        BodyStyleLabel.Foreground = defaultBrush;
        ConfigTypeLabel.Foreground = defaultBrush;
        ConfigurationLabel.Foreground = defaultBrush;
        YearMinLabel.Foreground = defaultBrush;
        YearMaxLabel.Foreground = defaultBrush;
        ValueLabel.Foreground = defaultBrush;
        PopulationLabel.Foreground = defaultBrush;
        ResetFieldBorder(BrandTextBox, defaultBorder);
        ResetFieldBorder(CountryTextBox, defaultBorder);
        ResetFieldBorder(TypeTextBox, defaultBorder);
        ResetFieldBorder(BodyStyleTextBox, defaultBorder);
        ResetFieldBorder(ConfigTypeTextBox, defaultBorder);
        ResetFieldBorder(ConfigurationTextBox, defaultBorder);
        ResetFieldBorder(YearMinTextBox, defaultBorder);
        ResetFieldBorder(YearMaxTextBox, defaultBorder);
        ResetFieldBorder(ValueTextBox, defaultBorder);
        ResetFieldBorder(PopulationTextBox, defaultBorder);
    }

    private static void SetFieldHighlight(TextBlock label, WpfTextBox box, bool missing, Brush badBrush, Brush defaultBorder)
    {
        if (missing)
        {
            label.Foreground = badBrush;
            box.BorderBrush = badBrush;
            box.BorderThickness = new Thickness(1.5);
        }
        else
        {
            label.Foreground = (Brush)(WpfApplication.Current?.TryFindResource("TextPrimary") ?? System.Windows.Media.Brushes.White);
            ResetFieldBorder(box, defaultBorder);
        }
    }

    private static void ResetFieldBorder(WpfTextBox box, Brush defaultBorder)
    {
        box.BorderBrush = defaultBorder;
        box.BorderThickness = new Thickness(1);
    }

    private void SetDirty(bool value)
    {
        _hasUnsavedChanges = value;
        if (UnsavedBadge != null)
        {
            UnsavedBadge.Visibility = _hasUnsavedChanges ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void AuditParts_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            System.Windows.MessageBox.Show("Select a config to audit first.", "Parts Audit",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            PartsAuditTextBox.Text = BuildPartsAudit(_selected);
        }
        catch (Exception ex)
        {
            PartsAuditTextBox.Text = $"Audit failed: {ex.Message}";
        }
    }

    private string BuildPartsAudit(VehicleConfigItem item)
    {
        var pcPath = GetPcPath(item);
        if (string.IsNullOrWhiteSpace(pcPath))
        {
            return "No pcFilename found and default pc path could not be built.";
        }

        var pcText = ReadSourceText(item, pcPath);
        var pcRoot = ParseJson(pcText) as JsonObject;
        if (pcRoot == null)
        {
            return "Failed to parse pc file.";
        }

        if (pcRoot["parts"] is not JsonObject partsObj)
        {
            return "pc file has no parts list.";
        }

        var partNames = new List<string>();
        foreach (var kvp in partsObj)
        {
            if (kvp.Value is JsonValue val && val.TryGetValue<string>(out var partName) && !string.IsNullOrWhiteSpace(partName))
            {
                partNames.Add(partName);
            }
        }

        if (partNames.Count == 0)
        {
            return "pc file has no named parts.";
        }

        var partValueIndex = BuildPartValueIndex(item);
        var missingParts = new List<string>();
        var withValues = new List<(string name, double value)>();

        foreach (var partName in partNames.Distinct())
        {
            if (partValueIndex.TryGetValue(partName, out var value))
            {
                withValues.Add((partName, value));
            }
            else
            {
                missingParts.Add(partName);
            }
        }

        var totalValue = withValues.Sum(x => x.value);
        var topParts = withValues
            .OrderByDescending(x => x.value)
            .Take(5)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Parts listed: {partNames.Count}");
        sb.AppendLine($"Parts with value: {withValues.Count}");
        sb.AppendLine($"Parts missing value: {missingParts.Count}");
        sb.AppendLine($"Total parts value: {totalValue:0}");
        if (item.Value.HasValue)
        {
            sb.AppendLine($"Config Value: {item.Value.Value:0}");
        }

        if (topParts.Count > 0)
        {
            sb.AppendLine("Top parts:");
            foreach (var part in topParts)
            {
                sb.AppendLine($"- {part.name}: {part.value:0}");
            }
        }

        if (missingParts.Count > 0)
        {
            sb.AppendLine("Missing part values (sample):");
            foreach (var part in missingParts.Take(10))
            {
                sb.AppendLine($"- {part}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string? GetPcPath(VehicleConfigItem item)
    {
        if (item.Json is JsonObject root)
        {
            var pcFile = ReadString(root, "pcFilename");
            if (!string.IsNullOrWhiteSpace(pcFile))
            {
                return TrimLeadingSlash(pcFile);
            }
        }

        if (string.IsNullOrWhiteSpace(item.ModelKey) || string.IsNullOrWhiteSpace(item.ConfigKey))
        {
            return null;
        }

        return $"vehicles/{item.ModelKey}/{item.ConfigKey}.pc";
    }

    private static string TrimLeadingSlash(string path)
    {
        return path.StartsWith("/", StringComparison.Ordinal) ? path.Substring(1) : path;
    }

    private string ReadSourceText(VehicleConfigItem item, string relativePath)
    {
        if (item.IsZip)
        {
            using var archive = ZipFile.OpenRead(item.SourcePath);
            var entry = archive.GetEntry(relativePath.Replace('\\', '/'));
            if (entry == null)
            {
                throw new FileNotFoundException("Entry not found in zip.", relativePath);
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        var fullPath = Path.Combine(item.SourcePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.ReadAllText(fullPath);
    }

    private Dictionary<string, double> BuildPartValueIndex(VehicleConfigItem item)
    {
        var index = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var modelPath = $"vehicles/{item.ModelKey}/";

        if (item.IsZip)
        {
            using var archive = ZipFile.OpenRead(item.SourcePath);
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.StartsWith(modelPath, StringComparison.OrdinalIgnoreCase) ||
                    !entry.FullName.EndsWith(".jbeam", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    var text = reader.ReadToEnd();
                    AddPartValuesFromJbeam(text, index);
                }
                catch
                {
                    // Ignore malformed jbeam entries during audit.
                }
            }
        }
        else
        {
            var modelDir = Path.Combine(item.SourcePath, "vehicles", item.ModelKey);
            if (!Directory.Exists(modelDir))
            {
                return index;
            }

            foreach (var file in Directory.EnumerateFiles(modelDir, "*.jbeam", SearchOption.AllDirectories))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    AddPartValuesFromJbeam(text, index);
                }
                catch
                {
                    // Ignore malformed jbeam files during audit.
                }
            }
        }

        return index;
    }

    private static void AddPartValuesFromJbeam(string text, Dictionary<string, double> index)
    {
        var root = ParseJson(text) as JsonObject;
        if (root == null)
        {
            return;
        }

        foreach (var kvp in root)
        {
            if (kvp.Value is not JsonObject partObj)
            {
                continue;
            }

            var info = partObj["information"] as JsonObject;
            if (info == null)
            {
                continue;
            }

            if (info["value"] is JsonValue val && val.TryGetValue<double>(out var value))
            {
                if (!index.ContainsKey(kvp.Key))
                {
                    index[kvp.Key] = value;
                }
            }
        }
    }

    private void WriteConfig(VehicleConfigItem item, string json)
    {
        if (BackupCheckBox.IsChecked == true)
        {
            var backupTarget = item.IsZip ? item.SourcePath : item.InfoPath;
            var backupPath = backupTarget + ".bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(backupTarget, backupPath);
            }
        }

        if (item.IsZip)
        {
            WriteZipEntry(item.SourcePath, item.InfoPath, json);
        }
        else
        {
            File.WriteAllText(item.InfoPath, json, new UTF8Encoding(false));
        }
    }

    private static void WriteZipEntry(string zipPath, string entryPath, string json)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        var entry = archive.GetEntry(entryPath);
        entry?.Delete();

        var newEntry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var writer = new StreamWriter(newEntry.Open(), new UTF8Encoding(false));
        writer.Write(json);
    }

    private string ReadConfigText(VehicleConfigItem item)
    {
        if (!item.IsZip)
        {
            return File.ReadAllText(item.InfoPath);
        }

        using var archive = ZipFile.OpenRead(item.SourcePath);
        var entry = archive.GetEntry(item.InfoPath);
        if (entry == null)
        {
            throw new FileNotFoundException("Entry not found in zip.", item.InfoPath);
        }

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static bool IsVehicleInfoPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        var idx = normalized.IndexOf("\\vehicles\\", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        var fileName = Path.GetFileName(normalized);
        return fileName.StartsWith("info_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVehicleInfoEntry(string entryName)
    {
        if (!entryName.StartsWith("vehicles/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileName(entryName);
        return fileName.StartsWith("info_", StringComparison.OrdinalIgnoreCase) &&
               entryName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractModelAndConfig(string path, bool isZip, out string modelKey, out string configKey)
    {
        modelKey = string.Empty;
        configKey = string.Empty;

        var normalized = isZip ? path.Replace('\\', '/') : path.Replace('/', '\\');
        var marker = isZip ? "vehicles/" : "\\vehicles\\";
        var idx = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        var relative = normalized.Substring(idx + marker.Length);
        var parts = isZip
            ? relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
            : relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            return false;
        }

        modelKey = parts[0];
        var fileName = parts[^1];
        if (!fileName.StartsWith("info_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        configKey = baseName.Length > 5 ? baseName.Substring(5) : baseName;
        return true;
    }

    private static (int? min, int? max) ReadYears(JsonNode root)
    {
        var years = root["Years"] as JsonObject ?? root["aggregates"]?["Years"] as JsonObject;
        if (years == null)
        {
            return (null, null);
        }

        var min = ReadIntFromNode(years["min"]);
        var max = ReadIntFromNode(years["max"]);
        return (min, max);
    }

    private static string? ReadString(JsonNode root, string key)
    {
        if (root[key] is JsonValue val && val.TryGetValue<string>(out var result))
        {
            return result;
        }

        return null;
    }

    private static string? ReadStringOrAggregate(JsonNode root, string key)
    {
        var direct = ReadString(root, key);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (root["aggregates"]?[key] is JsonObject agg)
        {
            var values = agg
                .Where(kvp => kvp.Value is JsonValue v && v.TryGetValue<bool>(out var b) && b)
                .Select(kvp => kvp.Key)
                .ToList();

            if (values.Count > 0)
            {
                return string.Join(", ", values);
            }
        }

        return null;
    }

    private static double? ReadDouble(JsonNode root, string key)
    {
        if (root[key] is JsonValue val && val.TryGetValue<double>(out var result))
        {
            return result;
        }

        return null;
    }

    private static int? ReadInt(JsonNode root, string key)
    {
        return ReadIntFromNode(root[key]);
    }

    private static int? ReadIntFromNode(JsonNode? node)
    {
        if (node is JsonValue val)
        {
            if (val.TryGetValue<int>(out var result))
            {
                return result;
            }

            if (val.TryGetValue<double>(out var doubleResult))
            {
                return (int)doubleResult;
            }
        }

        return null;
    }
}
