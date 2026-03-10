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
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Media;
using ControlzEx.Theming;
using MahApps.Metro.Controls;
using Brush = System.Windows.Media.Brush;
using WpfApplication = System.Windows.Application;
using WpfControl = System.Windows.Controls.Control;
using WpfTextBox = System.Windows.Controls.TextBox;
using WinForms = System.Windows.Forms;

namespace WpfApp1;

public partial class MainWindow : MetroWindow
{
    private const string DefaultModsPath = @"D:\beamng progress\30\current\mods";
    private const string DefaultRegion = "northAmerica";
    private const string DefaultInsuranceClass = "dailyDriver";
    private static readonly string PersistedSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BeamNGMarketplaceConfigEditor",
        "settings.json");
    private static readonly Uri LightThemeDictionaryUri = new("Themes/AppTheme.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkThemeDictionaryUri = new("Themes/AppTheme.Dark.xaml", UriKind.Relative);
    private VehicleConfigItem? _selected;
    private ICollectionView? _configsView;
    private readonly AutoFillSettings _autoFillSettings = AutoFillSettings.CreateDefaults();
    private bool _hasUnsavedChanges;
    private bool _isLoadingForm;
    private bool _isScanning;
    private bool _suppressSettingsSave;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _searchCts;

    public ObservableCollection<VehicleConfigItem> Configs { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        LoadPersistedUiSettings();
        if (string.IsNullOrWhiteSpace(ModsPathTextBox.Text) && Directory.Exists(DefaultModsPath))
        {
            ModsPathTextBox.Text = DefaultModsPath;
        }
        StatusTextBlock.Text = "Select a mods folder and click Scan.";
        SetupConfigsView();
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
            {
                Opacity = 1;
            }
        };
        Closing += (_, _) => SavePersistedSettings();
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

    private async void ScanMods_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning)
        {
            _scanCts?.Cancel();
            return;
        }

        var modsPath = ModsPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath))
        {
            System.Windows.MessageBox.Show("Please select a valid mods folder.", "Scan Mods",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isScanning = true;
        ScanButton.IsEnabled = false;
        BrowseButton.IsEnabled = false;
        StatusTextBlock.Text = "Scanning mods...";

        _scanCts = new CancellationTokenSource();
        try
        {
            var (items, errors) = await Task.Run(() => CollectConfigs(modsPath, _scanCts.Token), _scanCts.Token);
            Configs.Clear();
            foreach (var item in items)
            {
                Configs.Add(item);
            }

            _selected = null;
            ClearForm();
            SetupConfigsView();
            StatusTextBlock.Text = $"Loaded {items.Count} configs. Errors: {errors}.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Scan canceled.";
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            _isScanning = false;
            ScanButton.IsEnabled = true;
            BrowseButton.IsEnabled = true;
        }
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
            if (!ReadInteger(YearMinUpDown.Value).HasValue) YearMinUpDown.Value = defaultYear;
            if (!ReadInteger(YearMaxUpDown.Value).HasValue) YearMaxUpDown.Value = defaultYear;
        }

        if (_autoFillSettings.UseValue && !ValueUpDown.Value.HasValue)
        {
            ValueUpDown.Value = _autoFillSettings.Value;
        }

        if (_autoFillSettings.UsePopulation && !ReadInteger(PopulationUpDown.Value).HasValue)
        {
            PopulationUpDown.Value = _autoFillSettings.Population;
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
            var mirrorResult = WriteConfig(_selected, json);
            _selected.NotifyChanges();
            LoadForm(_selected);
            SetDirty(false);
            StatusTextBlock.Text = $"Saved {_selected.ConfigKey} ({_selected.ModName}).{mirrorResult.BuildStatusSuffix(_selected.ModelKey)}";

            if (!string.IsNullOrWhiteSpace(mirrorResult.HardError))
            {
                System.Windows.MessageBox.Show(
                    mirrorResult.HardError,
                    "Input Into Vehicles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
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

    private (List<VehicleConfigItem> items, int errors) CollectConfigs(string modsPath, CancellationToken token)
    {
        var items = new List<VehicleConfigItem>();
        var errors = 0;

        foreach (var modDir in Directory.EnumerateDirectories(modsPath))
        {
            token.ThrowIfCancellationRequested();
            var dirName = Path.GetFileName(modDir);
            if (string.Equals(dirName, "unpacked", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddFolderModConfigs(modDir, dirName, items, ref errors, token);
        }

        var unpackedRoot = Path.Combine(modsPath, "unpacked");
        if (Directory.Exists(unpackedRoot))
        {
            foreach (var modDir in Directory.EnumerateDirectories(unpackedRoot))
            {
                token.ThrowIfCancellationRequested();
                AddFolderModConfigs(modDir, Path.GetFileName(modDir), items, ref errors, token);
            }
        }

        foreach (var zipPath in Directory.EnumerateFiles(modsPath, "*.zip"))
        {
            token.ThrowIfCancellationRequested();
            AddZipModConfigs(zipPath, items, ref errors, token);
        }

        return (items, errors);
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
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, cts.Token);
                WpfApplication.Current?.Dispatcher.Invoke(() =>
                {
                    _configsView?.Refresh();
                    UpdateEmptyState();
                });
            }
            catch (TaskCanceledException)
            {
                // Ignore.
            }
        });
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
        if (_configsView == null || EmptyStateContainer == null)
        {
            return;
        }

        EmptyStateContainer.Visibility = _configsView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
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

    private static void AddFolderModConfigs(string modDir, string modName, List<VehicleConfigItem> items, ref int errors, CancellationToken token)
    {
        foreach (var filePath in Directory.EnumerateFiles(modDir, "info_*.json", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
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

                items.Add(item);
            }
            catch
            {
                errors++;
            }
        }
    }

    private static void AddZipModConfigs(string zipPath, List<VehicleConfigItem> items, ref int errors, CancellationToken token)
    {
        var modName = Path.GetFileNameWithoutExtension(zipPath);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
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

                items.Add(item);
            }
        }
        catch
        {
            errors++;
        }
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
        _isLoadingForm = true;
        ConfigPathText.Text = item.IsZip ? $"{item.SourcePath} :: {item.InfoPath}" : item.InfoPath;
        BrandTextBox.Text = item.Brand ?? string.Empty;
        CountryTextBox.Text = item.Country ?? string.Empty;
        TypeTextBox.Text = item.Type ?? string.Empty;
        BodyStyleTextBox.Text = item.BodyStyle ?? string.Empty;
        ConfigTypeTextBox.Text = item.ConfigType ?? string.Empty;
        ConfigurationTextBox.Text = item.Configuration ?? string.Empty;
        SelectInsuranceClass(item.InsuranceClass);
        YearMinUpDown.Value = item.YearMin;
        YearMaxUpDown.Value = item.YearMax;
        ValueUpDown.Value = item.Value;
        PopulationUpDown.Value = item.Population;
        UpdatePopulationPresetFromValue(item.Population);

        var missing = item.GetMissingFields();
        
        UpdateFieldHighlightingFromMissing(missing);
        UpdateSummary(item);
        _isLoadingForm = false;
    }

    private void ClearForm()
    {
        _isLoadingForm = true;
        ConfigPathText.Text = string.Empty;
        BrandTextBox.Text = string.Empty;
        CountryTextBox.Text = string.Empty;
        TypeTextBox.Text = string.Empty;
        BodyStyleTextBox.Text = string.Empty;
        ConfigTypeTextBox.Text = string.Empty;
        ConfigurationTextBox.Text = string.Empty;
        SelectInsuranceClass(DefaultInsuranceClass);
        YearMinUpDown.Value = null;
        YearMaxUpDown.Value = null;
        ValueUpDown.Value = null;
        PopulationUpDown.Value = null;

        PopulationPresetComboBox.SelectedIndex = 0;
        ConfigSummaryText.Text = string.Empty;
        ResetFieldHighlighting();
        SetDirty(false);
        _isLoadingForm = false;
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
        var insuranceClass = GetSelectedInsuranceClass();

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(brand)) errors.Add("Brand");
        if (string.IsNullOrWhiteSpace(country)) errors.Add("Country");
        if (string.IsNullOrWhiteSpace(type)) errors.Add("Type");
        if (string.IsNullOrWhiteSpace(bodyStyle)) errors.Add("Body Style");
        if (string.IsNullOrWhiteSpace(configType)) errors.Add("Config Type");
        if (string.IsNullOrWhiteSpace(configuration)) errors.Add("Configuration");
        if (string.IsNullOrWhiteSpace(insuranceClass)) errors.Add("Insurance Class");

        var yearMin = ReadInteger(YearMinUpDown.Value);
        if (!yearMin.HasValue)
        {
            errors.Add("Year Min");
        }

        var yearMax = ReadInteger(YearMaxUpDown.Value);
        if (!yearMax.HasValue)
        {
            errors.Add("Year Max");
        }

        if (errors.Count == 0 && yearMin!.Value > yearMax!.Value)
        {
            errors.Add("Year Min must be <= Year Max");
        }

        var value = ValueUpDown.Value;
        if (!value.HasValue)
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
        updated["InsuranceClass"] = insuranceClass;
        updated["Region"] = DefaultRegion;
        updated["Years"] = new JsonObject
        {
            ["min"] = yearMin!.Value,
            ["max"] = yearMax!.Value
        };
        updated["Value"] = value!.Value;

        var population = ReadInteger(PopulationUpDown.Value);
        if (population.HasValue)
        {
            updated["Population"] = population.Value;
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
        item.InsuranceClass = ReadStringOrAggregate(root, "InsuranceClass") ?? DefaultInsuranceClass;

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
        if (string.IsNullOrWhiteSpace(GetSelectedInsuranceClass())) missing.Add("Insurance Class");

        if (!ReadInteger(YearMinUpDown.Value).HasValue)
        {
            missing.Add("Years");
        }

        if (!ReadInteger(YearMaxUpDown.Value).HasValue)
        {
            if (!missing.Contains("Years")) missing.Add("Years");
        }

        if (!ValueUpDown.Value.HasValue)
        {
            missing.Add("Value");
        }

        if (!ReadInteger(PopulationUpDown.Value).HasValue)
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
        var populationText = ReadInteger(PopulationUpDown.Value)?.ToString(CultureInfo.InvariantCulture) ?? "Missing";
        var valueText = ValueUpDown.Value.HasValue
            ? ValueUpDown.Value.Value.ToString("0", CultureInfo.InvariantCulture)
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
        if (_isLoadingForm)
        {
            return;
        }

        if (PopulationPresetComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var preset = item.Content?.ToString() ?? string.Empty;
        switch (preset)
        {
            case "Ultra-rare (1-50)":
                PopulationUpDown.Value = 25;
                break;
            case "Rare (50-200)":
                PopulationUpDown.Value = 100;
                break;
            case "Uncommon (200-800)":
                PopulationUpDown.Value = 400;
                break;
            case "Common (800-3000)":
                PopulationUpDown.Value = 1500;
                break;
            case "Very common (3000-10000)":
                PopulationUpDown.Value = 6000;
                break;
            default:
                return;
        }

        UpdateMissingFromForm();
        SetDirty(true);
    }

    private void InsuranceClassComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingForm)
        {
            return;
        }

        UpdateMissingFromForm();
        SetDirty(true);
    }

    private string GetSelectedInsuranceClass()
    {
        if (InsuranceClassComboBox.SelectedItem is ComboBoxItem selectedItem)
        {
            var value = selectedItem.Content?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return DefaultInsuranceClass;
    }

    private void SelectInsuranceClass(string? insuranceClass)
    {
        var target = string.IsNullOrWhiteSpace(insuranceClass)
            ? DefaultInsuranceClass
            : insuranceClass.Trim();

        foreach (var item in InsuranceClassComboBox.Items.OfType<ComboBoxItem>())
        {
            var value = item.Content?.ToString();
            if (string.Equals(value, target, StringComparison.OrdinalIgnoreCase))
            {
                InsuranceClassComboBox.SelectedItem = item;
                return;
            }
        }

        InsuranceClassComboBox.SelectedIndex = 0;
    }

    private void UpdatePopulationPresetFromValue(int? population)
    {
        if (!population.HasValue)
        {
            PopulationPresetComboBox.SelectedIndex = 0;
            return;
        }

        var value = population.Value;
        if (value >= 1 && value <= 50)
        {
            PopulationPresetComboBox.SelectedIndex = 1;
        }
        else if (value > 50 && value <= 200)
        {
            PopulationPresetComboBox.SelectedIndex = 2;
        }
        else if (value > 200 && value <= 800)
        {
            PopulationPresetComboBox.SelectedIndex = 3;
        }
        else if (value > 800 && value <= 3000)
        {
            PopulationPresetComboBox.SelectedIndex = 4;
        }
        else if (value > 3000 && value <= 10000)
        {
            PopulationPresetComboBox.SelectedIndex = 5;
        }
        else
        {
            PopulationPresetComboBox.SelectedIndex = 0;
        }
    }

    private void LoadPersistedUiSettings()
    {
        var persisted = ReadPersistedSettings();

        _suppressSettingsSave = true;
        try
        {
            if (persisted?.AutoFill != null)
            {
                _autoFillSettings.ApplyFrom(persisted.AutoFill);
            }

            ThemeToggleSwitch.IsOn = persisted?.IsDarkTheme ?? false;
            BackupToggleSwitch.IsOn = persisted?.BackupBeforeSave ?? false;
            VehiclesToggleSwitch.IsOn = persisted?.InputIntoVehicles ?? false;

            ApplyTheme(ThemeToggleSwitch.IsOn);
            LoadAutoFillSettingsIntoUi();
        }
        finally
        {
            _suppressSettingsSave = false;
        }
    }

    private static PersistedUiSettings? ReadPersistedSettings()
    {
        if (!File.Exists(PersistedSettingsPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(PersistedSettingsPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<PersistedUiSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private void SavePersistedSettings()
    {
        if (_suppressSettingsSave)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(PersistedSettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var snapshot = new PersistedUiSettings
            {
                IsDarkTheme = ThemeToggleSwitch?.IsOn == true,
                BackupBeforeSave = BackupToggleSwitch?.IsOn == true,
                InputIntoVehicles = VehiclesToggleSwitch?.IsOn == true,
                AutoFill = _autoFillSettings.Clone()
            };

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(PersistedSettingsPath, json, new UTF8Encoding(false));
        }
        catch
        {
            // Keep app flow non-blocking if settings persistence fails.
        }
    }


    private void AutoFillSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (HelpPopup.IsOpen)
        {
            HelpPopup.IsOpen = false;
        }

        LoadAutoFillSettingsIntoUi();
        AutoFillSettingsPopup.IsOpen = !AutoFillSettingsPopup.IsOpen;
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        if (AutoFillSettingsPopup.IsOpen)
        {
            AutoFillSettingsPopup.IsOpen = false;
        }

        HelpPopup.IsOpen = !HelpPopup.IsOpen;
    }

    private void AutoFillSettingsSave_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();

        var year = _autoFillSettings.Year ?? DateTime.Now.Year;
        var value = _autoFillSettings.Value;
        var population = _autoFillSettings.Population;

        if (AutoYearCheckBox.IsChecked == true)
        {
            var parsedYear = ReadInteger(AutoYearUpDown.Value);
            if (!parsedYear.HasValue)
            {
                errors.Add("Default Year");
            }
            else
            {
                year = parsedYear.Value;
            }
        }

        if (AutoValueCheckBox.IsChecked == true)
        {
            if (!AutoValueUpDown.Value.HasValue)
            {
                errors.Add("Value");
            }
            else
            {
                value = AutoValueUpDown.Value.Value;
            }
        }

        if (AutoPopulationCheckBox.IsChecked == true)
        {
            var parsedPopulation = ReadInteger(AutoPopulationUpDown.Value);
            if (!parsedPopulation.HasValue)
            {
                errors.Add("Population");
            }
            else
            {
                population = parsedPopulation.Value;
            }
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

        SavePersistedSettings();
        AutoFillSettingsPopup.IsOpen = false;
    }

    private void FieldTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingForm)
        {
            return;
        }

        UpdateMissingFromForm();
        SetDirty(true);
    }

    private void NumericFieldChanged(object sender, RoutedPropertyChangedEventArgs<double?> e)
    {
        if (_isLoadingForm)
        {
            return;
        }

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
        if (missing.Contains("Insurance Class")) { FocusField(InsuranceClassComboBox); return; }
        if (missing.Contains("Years")) { FocusField(YearMinUpDown); return; }
        if (missing.Contains("Value")) { FocusField(ValueUpDown); return; }
        if (missing.Contains("Population")) { FocusField(PopulationUpDown); return; }
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
        AutoYearUpDown.Value = _autoFillSettings.Year ?? DateTime.Now.Year;
        AutoValueUpDown.Value = _autoFillSettings.Value;
        AutoPopulationUpDown.Value = _autoFillSettings.Population;
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

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var active = Keyboard.FocusedElement as DependencyObject;
        var inTextInput = active is WpfTextBox;

        if (e.Key == Key.Escape && AutoFillSettingsPopup.IsOpen)
        {
            AutoFillSettingsPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && HelpPopup.IsOpen)
        {
            HelpPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        if (!inTextInput && ctrl && e.Key == Key.F)
        {
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.S)
        {
            SaveChanges_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.R)
        {
            ReloadSelected_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (!inTextInput && ctrl && e.Key == Key.A)
        {
            AutoFillMissing_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (!inTextInput && e.Key is Key.Down or Key.Up)
        {
            if (ConfigsGrid.Items.Count == 0)
            {
                return;
            }

            var idx = ConfigsGrid.SelectedIndex;
            if (idx < 0)
            {
                idx = 0;
            }
            else
            {
                idx += e.Key == Key.Down ? 1 : -1;
                idx = Math.Clamp(idx, 0, ConfigsGrid.Items.Count - 1);
            }

            ConfigsGrid.SelectedIndex = idx;
            ConfigsGrid.ScrollIntoView(ConfigsGrid.SelectedItem);
            e.Handled = true;
        }
    }

    private void ThemeToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        ApplyTheme(ThemeToggleSwitch.IsOn);
        SavePersistedSettings();
    }

    private void BackupToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        SavePersistedSettings();
    }

    private void VehiclesToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        SavePersistedSettings();
    }

    private void ApplyTheme(bool isDark)
    {
        ThemeManager.Current.ChangeTheme(this, isDark ? "Dark.Blue" : "Light.Blue");
        ReplaceThemeDictionary(isDark ? DarkThemeDictionaryUri : LightThemeDictionaryUri);
    }

    private static void ReplaceThemeDictionary(Uri themeDictionaryUri)
    {
        var app = WpfApplication.Current;
        if (app == null)
        {
            return;
        }

        var dictionaries = app.Resources.MergedDictionaries;
        var oldThemeDictionary = dictionaries.FirstOrDefault(d =>
            d.Source != null &&
            (d.Source.OriginalString.Contains("AppTheme.Light.xaml", StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.Contains("AppTheme.Dark.xaml", StringComparison.OrdinalIgnoreCase)));

        var newThemeDictionary = new ResourceDictionary { Source = themeDictionaryUri };
        if (oldThemeDictionary != null)
        {
            var index = dictionaries.IndexOf(oldThemeDictionary);
            dictionaries.RemoveAt(index);
            dictionaries.Insert(index, newThemeDictionary);
            return;
        }

        var stylesIndex = dictionaries
            .Select((dictionary, index) => new { dictionary, index })
            .FirstOrDefault(x => x.dictionary.Source != null &&
                                 x.dictionary.Source.OriginalString.Contains("AppControlStyles.xaml", StringComparison.OrdinalIgnoreCase))
            ?.index;

        if (stylesIndex.HasValue)
        {
            dictionaries.Insert(stylesIndex.Value, newThemeDictionary);
        }
        else
        {
            dictionaries.Add(newThemeDictionary);
        }
    }

    private static int? ReadInteger(double? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        var rounded = Math.Round(value.Value);
        if (Math.Abs(value.Value - rounded) > 0.0001d)
        {
            return null;
        }

        return (int)rounded;
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

    private sealed class PersistedUiSettings
    {
        public bool IsDarkTheme { get; set; }
        public bool BackupBeforeSave { get; set; }
        public bool InputIntoVehicles { get; set; }
        public AutoFillSettings AutoFill { get; set; } = AutoFillSettings.CreateDefaults();
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

        public AutoFillSettings Clone()
        {
            return new AutoFillSettings
            {
                Brand = Brand,
                Country = Country,
                Type = Type,
                BodyStyle = BodyStyle,
                ConfigType = ConfigType,
                Year = Year,
                Value = Value,
                Population = Population,
                UseBrand = UseBrand,
                UseCountry = UseCountry,
                UseType = UseType,
                UseBodyStyle = UseBodyStyle,
                UseConfigType = UseConfigType,
                UseYear = UseYear,
                UseValue = UseValue,
                UsePopulation = UsePopulation
            };
        }

        public void ApplyFrom(AutoFillSettings settings)
        {
            Brand = settings.Brand ?? Brand;
            Country = settings.Country ?? Country;
            Type = settings.Type ?? Type;
            BodyStyle = settings.BodyStyle ?? BodyStyle;
            ConfigType = settings.ConfigType ?? ConfigType;
            Year = settings.Year;
            Value = settings.Value;
            Population = settings.Population;
            UseBrand = settings.UseBrand;
            UseCountry = settings.UseCountry;
            UseType = settings.UseType;
            UseBodyStyle = settings.UseBodyStyle;
            UseConfigType = settings.UseConfigType;
            UseYear = settings.UseYear;
            UseValue = settings.UseValue;
            UsePopulation = settings.UsePopulation;
        }

        public static AutoFillSettings CreateDefaults() => new();
    }

    private void UpdateFieldHighlightingFromMissing(IReadOnlyCollection<string> missing)
    {
        ResetFieldHighlighting();
        var badBrush = (Brush)(WpfApplication.Current?.TryFindResource("ErrorBrush") ?? System.Windows.Media.Brushes.Red);
        var defaultBorder = (Brush)(WpfApplication.Current?.TryFindResource("BorderBrush") ?? System.Windows.Media.Brushes.Gray);

        SetFieldHighlight(BrandLabel, BrandTextBox, missing.Contains("Brand"), badBrush, defaultBorder);
        SetFieldHighlight(CountryLabel, CountryTextBox, missing.Contains("Country"), badBrush, defaultBorder);
        SetFieldHighlight(TypeLabel, TypeTextBox, missing.Contains("Type"), badBrush, defaultBorder);
        SetFieldHighlight(BodyStyleLabel, BodyStyleTextBox, missing.Contains("Body Style"), badBrush, defaultBorder);
        SetFieldHighlight(ConfigTypeLabel, ConfigTypeTextBox, missing.Contains("Config Type"), badBrush, defaultBorder);
        SetFieldHighlight(ConfigurationLabel, ConfigurationTextBox, missing.Contains("Configuration"), badBrush, defaultBorder);
        SetFieldHighlight(InsuranceClassLabel, InsuranceClassComboBox, missing.Contains("Insurance Class"), badBrush, defaultBorder);
        SetFieldHighlight(ValueLabel, ValueUpDown, missing.Contains("Value"), badBrush, defaultBorder);
        SetFieldHighlight(PopulationLabel, PopulationUpDown, missing.Contains("Population"), badBrush, defaultBorder);
        if (missing.Contains("Years"))
        {
            SetFieldHighlight(YearMinLabel, YearMinUpDown, true, badBrush, defaultBorder);
            SetFieldHighlight(YearMaxLabel, YearMaxUpDown, true, badBrush, defaultBorder);
        }
    }

    private void ResetFieldHighlighting()
    {
        var defaultBrush = (Brush)(WpfApplication.Current?.TryFindResource("FormLabelBrush") ?? System.Windows.Media.Brushes.Gray);
        var defaultBorder = (Brush)(WpfApplication.Current?.TryFindResource("BorderBrush") ?? System.Windows.Media.Brushes.Gray);

        BrandLabel.Foreground = defaultBrush;
        CountryLabel.Foreground = defaultBrush;
        TypeLabel.Foreground = defaultBrush;
        BodyStyleLabel.Foreground = defaultBrush;
        ConfigTypeLabel.Foreground = defaultBrush;
        ConfigurationLabel.Foreground = defaultBrush;
        InsuranceClassLabel.Foreground = defaultBrush;
        YearMinLabel.Foreground = defaultBrush;
        YearMaxLabel.Foreground = defaultBrush;
        ValueLabel.Foreground = defaultBrush;
        PopulationLabel.Foreground = defaultBrush;
        PopulationPresetLabel.Foreground = defaultBrush;
        ResetFieldBorder(BrandTextBox, defaultBorder);
        ResetFieldBorder(CountryTextBox, defaultBorder);
        ResetFieldBorder(TypeTextBox, defaultBorder);
        ResetFieldBorder(BodyStyleTextBox, defaultBorder);
        ResetFieldBorder(ConfigTypeTextBox, defaultBorder);
        ResetFieldBorder(ConfigurationTextBox, defaultBorder);
        ResetFieldBorder(InsuranceClassComboBox, defaultBorder);
        ResetFieldBorder(YearMinUpDown, defaultBorder);
        ResetFieldBorder(YearMaxUpDown, defaultBorder);
        ResetFieldBorder(ValueUpDown, defaultBorder);
        ResetFieldBorder(PopulationUpDown, defaultBorder);
    }

    private static void SetFieldHighlight(TextBlock label, WpfControl box, bool missing, Brush badBrush, Brush defaultBorder)
    {
        if (missing)
        {
            label.Foreground = badBrush;
            box.BorderBrush = badBrush;
            box.BorderThickness = new Thickness(1.5);
        }
        else
        {
            label.Foreground = (Brush)(WpfApplication.Current?.TryFindResource("FormLabelBrush") ?? System.Windows.Media.Brushes.Gray);
            ResetFieldBorder(box, defaultBorder);
        }
    }

    private static void ResetFieldBorder(WpfControl box, Brush defaultBorder)
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

    private VehiclesMirrorResult WriteConfig(VehicleConfigItem item, string json)
    {
        if (BackupToggleSwitch.IsOn)
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

        if (VehiclesToggleSwitch?.IsOn != true)
        {
            return VehiclesMirrorResult.NotRequested();
        }

        return MirrorConfigBundleToVehicles(item, json, ModsPathTextBox.Text.Trim());
    }

    private VehiclesMirrorResult MirrorConfigBundleToVehicles(VehicleConfigItem item, string updatedInfoJson, string modsPath)
    {
        var result = VehiclesMirrorResult.Requested();
        var vehiclesRoot = ResolveVehiclesRoot(modsPath, out var resolveWarning);
        if (vehiclesRoot == null)
        {
            result.Warnings.Add(resolveWarning);
            return result;
        }

        try
        {
            var destinationModelDir = Path.Combine(vehiclesRoot, item.ModelKey);
            Directory.CreateDirectory(destinationModelDir);

            var infoFileName = Path.GetFileName(item.InfoPath.Replace('/', '\\'));
            var destinationInfoPath = Path.Combine(destinationModelDir, infoFileName);
            File.WriteAllText(destinationInfoPath, updatedInfoJson, new UTF8Encoding(false));
            result.InfoCopied = true;

            if (item.IsZip)
            {
                CopyMirrorAssetsFromZip(item, destinationModelDir, result);
            }
            else
            {
                CopyMirrorAssetsFromFolder(item, destinationModelDir, result);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            result.HardError = $"Saved the mod config, but copying to vehicles failed due to access permissions: {ex.Message}";
        }
        catch (IOException ex)
        {
            result.HardError = $"Saved the mod config, but copying to vehicles failed due to an I/O error: {ex.Message}";
        }
        catch (Exception ex)
        {
            result.HardError = $"Saved the mod config, but vehicles mirror failed unexpectedly: {ex.Message}";
        }

        return result;
    }

    private static string? ResolveVehiclesRoot(string modsPath, out string warning)
    {
        warning = string.Empty;
        if (string.IsNullOrWhiteSpace(modsPath))
        {
            warning = "Vehicles mirror skipped because mods path is empty.";
            return null;
        }

        string fullModsPath;
        try
        {
            fullModsPath = Path.GetFullPath(modsPath);
        }
        catch (Exception)
        {
            warning = $"Vehicles mirror skipped because mods path '{modsPath}' is invalid.";
            return null;
        }

        var modsParent = Directory.GetParent(fullModsPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
        if (string.IsNullOrWhiteSpace(modsParent))
        {
            warning = "Vehicles mirror skipped because the mods folder parent path could not be resolved.";
            return null;
        }

        var vehiclesRoot = Path.Combine(modsParent, "vehicles");
        if (!Directory.Exists(vehiclesRoot))
        {
            warning = $"Vehicles mirror skipped because '{vehiclesRoot}' was not found.";
            return null;
        }

        return vehiclesRoot;
    }

    private static void CopyMirrorAssetsFromFolder(VehicleConfigItem item, string destinationModelDir, VehiclesMirrorResult result)
    {
        var sourceModelDir = ResolveSourceModelDirectory(item);
        if (sourceModelDir == null || !Directory.Exists(sourceModelDir))
        {
            result.Warnings.Add("Source model folder was not found for asset copy.");
            return;
        }

        foreach (var sourceFilePath in Directory.EnumerateFiles(sourceModelDir, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(sourceFilePath);
            if (!TryClassifyMirrorAsset(fileName, item.ConfigKey, out var assetKind))
            {
                continue;
            }

            var destinationFilePath = Path.Combine(destinationModelDir, fileName);
            File.Copy(sourceFilePath, destinationFilePath, overwrite: true);
            result.RegisterCopiedAsset(assetKind);
        }
    }

    private static void CopyMirrorAssetsFromZip(VehicleConfigItem item, string destinationModelDir, VehiclesMirrorResult result)
    {
        using var archive = ZipFile.OpenRead(item.SourcePath);
        var modelPrefix = ResolveModelPrefixFromZipInfoPath(item.InfoPath);
        if (string.IsNullOrWhiteSpace(modelPrefix))
        {
            result.Warnings.Add("Source zip model path could not be resolved for asset copy.");
            return;
        }

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            if (!entry.FullName.StartsWith(modelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = entry.FullName.Substring(modelPrefix.Length);
            if (relative.Contains('/'))
            {
                continue;
            }

            var fileName = Path.GetFileName(relative);
            if (!TryClassifyMirrorAsset(fileName, item.ConfigKey, out var assetKind))
            {
                continue;
            }

            var destinationFilePath = Path.Combine(destinationModelDir, fileName);
            using var sourceStream = entry.Open();
            using var targetStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            sourceStream.CopyTo(targetStream);
            result.RegisterCopiedAsset(assetKind);
        }
    }

    private static string? ResolveSourceModelDirectory(VehicleConfigItem item)
    {
        var sourcePath = item.InfoPath.Replace('/', '\\');
        var marker = $"\\vehicles\\{item.ModelKey}\\";
        var markerIndex = sourcePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var modelDirPath = sourcePath.Substring(0, markerIndex + marker.Length - 1);
            if (Directory.Exists(modelDirPath))
            {
                return modelDirPath;
            }
        }

        var fallback = Path.Combine(item.SourcePath, "vehicles", item.ModelKey);
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static string? ResolveModelPrefixFromZipInfoPath(string infoPath)
    {
        var normalized = infoPath.Replace('\\', '/');
        var markerIndex = normalized.IndexOf("vehicles/", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var afterVehicles = normalized.Substring(markerIndex + "vehicles/".Length);
        var slashIndex = afterVehicles.IndexOf('/');
        if (slashIndex <= 0)
        {
            return null;
        }

        var modelSegment = afterVehicles.Substring(0, slashIndex);
        return normalized.Substring(0, markerIndex) + "vehicles/" + modelSegment + "/";
    }

    private static bool TryClassifyMirrorAsset(string fileName, string configKey, out MirrorAssetKind assetKind)
    {
        assetKind = MirrorAssetKind.None;
        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension) || string.IsNullOrWhiteSpace(baseName))
        {
            return false;
        }

        if (extension.Equals(".pc", StringComparison.OrdinalIgnoreCase) &&
            baseName.Equals(configKey, StringComparison.OrdinalIgnoreCase))
        {
            assetKind = MirrorAssetKind.ConfigPc;
            return true;
        }

        if (!IsSupportedPreviewImageExtension(extension))
        {
            return false;
        }

        if (baseName.Equals(configKey, StringComparison.OrdinalIgnoreCase))
        {
            assetKind = MirrorAssetKind.ConfigImage;
            return true;
        }

        if (baseName.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            assetKind = MirrorAssetKind.DefaultImage;
            return true;
        }

        return false;
    }

    private static bool IsSupportedPreviewImageExtension(string extension)
    {
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private enum MirrorAssetKind
    {
        None,
        ConfigPc,
        ConfigImage,
        DefaultImage
    }

    private sealed class VehiclesMirrorResult
    {
        public bool IsRequested { get; private init; }
        public bool InfoCopied { get; set; }
        public bool ConfigPcCopied { get; private set; }
        public int ConfigImageCount { get; private set; }
        public int DefaultImageCount { get; private set; }
        public List<string> Warnings { get; } = new();
        public string? HardError { get; set; }

        public static VehiclesMirrorResult NotRequested() => new() { IsRequested = false };

        public static VehiclesMirrorResult Requested() => new() { IsRequested = true };

        public void RegisterCopiedAsset(MirrorAssetKind assetKind)
        {
            switch (assetKind)
            {
                case MirrorAssetKind.ConfigPc:
                    ConfigPcCopied = true;
                    break;
                case MirrorAssetKind.ConfigImage:
                    ConfigImageCount++;
                    break;
                case MirrorAssetKind.DefaultImage:
                    DefaultImageCount++;
                    break;
            }
        }

        public string BuildStatusSuffix(string modelKey)
        {
            if (!IsRequested)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(HardError))
            {
                return $" Vehicles mirror failed for {modelKey}.";
            }

            if (!InfoCopied)
            {
                return Warnings.Count == 0
                    ? " Vehicles mirror skipped."
                    : $" {string.Join(" ", Warnings)}";
            }

            var copiedParts = new List<string> { "info" };
            if (ConfigPcCopied)
            {
                copiedParts.Add("pc");
            }
            if (ConfigImageCount > 0)
            {
                copiedParts.Add($"{ConfigImageCount} config image(s)");
            }
            if (DefaultImageCount > 0)
            {
                copiedParts.Add($"{DefaultImageCount} default image(s)");
            }

            var missingParts = new List<string>();
            if (!ConfigPcCopied)
            {
                missingParts.Add("pc");
            }
            if (ConfigImageCount == 0)
            {
                missingParts.Add("config image");
            }
            if (DefaultImageCount == 0)
            {
                missingParts.Add("default image");
            }

            var suffix = $" Vehicles mirror to vehicles/{modelKey}: {string.Join(", ", copiedParts)}";
            if (missingParts.Count > 0)
            {
                suffix += $"; missing {string.Join(", ", missingParts)}";
            }
            if (Warnings.Count > 0)
            {
                suffix += $"; {string.Join(" ", Warnings)}";
            }

            return suffix + ".";
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

