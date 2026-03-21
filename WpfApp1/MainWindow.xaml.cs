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
using System.Text.RegularExpressions;
using ControlzEx.Theming;
using MahApps.Metro.Controls;
using Brush = System.Windows.Media.Brush;
using WpfApplication = System.Windows.Application;
using WpfControl = System.Windows.Controls.Control;
using WpfTextBox = System.Windows.Controls.TextBox;
using WinForms = System.Windows.Forms;
using System.Windows.Threading;

namespace WpfApp1;


public sealed class SummaryCountItem
{
    public string Label { get; init; } = string.Empty;
    public int Count { get; init; }
    public IReadOnlyList<BrandSourceItem> Sources { get; init; } = Array.Empty<BrandSourceItem>();
}

public sealed class BrandSourceItem
{
    public string DisplayName { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public int ConfigCount { get; init; }
    public int ReviewCount { get; init; }
}

public sealed class ModLibraryItem
{
    public string Label { get; init; } = string.Empty;
    public int Count { get; init; }
    public int ReviewCount { get; init; }
    public string Category { get; init; } = string.Empty;
}

public partial class MainWindow : MetroWindow
{
    private sealed class PendingConfigWrite
    {
        public required VehicleConfigItem Item { get; init; }
        public required string Json { get; init; }
    }

    private sealed class ZipScanContext
    {
        private readonly Dictionary<string, ZipArchiveEntry> _entries;
        private readonly Dictionary<string, JsonNode?> _parsedJsonCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _licensePlateCache = new(StringComparer.OrdinalIgnoreCase);

        public ZipScanContext(ZipArchive archive)
        {
            _entries = archive.Entries
                .Where(e => !string.IsNullOrWhiteSpace(e.FullName))
                .GroupBy(e => NormalizeArchivePath(e.FullName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        public IEnumerable<ZipArchiveEntry> Entries => _entries.Values;

        public bool Contains(string path) => _entries.ContainsKey(NormalizeArchivePath(path));

        public string? ReadText(string path)
        {
            if (!_entries.TryGetValue(NormalizeArchivePath(path), out var entry))
            {
                return null;
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        public JsonNode? ReadJson(string path)
        {
            var normalized = NormalizeArchivePath(path);
            if (_parsedJsonCache.TryGetValue(normalized, out var cached))
            {
                return cached;
            }

            var parsed = ParseJson(ReadText(path) ?? string.Empty);
            _parsedJsonCache[normalized] = parsed;
            return parsed;
        }

        public string? ReadLicensePlate(string path)
        {
            var normalized = NormalizeArchivePath(path);
            if (_licensePlateCache.TryGetValue(normalized, out var cached))
            {
                return cached;
            }

            var jsonText = ReadText(path);
            var value = jsonText == null ? null : ReadString(ParseJson(jsonText), "licenseName");
            _licensePlateCache[normalized] = value;
            return value;
        }

        private static string NormalizeArchivePath(string path) => path.Replace('\\', '/').TrimStart('/');
    }

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
    private readonly VehicleInferenceService _inferenceService;
    private bool _hasUnsavedChanges;
    private bool _isLoadingForm;
    private bool _isScanning;
    private bool _suppressSettingsSave;
    private bool _isAutoFillAllRunning;
    private string _accentColorName = "Blue";
    private string _defaultStartupPage = "Dashboard";
    private bool _reopenLastModsFolderOnStartup = true;
    private bool _holdWeakMatchesForReview = true;
    private bool _openReviewQueueAfterAutoFill = false;
    private int _lookupTimeoutSeconds = 8;
    private CancellationTokenSource? _scanCts;
    private readonly DispatcherTimer _searchRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private CancellationTokenSource? _autoFillAllCts;
    private static readonly string ScrapeLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeamNGMarketplaceConfigEditor", "scrape.log");

    public ObservableCollection<VehicleConfigItem> Configs { get; } = new();
    public ObservableCollection<VehicleConfigItem> FlaggedConfigs { get; } = new();
    public ObservableCollection<SummaryCountItem> DashboardBrandStats { get; } = new();
    public ObservableCollection<ModLibraryItem> ModLibraryItems { get; } = new();

    private string _currentWorkspacePage = "Results";
    private bool _isLeftNavCollapsed;
    private readonly GridLength _expandedLeftNavWidth = new(160, GridUnitType.Pixel);
    private readonly GridLength _expandedLeftNavSpacerWidth = new(8, GridUnitType.Pixel);
    private DateTime _lastAutoFillUiProgressUpdateUtc = DateTime.MinValue;
    private readonly object _autoFillUiProgressGate = new();
    private const int AutoFillUiProgressIntervalMs = 650;
    private readonly ObservableCollection<PlatePreviewItem> _licensePagePreviewItems = new();
    private readonly List<PlateModChoiceItem> _licensePageModChoices = new();
    private readonly List<PlateConfigChoiceItem> _licensePageConfigChoices = new();
    private bool _isRefreshingLicensePageUi;
    private string? _licensePageSelectedModSourcePath;
    private readonly HashSet<string> _licensePageSelectedConfigKeys = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        _inferenceService = VehicleInferenceService.CreateDefault();
        InitializeComponent();
        DataContext = this;
        _searchRefreshTimer.Tick += (_, _) =>
        {
            _searchRefreshTimer.Stop();
            _configsView?.Refresh();
            UpdateEmptyState();
        };
        DashboardBrandStatsGrid.ItemsSource = DashboardBrandStats;
        ModLibraryGrid.ItemsSource = ModLibraryItems;
        if (LicensePagePreviewDataGrid != null) LicensePagePreviewDataGrid.ItemsSource = _licensePagePreviewItems;
        LoadPersistedUiSettings();
        if (string.IsNullOrWhiteSpace(ModsPathTextBox.Text))
        {
            if (_reopenLastModsFolderOnStartup)
            {
                var persistedPath = ReadPersistedSettings()?.LastModsPath;
                if (!string.IsNullOrWhiteSpace(persistedPath) && Directory.Exists(persistedPath))
                {
                    ModsPathTextBox.Text = persistedPath;
                }
            }

            if (string.IsNullOrWhiteSpace(ModsPathTextBox.Text) && Directory.Exists(DefaultModsPath))
            {
                ModsPathTextBox.Text = DefaultModsPath;
            }
        }
        StatusTextBlock.Text = "Select a mods folder and click Scan.";
        StatusDetailTextBlock.Text = string.Empty;
        SetupConfigsView();
        SetWorkspacePage(string.IsNullOrWhiteSpace(_defaultStartupPage) ? "Dashboard" : _defaultStartupPage);
        SyncSettingsPageFromMain();
        RefreshWorkspaceSummary();
        RefreshDataPageSummary();
        RefreshWorkspacePageSummaries();
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
            {
                Opacity = 1;
            }

            UpdateWindowChromeState();
        };
        Loaded += (_, _) =>
        {
            UpdateWindowChromeState();
            ApplyLeftNavState();
        };
        Closing += (_, _) => { SavePersistedSettings(); SaveModMemorySnapshot(); };
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
            RefreshSettingsPageSummary();
            FooterRepoStateTextBlock.Text = "Repo selected";
            SavePersistedSettings();
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
        AutoFillAllButton.IsEnabled = false;
        SetWorkspaceNavigationEnabled(false);
        StatusTextBlock.Text = "Scanning mods...";
        StatusDetailTextBlock.Text = "Reading zip archives, folders, and vehicle JSON files. Page switching is temporarily locked during scan to avoid UI races.";
        DashboardRecentActivityTextBlock.Text = "Scan in progress...";

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
            ApplyPersistedModMemoryToConfigs();
            RefreshAllWorkspaceState(persist: true);
            StatusTextBlock.Text = $"Loaded {items.Count} configs. Errors: {errors}.";
            StatusDetailTextBlock.Text = errors > 0 ? "Some files could not be parsed cleanly. Review logs and flagged items for follow-up." : "Scan completed successfully. Open Configuration Editor to edit configs or Flagged Configs to inspect items that still need attention.";
            DashboardRecentActivityTextBlock.Text = StatusTextBlock.Text;
            RefreshLogsPage();
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Scan canceled.";
            StatusDetailTextBlock.Text = "The scan was interrupted before completion.";
            DashboardRecentActivityTextBlock.Text = StatusTextBlock.Text;
            RefreshLogsPage();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Scan failed.";
            StatusDetailTextBlock.Text = ex.Message;
            DashboardRecentActivityTextBlock.Text = $"Scan failed: {ex.Message}";
            AppendScrapeLog($"Scan failed: {ex}");
            RefreshLogsPage();
            System.Windows.MessageBox.Show($"The scan failed before the UI could finish updating.\n\n{ex.Message}", "Scan failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            _isScanning = false;
            ScanButton.IsEnabled = true;
            BrowseButton.IsEnabled = true;
            AutoFillAllButton.IsEnabled = true;
            SetWorkspaceNavigationEnabled(true);
        }
    }

    private void ConfigsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentWorkspacePage != "Results" || _isLoadingForm)
        {
            return;
        }

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

        ApplyInferenceToForm(_selected);

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

    private void InternetLookup_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            System.Windows.MessageBox.Show("Select a config to edit first.", "Internet Lookup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lookupWindow = new InternetLookupWindow(_selected)
        {
            Owner = this
        };

        var accepted = lookupWindow.ShowDialog();
        if (accepted != true || lookupWindow.LookupResult == null)
        {
            return;
        }

        var result = lookupWindow.LookupResult;
        if (!string.IsNullOrWhiteSpace(result.Brand)) BrandTextBox.Text = result.Brand;
        if (!string.IsNullOrWhiteSpace(result.Model)) ConfigurationTextBox.Text = result.Model;
        if (!string.IsNullOrWhiteSpace(result.BodyStyle)) BodyStyleTextBox.Text = result.BodyStyle;
        if (!string.IsNullOrWhiteSpace(result.Country)) CountryTextBox.Text = result.Country;
        if (!string.IsNullOrWhiteSpace(result.Type)) TypeTextBox.Text = result.Type;
        if (result.YearMin.HasValue) YearMinUpDown.Value = result.YearMin;
        if (result.YearMax.HasValue) YearMaxUpDown.Value = result.YearMax;
        if (result.EstimatedValue.HasValue && result.EstimatedValue.Value > 0) ValueUpDown.Value = result.EstimatedValue.Value;
        if (result.Population.HasValue && result.Population.Value > 0) PopulationUpDown.Value = result.Population.Value;
        if (!string.IsNullOrWhiteSpace(result.InsuranceClass)) SelectInsuranceClass(result.InsuranceClass);

        UpdateMissingFromForm();
        SetDirty(true);

        _selected.LastAutoFillStatus = "Lookup applied";
        _selected.LastAutoFillSource = "Internet lookup";
        _selected.LastAutoFillDetail = result.Evidence;
        _selected.NotifyChanges();
    }

    private void ModInternetLookup_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            System.Windows.MessageBox.Show("Select a config from the mod you want to look up first.", "Mod Lookup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lookupWindow = new InternetLookupWindow(_selected)
        {
            Owner = this
        };

        var accepted = lookupWindow.ShowDialog();
        if (accepted != true || lookupWindow.LookupResult == null)
        {
            return;
        }

        var result = lookupWindow.LookupResult;
        var modItems = GetSelectedModConfigs().ToList();
        foreach (var item in modItems)
        {
            if (!string.IsNullOrWhiteSpace(result.Brand)) item.Brand = result.Brand;
            if (!string.IsNullOrWhiteSpace(result.BodyStyle)) item.BodyStyle = result.BodyStyle;
            if (!string.IsNullOrWhiteSpace(result.Country)) item.Country = result.Country;
            if (!string.IsNullOrWhiteSpace(result.Type)) item.Type = result.Type;
            if (result.YearMin.HasValue) item.YearMin = result.YearMin;
            if (result.YearMax.HasValue) item.YearMax = result.YearMax;
            item.LastAutoFillStatus = "Mod lookup applied";
            item.LastAutoFillSource = "Internet lookup";
            item.LastAutoFillDetail = result.Evidence;
            item.NotifyChanges();
        }

        if (!string.IsNullOrWhiteSpace(result.Brand)) BrandTextBox.Text = result.Brand;
        if (!string.IsNullOrWhiteSpace(result.Model)) ConfigurationTextBox.Text = result.Model;
        if (!string.IsNullOrWhiteSpace(result.BodyStyle)) BodyStyleTextBox.Text = result.BodyStyle;
        if (!string.IsNullOrWhiteSpace(result.Country)) CountryTextBox.Text = result.Country;
        if (!string.IsNullOrWhiteSpace(result.Type)) TypeTextBox.Text = result.Type;
        if (result.YearMin.HasValue) YearMinUpDown.Value = result.YearMin;
        if (result.YearMax.HasValue) YearMaxUpDown.Value = result.YearMax;
        UpdateMissingFromForm();
        SetDirty(true);

        StatusTextBlock.Text = $"Applied lookup fields to {modItems.Count} config(s) in {GetSelectedModDisplayName()}.";
        StatusDetailTextBlock.Text = "Shared mod-level identity fields were updated from the selected lookup result.";
        DashboardRecentActivityTextBlock.Text = StatusTextBlock.Text;
        RefreshLicensePlatesPageSummary();
    }

    private void OpenLicensePlatesPage_Click(object sender, RoutedEventArgs e)
    {
        SetWorkspacePage("LicensePlates");
    }

    private void OpenLicensePlateManagerPageWindow_Click(object sender, RoutedEventArgs e)
    {
        SetWorkspacePage("LicensePlates");
    }


    private async void ConfigPlate_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            System.Windows.MessageBox.Show("Select a config first.", "Config Plate", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await OpenSimpleConfigPlateDialogAsync(_selected);
    }

    private Task OpenSimpleConfigPlateDialogAsync(VehicleConfigItem item)
    {
        var plateTextBox = new System.Windows.Controls.TextBox
        {
            Margin = new Thickness(0, 10, 0, 0),
            Height = 42,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = (Brush?)TryFindResource("InputBackgroundBrush") ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 20, 40)),
            Foreground = (Brush?)TryFindResource("InputForegroundBrush") ?? System.Windows.Media.Brushes.White,
            BorderBrush = (Brush?)TryFindResource("BorderBrush") ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 86, 130)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 0, 12, 0),
            Text = item.CurrentLicensePlate ?? string.Empty
        };

        var removeCheckBox = new System.Windows.Controls.CheckBox
        {
            Content = "Remove this config's custom plate instead",
            Margin = new Thickness(0, 14, 0, 0),
            Foreground = (Brush?)TryFindResource("AppForegroundBrush") ?? System.Windows.Media.Brushes.White,
            IsChecked = false
        };
        removeCheckBox.Checked += (_, _) => plateTextBox.IsEnabled = false;
        removeCheckBox.Unchecked += (_, _) => plateTextBox.IsEnabled = true;

        var applyButton = new System.Windows.Controls.Button
        {
            Content = "Apply",
            Width = 140,
            Height = 40,
            Margin = new Thickness(12, 0, 0, 0),
            IsDefault = true
        };

        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Width = 140,
            Height = 40,
            IsCancel = true
        };

        var window = new Window
        {
            Title = "Config Plate",
            Owner = this,
            Width = 760,
            Height = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush?)TryFindResource("PanelBackgroundBrush") ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 20, 40)),
            Foreground = (Brush?)TryFindResource("AppForegroundBrush") ?? System.Windows.Media.Brushes.White,
            Content = null
        };

        applyButton.Click += async (_, _) =>
        {
            var clearMode = removeCheckBox.IsChecked == true;
            var plateText = plateTextBox.Text?.Trim() ?? string.Empty;
            if (!clearMode && string.IsNullOrWhiteSpace(plateText))
            {
                System.Windows.MessageBox.Show(window, "Enter a plate value or choose remove.", "Config Plate", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            window.DialogResult = true;
            window.Close();

            await ApplyLicensePlateChangesAsync(new List<VehicleConfigItem> { item }, clearMode, plateText, $"{item.ModName} / {item.ConfigKey}");
            RefreshLicensePlatesPageUi();
            LoadForm(item);
        };

        cancelButton.Click += (_, _) => window.Close();

        var accentBrush = (Brush?)TryFindResource("AccentBrush") ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(169, 140, 230));
        var borderBrush = (Brush?)TryFindResource("BorderBrush") ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 86, 130));
        var cardBrush = (Brush?)TryFindResource("CardBackgroundBrush") ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 27, 52));
        var mutedBrush = (Brush?)TryFindResource("MutedForegroundBrush") ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 170, 190));

        var root = new Border
        {
            Margin = new Thickness(14),
            Padding = new Thickness(28),
            Background = cardBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18)
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Config Plate", FontSize = 22, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = $"Editing only this config: {item.ModName} / {item.ConfigKey}", Margin = new Thickness(0, 10, 0, 18), Foreground = mutedBrush, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = "Plate text", Foreground = mutedBrush, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(plateTextBox);
        stack.Children.Add(removeCheckBox);
        var buttonsRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 28, 0, 0) };
        buttonsRow.Children.Add(cancelButton);
        buttonsRow.Children.Add(applyButton);
        stack.Children.Add(buttonsRow);
        root.Child = stack;
        window.Content = root;
        applyButton.Background = accentBrush;
        applyButton.BorderBrush = accentBrush;
        cancelButton.Background = cardBrush;
        cancelButton.BorderBrush = borderBrush;
        cancelButton.Foreground = window.Foreground;
        applyButton.Foreground = window.Foreground;

        window.ShowDialog();
        return Task.CompletedTask;
    }


    private static void EnforcePlateTextLimit(System.Windows.Controls.TextBox? textBox)
    {
        if (textBox is null) return;

        const int maxPlateLength = 10;
        var text = textBox.Text ?? string.Empty;
        if (text.Length <= maxPlateLength) return;

        var caret = textBox.CaretIndex;
        textBox.Text = text.Substring(0, maxPlateLength);
        textBox.CaretIndex = Math.Min(caret, maxPlateLength);
    }

    private async void ModPlates_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            System.Windows.MessageBox.Show("Select a config from the mod you want to edit first.", "Mod Plates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await OpenLicensePlateManagerAsync("Mod");
    }

    private async void WorkspacePlates_Click(object sender, RoutedEventArgs e)
    {
        if (Configs.Count == 0)
        {
            System.Windows.MessageBox.Show("Scan mods first.", "Bulk Plates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await OpenLicensePlateManagerAsync("Workspace");
    }

    private async Task OpenLicensePlateManagerAsync(string initialScope)
    {
        try
        {
            var configSnapshot = Configs.Where(x => x != null).ToList();
            var window = new LicensePlateManagerWindow(configSnapshot, _selected, initialScope)
            {
                Owner = this
            };

            if (window.ShowDialog() != true)
            {
                return;
            }

            await ApplyLicensePlateChangesAsync(window.ActionableItems, window.ClearMode, window.PlateText, window.SelectedScopeDisplayLabel);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Could not open the license plate manager.\n\n" + ex.Message, "License Plate Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private IEnumerable<VehicleConfigItem> GetSelectedModConfigs()
    {
        if (_selected == null)
        {
            return Enumerable.Empty<VehicleConfigItem>();
        }

        return Configs.Where(x => string.Equals(x.SourcePath ?? string.Empty, _selected.SourcePath ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    private string GetSelectedModDisplayName()
    {
        return string.IsNullOrWhiteSpace(_selected?.ModName) ? "Selected mod" : _selected!.ModName;
    }

    private IReadOnlyList<VehicleConfigItem> GetFilteredConfigSnapshot()
    {
        if (_configsView == null)
        {
            return Configs.ToList();
        }

        return _configsView.Cast<object>()
            .OfType<VehicleConfigItem>()
            .ToList();
    }

    private void OpenBulkEdit_Click(object sender, RoutedEventArgs e)
    {
        var selectedModItems = GetSelectedModConfigs().ToList();
        var filteredItems = GetFilteredConfigSnapshot();
        var flaggedItems = FlaggedConfigs.ToList();
        var window = new BulkEditWindow(selectedModItems, filteredItems, flaggedItems, GetSelectedModDisplayName())
        {
            Owner = this
        };

        if (window.ShowDialog() != true || window.Request == null)
        {
            return;
        }

        ApplyBulkEdit(window.Request);
    }

    private void ApplyBulkEdit(BulkEditRequest request)
    {
        var targets = request.Scope switch
        {
            BulkEditScope.SelectedMod => GetSelectedModConfigs().Distinct().ToList(),
            BulkEditScope.FlaggedConfigs => FlaggedConfigs.Distinct().ToList(),
            _ => GetFilteredConfigSnapshot().Distinct().ToList()
        };

        if (targets.Count == 0)
        {
            System.Windows.MessageBox.Show("There are no configs in the selected bulk-edit scope.", "Bulk Edit Fields", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            $"Apply the selected field changes to {targets.Count} config(s)?",
            "Confirm Bulk Edit",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var writes = new List<PendingConfigWrite>();
        var updated = 0;
        foreach (var item in targets)
        {
            try
            {
                var updatedJson = BuildJsonForItem(item);
                ApplyBulkEditRequestToJson(updatedJson, request);
                item.Json = updatedJson;
                var vehicleRoot = ResolveVehicleInfoRoot(item);
                UpdateItemFromJson(item, updatedJson, vehicleRoot);
                writes.Add(new PendingConfigWrite
                {
                    Item = item,
                    Json = updatedJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                });
                item.LastAutoFillStatus = "Bulk edit applied";
                item.LastAutoFillSource = request.Scope switch
                {
                    BulkEditScope.SelectedMod => "Bulk edit · selected mod",
                    BulkEditScope.FlaggedConfigs => "Bulk edit · flagged configs",
                    _ => "Bulk edit · filtered results"
                };
                item.LastAutoFillDetail = BuildBulkEditSummary(request);
                updated++;
            }
            catch (Exception ex)
            {
                AppendScrapeLog($"BULK EDIT FAILED {DescribeItem(item)} :: {ex}");
            }
        }

        if (writes.Count == 0)
        {
            System.Windows.MessageBox.Show("No configs could be prepared for bulk editing.", "Bulk Edit Fields", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            WriteConfigBatch(writes, mirrorToVehicles: true);
            _configsView?.Refresh();
            if (_selected != null)
            {
                LoadForm(_selected);
                SetDirty(false);
            }
            RefreshAllWorkspaceState(persist: true);
            AppendScrapeLog($"BULK EDIT SUCCESS {updated} config(s) :: {BuildBulkEditSummary(request)}");
            System.Windows.MessageBox.Show($"Bulk edit applied to {updated} config(s).", "Bulk Edit Fields", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendScrapeLog($"BULK EDIT WRITE FAILED :: {ex}");
            System.Windows.MessageBox.Show("Bulk edit failed while writing files." + Environment.NewLine + Environment.NewLine + ex.Message, "Bulk Edit Fields", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void ApplyBulkEditRequestToJson(JsonObject root, BulkEditRequest request)
    {
        if (request.ApplyInsuranceClass)
        {
            root["InsuranceClass"] = request.InsuranceClass;
        }

        if (request.ApplyPopulation)
        {
            if (request.Population.HasValue)
            {
                root["Population"] = request.Population.Value;
            }
            else
            {
                root.Remove("Population");
            }
        }

        if (request.ApplyValue && request.Value.HasValue)
        {
            root["Value"] = request.Value.Value;
        }

        if (request.ApplyBodyStyle)
        {
            root["Body Style"] = request.BodyStyle;
        }

        if (request.ApplyType)
        {
            root["Type"] = request.Type;
        }

        if (request.ApplyConfigType)
        {
            root["Config Type"] = request.ConfigType;
        }
    }

    private static string BuildBulkEditSummary(BulkEditRequest request)
    {
        var parts = new List<string>();
        if (request.ApplyInsuranceClass) parts.Add($"Insurance Class={request.InsuranceClass}");
        if (request.ApplyPopulation) parts.Add($"Population={request.Population}");
        if (request.ApplyValue) parts.Add($"Value={request.Value:0}");
        if (request.ApplyBodyStyle) parts.Add($"Body Style={request.BodyStyle}");
        if (request.ApplyType) parts.Add($"Type={request.Type}");
        if (request.ApplyConfigType) parts.Add($"Config Type={request.ConfigType}");
        return string.Join(" | ", parts);
    }

    private async Task ApplyLicensePlateChangesAsync(IReadOnlyList<VehicleConfigItem> items, bool clearMode, string plateText, string scopeLabel)
    {
        if (items.Count == 0)
        {
            System.Windows.MessageBox.Show("There were no matching .pc configs to update.", "License Plate Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var status = clearMode ? "Removing hardcoded license plates..." : "Applying shared license plate text...";
        var workItems = items
            .Select((item, index) => new PlateApplyWorkItem
            {
                Index = index,
                SourcePath = item.SourcePath ?? string.Empty,
                ConfigPcPath = item.ConfigPcPath ?? string.Empty,
                IsZip = item.IsZip,
                HasConfigPc = item.HasConfigPc,
                ModName = item.ModName ?? string.Empty,
                ConfigKey = item.ConfigKey ?? string.Empty,
                ItemLabel = DescribeItem(item)
            })
            .ToList();

        BeginAutoFillProgress(workItems.Count, status);
        AutoFillOverlayCancelButton.IsEnabled = false;

        PlateApplyBatchResult batchResult = new();
        var progress = new Progress<PlateApplyProgress>(p =>
        {
            UpdateAutoFillProgress(p.Processed, workItems.Count, p.Status, p.Detail);
        });

        try
        {
            var backupsEnabled = BackupToggleSwitch?.IsOn == true;
            batchResult = await Task.Run(() => ExecutePlateApplyBatch(workItems, clearMode, plateText, backupsEnabled, status, progress));

            foreach (var result in batchResult.Results.OrderBy(x => x.Index))
            {
                var item = items[result.Index];
                if (result.Outcome == PlateApplyOutcome.Changed)
                {
                    item.CurrentLicensePlate = clearMode ? null : plateText;
                    item.HasConfigPc = true;
                    item.NotifyChanges();
                }
            }

            _configsView?.Refresh();
            if (_selected != null)
            {
                LoadForm(_selected);
                SetDirty(false);
            }

            EndAutoFillProgress($"Plate operation complete. Changed: {batchResult.Changed}. Skipped: {batchResult.Skipped}. Failed: {batchResult.Failed}.", $"Scope: {scopeLabel}");
        }
        finally
        {
            HideAutoFillOverlay();
            AutoFillOverlayCancelButton.IsEnabled = false;
        }

        var summary = $"Scope: {scopeLabel}. Changed: {batchResult.Changed}. Skipped: {batchResult.Skipped}. Failed: {batchResult.Failed}.";
        if (batchResult.Errors.Count > 0)
        {
            summary += "\n\n" + string.Join("\n", batchResult.Errors.Take(8));
            if (batchResult.Errors.Count > 8)
            {
                summary += $"\n...and {batchResult.Errors.Count - 8} more.";
            }
        }

        System.Windows.MessageBox.Show(summary, "License Plate Manager", MessageBoxButton.OK, batchResult.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private static PlateApplyBatchResult ExecutePlateApplyBatch(
        List<PlateApplyWorkItem> workItems,
        bool clearMode,
        string plateText,
        bool backupsEnabled,
        string status,
        IProgress<PlateApplyProgress>? progress)
    {
        var result = new PlateApplyBatchResult();
        var processed = 0;

        void report(string detail)
        {
            progress?.Report(new PlateApplyProgress
            {
                Processed = processed,
                Status = processed >= workItems.Count ? $"{status} {processed}/{workItems.Count}" : status,
                Detail = detail
            });
        }

        foreach (var fileItem in workItems.Where(x => !x.IsZip))
        {
            report(fileItem.ItemLabel);
            var itemResult = ProcessFilePlateApply(fileItem, clearMode, plateText, backupsEnabled);
            result.Results.Add(itemResult);
            if (itemResult.Outcome == PlateApplyOutcome.Changed) result.Changed++;
            else if (itemResult.Outcome == PlateApplyOutcome.Skipped) result.Skipped++;
            else result.Failed++;
            if (!string.IsNullOrWhiteSpace(itemResult.ErrorMessage)) result.Errors.Add(itemResult.ErrorMessage!);
            processed++;
            report(fileItem.ItemLabel);
        }

        foreach (var zipGroup in workItems.Where(x => x.IsZip).GroupBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var groupResults = ProcessZipPlateApplyGroup(zipGroup.Key, zipGroup.ToList(), clearMode, plateText, backupsEnabled, progress, status, ref processed, workItems.Count);
            foreach (var itemResult in groupResults)
            {
                result.Results.Add(itemResult);
                if (itemResult.Outcome == PlateApplyOutcome.Changed) result.Changed++;
                else if (itemResult.Outcome == PlateApplyOutcome.Skipped) result.Skipped++;
                else result.Failed++;
                if (!string.IsNullOrWhiteSpace(itemResult.ErrorMessage)) result.Errors.Add(itemResult.ErrorMessage!);
            }
        }

        return result;
    }

    private static PlateApplyItemResult ProcessFilePlateApply(PlateApplyWorkItem item, bool clearMode, string plateText, bool backupsEnabled)
    {
        try
        {
            if (!item.HasConfigPc || string.IsNullOrWhiteSpace(item.ConfigPcPath))
            {
                return PlateApplyItemResult.Skipped(item.Index);
            }

            var filePath = item.ConfigPcPath.Replace('/', Path.DirectorySeparatorChar);
            if (!File.Exists(filePath))
            {
                return PlateApplyItemResult.Skipped(item.Index);
            }

            var configText = File.ReadAllText(filePath);
            if (!TryRenderUpdatedLicensePlateJson(configText, clearMode, plateText, out var rendered, out var changed))
            {
                return PlateApplyItemResult.Skipped(item.Index);
            }

            if (!changed || string.IsNullOrWhiteSpace(rendered))
            {
                return PlateApplyItemResult.Skipped(item.Index);
            }

            if (backupsEnabled)
            {
                EnsureBackup(filePath);
            }

            File.WriteAllText(filePath, rendered, new UTF8Encoding(false));

            var verifiedText = File.ReadAllText(filePath);
            var verifiedValue = TryReadLicensePlateFromJsonText(verifiedText);
            var expectedValue = clearMode ? null : plateText;
            if (!string.Equals(verifiedValue ?? string.Empty, expectedValue ?? string.Empty, StringComparison.Ordinal))
            {
                return PlateApplyItemResult.Failed(item.Index, $"{item.ModName} / {item.ConfigKey}: verification failed after writing file.");
            }

            return PlateApplyItemResult.Changed(item.Index);
        }
        catch (Exception ex)
        {
            return PlateApplyItemResult.Failed(item.Index, $"{item.ModName} / {item.ConfigKey}: {ex.Message}");
        }
    }

    private static List<PlateApplyItemResult> ProcessZipPlateApplyGroup(
        string zipPath,
        List<PlateApplyWorkItem> items,
        bool clearMode,
        string plateText,
        bool backupsEnabled,
        IProgress<PlateApplyProgress>? progress,
        string status,
        ref int processed,
        int totalCount)
    {
        var results = new List<PlateApplyItemResult>();
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            foreach (var item in items)
            {
                processed++;
                progress?.Report(new PlateApplyProgress { Processed = processed, Status = $"{status} {processed}/{totalCount}", Detail = item.ItemLabel });
                results.Add(PlateApplyItemResult.Skipped(item.Index));
            }
            return results;
        }

        try
        {
            var changesByEntry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var replacementNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pendingChangedItems = new List<PlateApplyWorkItem>();

            using (var sourceArchive = ZipFile.OpenRead(zipPath))
            {
                foreach (var item in items)
                {
                    progress?.Report(new PlateApplyProgress { Processed = processed, Status = status, Detail = item.ItemLabel });
                    if (!item.HasConfigPc || string.IsNullOrWhiteSpace(item.ConfigPcPath))
                    {
                        results.Add(PlateApplyItemResult.Skipped(item.Index));
                        processed++;
                        progress?.Report(new PlateApplyProgress { Processed = processed, Status = $"{status} {processed}/{totalCount}", Detail = item.ItemLabel });
                        continue;
                    }

                    var normalizedEntryPath = NormalizeZipEntryPath(item.ConfigPcPath);
                    var entry = FindZipEntry(sourceArchive, normalizedEntryPath);
                    if (entry == null)
                    {
                        results.Add(PlateApplyItemResult.Skipped(item.Index));
                        processed++;
                        progress?.Report(new PlateApplyProgress { Processed = processed, Status = $"{status} {processed}/{totalCount}", Detail = item.ItemLabel });
                        continue;
                    }

                    string configText;
                    using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, true))
                    {
                        configText = reader.ReadToEnd();
                    }

                    if (!TryRenderUpdatedLicensePlateJson(configText, clearMode, plateText, out var rendered, out var changed))
                    {
                        results.Add(PlateApplyItemResult.Skipped(item.Index));
                    }
                    else if (changed && !string.IsNullOrWhiteSpace(rendered))
                    {
                        changesByEntry[normalizedEntryPath] = rendered!;
                        replacementNames[normalizedEntryPath] = entry.FullName;
                        pendingChangedItems.Add(item);
                    }
                    else
                    {
                        results.Add(PlateApplyItemResult.Skipped(item.Index));
                    }

                    processed++;
                    progress?.Report(new PlateApplyProgress { Processed = processed, Status = $"{status} {processed}/{totalCount}", Detail = item.ItemLabel });
                }
            }

            if (changesByEntry.Count > 0)
            {
                if (backupsEnabled)
                {
                    EnsureBackup(zipPath);
                }

                WriteZipEntries(zipPath, changesByEntry, replacementNames);

                using (var verifyArchive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var item in pendingChangedItems)
                    {
                        var normalizedEntryPath = NormalizeZipEntryPath(item.ConfigPcPath);
                        var verifyEntry = FindZipEntry(verifyArchive, normalizedEntryPath);
                        if (verifyEntry == null)
                        {
                            results.Add(PlateApplyItemResult.Failed(item.Index, $"{item.ModName} / {item.ConfigKey}: rewritten entry could not be found for verification."));
                            continue;
                        }

                        string verifyText;
                        using (var verifyReader = new StreamReader(verifyEntry.Open(), Encoding.UTF8, true))
                        {
                            verifyText = verifyReader.ReadToEnd();
                        }

                        var verifiedValue = TryReadLicensePlateFromJsonText(verifyText);
                        var expectedValue = clearMode ? null : plateText;
                        if (string.Equals(verifiedValue ?? string.Empty, expectedValue ?? string.Empty, StringComparison.Ordinal))
                        {
                            results.Add(PlateApplyItemResult.Changed(item.Index));
                        }
                        else
                        {
                            results.Add(PlateApplyItemResult.Failed(item.Index, $"{item.ModName} / {item.ConfigKey}: verification failed after rewriting zip."));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            var finalized = new HashSet<int>(results.Select(r => r.Index));
            foreach (var item in items)
            {
                if (finalized.Contains(item.Index))
                {
                    continue;
                }

                results.Add(PlateApplyItemResult.Failed(item.Index, $"{item.ModName} / {item.ConfigKey}: {ex.Message}"));
            }
        }

        return results;
    }

    private static bool TryRenderUpdatedLicensePlateJson(string configText, bool clearMode, string plateText, out string? rendered, out bool changed)
    {
        rendered = null;
        changed = false;

        if (ParseJson(configText) is not JsonObject root)
        {
            throw new InvalidDataException("The config .pc file could not be parsed as JSON.");
        }

        if (clearMode)
        {
            if (!root.ContainsKey("licenseName"))
            {
                return false;
            }

            root.Remove("licenseName");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(plateText))
            {
                return false;
            }

            var currentValue = ReadString(root, "licenseName") ?? string.Empty;
            if (string.Equals(currentValue, plateText, StringComparison.Ordinal))
            {
                return false;
            }

            root["licenseName"] = plateText;
        }

        rendered = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        changed = true;
        return true;
    }

    private static string? TryReadLicensePlateFromJsonText(string jsonText)
    {
        try
        {
            var root = ParseJson(jsonText ?? string.Empty);
            return ReadString(root, "licenseName");
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureBackup(string targetPath)
    {
        var backupPath = targetPath + ".bak";
        if (!File.Exists(backupPath))
        {
            File.Copy(targetPath, backupPath);
        }
    }

    private sealed class PlateApplyWorkItem
    {
        public int Index { get; init; }
        public string SourcePath { get; init; } = string.Empty;
        public string ConfigPcPath { get; init; } = string.Empty;
        public bool IsZip { get; init; }
        public bool HasConfigPc { get; init; }
        public string ModName { get; init; } = string.Empty;
        public string ConfigKey { get; init; } = string.Empty;
        public string ItemLabel { get; init; } = string.Empty;
    }

    private sealed class PlateApplyProgress
    {
        public int Processed { get; init; }
        public string Status { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
    }

    private enum PlateApplyOutcome
    {
        Changed,
        Skipped,
        Failed
    }

    private sealed class PlateApplyItemResult
    {
        public int Index { get; init; }
        public PlateApplyOutcome Outcome { get; init; }
        public string? ErrorMessage { get; init; }

        public static PlateApplyItemResult Changed(int index) => new() { Index = index, Outcome = PlateApplyOutcome.Changed };
        public static PlateApplyItemResult Skipped(int index) => new() { Index = index, Outcome = PlateApplyOutcome.Skipped };
        public static PlateApplyItemResult Failed(int index, string errorMessage) => new() { Index = index, Outcome = PlateApplyOutcome.Failed, ErrorMessage = errorMessage };
    }

    private sealed class PlateApplyBatchResult
    {
        public List<PlateApplyItemResult> Results { get; } = new();
        public List<string> Errors { get; } = new();
        public int Changed { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
    }

    private void ApplyInferenceToItem(VehicleConfigItem item)
    {
        var inference = _inferenceService.Infer(item);
        ApplyInferenceResultToItem(item, inference);
    }

    private void ApplyInferenceResultToItem(VehicleConfigItem item, VehicleInferenceResult inference)
    {
        item.InferenceReason = inference.InferenceReason;
        item.IsSuspicious = inference.IsSuspicious;
        item.ReviewReason = inference.IsSuspicious ? inference.SuspicionReason ?? inference.BrandEvidence : null;

        if (!inference.HasAnyValue)
        {
            ApplyDefaultAutoFillFallbacksToItem(item);
            item.NotifyChanges();
            return;
        }

        if (!string.IsNullOrWhiteSpace(inference.Model)) item.VehicleName = inference.Model;
        if (!string.IsNullOrWhiteSpace(inference.Brand)) item.Brand = inference.Brand;
        if (!string.IsNullOrWhiteSpace(inference.Country)) item.Country = inference.Country;
        if (!string.IsNullOrWhiteSpace(inference.Type)) item.Type = inference.Type;
        if (!string.IsNullOrWhiteSpace(inference.BodyStyle)) item.BodyStyle = inference.BodyStyle;
        if (!string.IsNullOrWhiteSpace(inference.ConfigType)) item.ConfigType = inference.ConfigType;
        if (!string.IsNullOrWhiteSpace(inference.Configuration)) item.Configuration = inference.Configuration;
        if (!string.IsNullOrWhiteSpace(inference.InsuranceClass)) item.InsuranceClass = inference.InsuranceClass;
        if (inference.YearMin.HasValue) item.YearMin = inference.YearMin;
        if (inference.YearMax.HasValue) item.YearMax = inference.YearMax;
        if (inference.Value.HasValue && inference.Value.Value > 0) item.Value = inference.Value;
        if (inference.Population.HasValue && inference.Population.Value > 0) item.Population = inference.Population;

        ApplyDefaultAutoFillFallbacksToItem(item);
        item.NotifyChanges();
    }

    private string DescribeItem(VehicleConfigItem item)
    {
        var name = !string.IsNullOrWhiteSpace(item.VehicleName) ? item.VehicleName : (!string.IsNullOrWhiteSpace(item.Configuration) ? item.Configuration : item.ConfigKey);
        return $"{item.ModName} / {name}";
    }

    private string BuildInferenceProgressDetail(VehicleInferenceResult inference)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(inference.Brand) || !string.IsNullOrWhiteSpace(inference.Configuration))
        {
            parts.Add($"Vehicle: {string.Join(" ", new[] { inference.Brand, inference.Configuration }.Where(x => !string.IsNullOrWhiteSpace(x)))}".Trim());
        }

        if (!string.IsNullOrWhiteSpace(inference.InferenceReason))
        {
            parts.Add($"Match: {inference.InferenceReason}");
        }

        if (inference.Value.HasValue)
        {
            var source = string.IsNullOrWhiteSpace(inference.ValueSource) ? "Estimated value" : inference.ValueSource;
            parts.Add($"Value: ${Math.Round(inference.Value.Value):N0} ({source})");
        }

        if (inference.Population.HasValue)
        {
            parts.Add($"Population: {inference.Population.Value:N0}");
        }

        if (!string.IsNullOrWhiteSpace(inference.BrandEvidence))
        {
            parts.Add($"Reason: {inference.BrandEvidence}");
        }

        if (!string.IsNullOrWhiteSpace(inference.ValueEvidence))
        {
            parts.Add($"Evidence: {inference.ValueEvidence}");
        }

        if (inference.IsSuspicious && !string.IsNullOrWhiteSpace(inference.SuspicionReason))
        {
            parts.Add($"Review: {inference.SuspicionReason}");
        }

        return parts.Count == 0 ? "No new metadata found; defaults may have been used." : string.Join("  |  ", parts);
    }

    private void BeginAutoFillProgress(int total, string status)
    {
        AutoFillProgressBar.Visibility = Visibility.Visible;
        AutoFillProgressBar.IsIndeterminate = total <= 0;
        AutoFillProgressBar.Minimum = 0;
        AutoFillProgressBar.Maximum = Math.Max(1, total);
        AutoFillProgressBar.Value = 0;
        StatusTextBlock.Text = status;
        StatusDetailTextBlock.Text = string.Empty;
        ShowAutoFillOverlay(total, status, string.Empty);
    }

    private void UpdateAutoFillProgress(int processed, int total, string status, string detail)
    {
        AutoFillProgressBar.Visibility = Visibility.Visible;
        AutoFillProgressBar.IsIndeterminate = total <= 0;
        AutoFillProgressBar.Maximum = Math.Max(1, total);
        AutoFillProgressBar.Value = Math.Min(processed, Math.Max(1, total));
        StatusTextBlock.Text = status;
        StatusDetailTextBlock.Text = detail;
        UpdateAutoFillOverlay(processed, total, status, detail);
    }

    private void EndAutoFillProgress(string status, string detail = "")
    {
        AutoFillProgressBar.IsIndeterminate = false;
        AutoFillProgressBar.Visibility = Visibility.Collapsed;
        AutoFillProgressBar.Value = 0;
        StatusTextBlock.Text = status;
        StatusDetailTextBlock.Text = detail;
        AutoFillOverlayProgressBar.IsIndeterminate = false;
        AutoFillOverlayProgressBar.Value = 0;
        AutoFillOverlayStatusTextBlock.Text = status;
        AutoFillOverlayDetailTextBlock.Text = detail;
    }

    private void ShowAutoFillOverlay(int total, string status, string detail = "")
    {
        AutoFillOverlay.Visibility = Visibility.Visible;
        AutoFillOverlayProgressBar.IsIndeterminate = total <= 0;
        AutoFillOverlayProgressBar.Minimum = 0;
        AutoFillOverlayProgressBar.Maximum = Math.Max(1, total);
        AutoFillOverlayProgressBar.Value = 0;
        AutoFillOverlayStatusTextBlock.Text = status;
        AutoFillOverlayDetailTextBlock.Text = detail;
        TitleBar.IsHitTestVisible = false;
        MainWorkspaceGrid.IsEnabled = false;
        AutoFillOverlay.IsHitTestVisible = true;
        AutoFillOverlayCancelButton.IsEnabled = true;
    }

    private void UpdateAutoFillOverlay(int processed, int total, string status, string detail)
    {
        AutoFillOverlayProgressBar.IsIndeterminate = total <= 0;
        AutoFillOverlayProgressBar.Maximum = Math.Max(1, total);
        AutoFillOverlayProgressBar.Value = Math.Min(processed, Math.Max(1, total));
        AutoFillOverlayStatusTextBlock.Text = status;
        AutoFillOverlayDetailTextBlock.Text = detail;
    }

    private void HideAutoFillOverlay()
    {
        AutoFillOverlay.Visibility = Visibility.Collapsed;
        TitleBar.IsHitTestVisible = true;
        MainWorkspaceGrid.IsEnabled = true;
        AutoFillOverlayCancelButton.IsEnabled = false;
    }

    private static void AppendScrapeLog(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(ScrapeLogPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.AppendAllText(ScrapeLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private void SetItemAutoFillState(VehicleConfigItem item, string status, string? source = null, string? detail = null)
    {
        if (string.Equals(item.LastAutoFillStatus, status, StringComparison.Ordinal) &&
            string.Equals(item.LastAutoFillSource, source, StringComparison.Ordinal) &&
            string.Equals(item.LastAutoFillDetail, detail, StringComparison.Ordinal))
        {
            return;
        }

        item.LastAutoFillStatus = status;
        item.LastAutoFillSource = source;
        item.LastAutoFillDetail = detail;
        item.NotifyChanges();
    }

    private bool ShouldPushAutoFillUiProgress(bool force = false)
    {
        if (force)
        {
            lock (_autoFillUiProgressGate)
            {
                _lastAutoFillUiProgressUpdateUtc = DateTime.UtcNow;
            }
            return true;
        }

        var now = DateTime.UtcNow;
        lock (_autoFillUiProgressGate)
        {
            if ((now - _lastAutoFillUiProgressUpdateUtc).TotalMilliseconds < AutoFillUiProgressIntervalMs)
            {
                return false;
            }

            _lastAutoFillUiProgressUpdateUtc = now;
            return true;
        }
    }

    private void ApplyDefaultAutoFillFallbacksToItem(VehicleConfigItem item)
    {
        var defaultYear = _autoFillSettings.Year ?? DateTime.Now.Year;

        if (_autoFillSettings.UseBrand && VehicleConfigItem.IsMissingText(item.Brand)) item.Brand = _autoFillSettings.Brand;
        if (_autoFillSettings.UseCountry && VehicleConfigItem.IsMissingText(item.Country)) item.Country = _autoFillSettings.Country;
        if (_autoFillSettings.UseType && VehicleConfigItem.IsMissingText(item.Type)) item.Type = _autoFillSettings.Type;
        if (_autoFillSettings.UseBodyStyle && VehicleConfigItem.IsMissingText(item.BodyStyle)) item.BodyStyle = _autoFillSettings.BodyStyle;
        if (_autoFillSettings.UseConfigType && VehicleConfigItem.IsMissingText(item.ConfigType)) item.ConfigType = _autoFillSettings.ConfigType;

        if (_autoFillSettings.UseYear)
        {
            item.YearMin ??= defaultYear;
            item.YearMax ??= defaultYear;
        }

        if (_autoFillSettings.UseValue && (!item.Value.HasValue || item.Value.Value <= 0))
        {
            item.Value = _autoFillSettings.Value;
        }

        if (_autoFillSettings.UsePopulation && (!item.Population.HasValue || item.Population.Value <= 0))
        {
            item.Population = _autoFillSettings.Population;
        }

        if (VehicleConfigItem.IsMissingText(item.InsuranceClass))
        {
            item.InsuranceClass = DefaultInsuranceClass;
        }
    }

    private void ApplyInferenceToForm(VehicleConfigItem item)
    {
        var inference = _inferenceService.Infer(item);
        if (!inference.HasAnyValue)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(BrandTextBox.Text) && !string.IsNullOrWhiteSpace(inference.Brand)) BrandTextBox.Text = inference.Brand;
        if (string.IsNullOrWhiteSpace(CountryTextBox.Text) && !string.IsNullOrWhiteSpace(inference.Country)) CountryTextBox.Text = inference.Country;
        if (string.IsNullOrWhiteSpace(TypeTextBox.Text) && !string.IsNullOrWhiteSpace(inference.Type)) TypeTextBox.Text = inference.Type;
        if (string.IsNullOrWhiteSpace(BodyStyleTextBox.Text) && !string.IsNullOrWhiteSpace(inference.BodyStyle)) BodyStyleTextBox.Text = inference.BodyStyle;
        if (string.IsNullOrWhiteSpace(ConfigTypeTextBox.Text) && !string.IsNullOrWhiteSpace(inference.ConfigType)) ConfigTypeTextBox.Text = inference.ConfigType;
        if (string.IsNullOrWhiteSpace(ConfigurationTextBox.Text) && !string.IsNullOrWhiteSpace(inference.Configuration)) ConfigurationTextBox.Text = inference.Configuration;

        var currentInsurance = GetSelectedInsuranceClass();
        if ((string.IsNullOrWhiteSpace(currentInsurance) || currentInsurance == DefaultInsuranceClass) && !string.IsNullOrWhiteSpace(inference.InsuranceClass))
        {
            SelectInsuranceClass(inference.InsuranceClass);
        }

        if (!ReadInteger(YearMinUpDown.Value).HasValue && inference.YearMin.HasValue) YearMinUpDown.Value = inference.YearMin.Value;
        if (!ReadInteger(YearMaxUpDown.Value).HasValue && inference.YearMax.HasValue) YearMaxUpDown.Value = inference.YearMax.Value;
        if (!ValueUpDown.Value.HasValue && inference.Value.HasValue) ValueUpDown.Value = inference.Value.Value;
        if (!ReadInteger(PopulationUpDown.Value).HasValue && inference.Population.HasValue)
        {
            PopulationUpDown.Value = inference.Population.Value;
            UpdatePopulationPresetFromValue(inference.Population.Value);
        }
    }

    private async void AutoFillAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isAutoFillAllRunning)
        {
            return;
        }

        if (Configs.Count == 0)
        {
            System.Windows.MessageBox.Show("Scan mods first.", "Auto-fill all", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmationMessage = $"Auto Fill All will overwrite autofill-supported fields for all detected mod files ({Configs.Count} configs).\n\n" +
            "This can take an extended amount of time depending on how many mods are installed. Suspicious or unresolved mods may be sent to the review wizard afterward.\n\n" +
            "Do you want to continue?";
        var confirmation = System.Windows.MessageBox.Show(
            confirmationMessage,
            "Confirm Auto Fill All",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _isAutoFillAllRunning = true;
        _autoFillAllCts = new CancellationTokenSource();
        BrowseButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        AutoFillAllButton.IsEnabled = false;
        CancelAutoFillAllButton.IsEnabled = true;
        OpenAutoFillSettingsFromPageButton.IsEnabled = false;
        if (BulkEditFieldsButton != null) BulkEditFieldsButton.IsEnabled = false;
        BeginAutoFillProgress(Configs.Count, "Auto-filling all configs...");
        _lastAutoFillUiProgressUpdateUtc = DateTime.MinValue;
        RealVehiclePricingService.SetBulkAutofillMode(true);
        AppendScrapeLog($"=== Auto Fill All started for {Configs.Count} configs ===");

        try
        {
            var updated = 0;
            var saved = 0;
            var failed = 0;
            var processed = 0;
            var canceled = false;
            var sourceGroups = Configs
                .GroupBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var sourceGroup in sourceGroups)
            {
                if (_autoFillAllCts.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                var sourceItems = sourceGroup.ToList();
                var pendingWrites = new List<PendingConfigWrite>();
                var completedItems = new List<(VehicleConfigItem Item, VehicleInferenceResult Inference, string Detail, string Source)>();
                var sourceLabel = Path.GetFileName(sourceGroup.Key);

                foreach (var item in sourceItems)
                {
                    if (_autoFillAllCts.IsCancellationRequested)
                    {
                        canceled = true;
                        break;
                    }

                    var itemLabel = DescribeItem(item);
                    SetItemAutoFillState(item, "Running", "Working", $"Starting {itemLabel}");
                    UpdateAutoFillProgress(processed, Configs.Count, $"Auto-filling all configs... {processed}/{Configs.Count}", $"Starting {itemLabel}");
                    AppendScrapeLog($"START {itemLabel}");

                    try
                    {
                        var inference = await Task.Run(() => _inferenceService.Infer(item, detail =>
                        {
                            if (ShouldPushAutoFillUiProgress())
                            {
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    SetItemAutoFillState(item, "Running", "Working", detail);
                                    UpdateAutoFillProgress(processed, Configs.Count, $"Auto-filling all configs... {processed}/{Configs.Count}", $"{itemLabel}: {detail}");
                                }));
                            }
                        }, _autoFillAllCts.Token), _autoFillAllCts.Token);

                        var shouldHoldForReview = _holdWeakMatchesForReview && (inference.IsSuspicious || inference.ConfidenceScore < 55 || string.Equals(inference.ConfidenceTier, "Fallback", StringComparison.OrdinalIgnoreCase)) && !item.HasSourceHints;
                        var detail = BuildInferenceProgressDetail(inference);
                        if (shouldHoldForReview)
                        {
                            item.InferenceReason = inference.InferenceReason;
                            item.IsSuspicious = true;
                            item.ReviewReason = !string.IsNullOrWhiteSpace(inference.SuspicionReason)
                                ? inference.SuspicionReason
                                : $"Held for review due to low-confidence or conflicting match. {detail}";
                            processed++;
                            SetItemAutoFillState(item, "Needs review", "Held for review", detail);
                            item.NotifyChanges();
                            UpdateAutoFillProgress(processed, Configs.Count, $"Auto-filling all configs... {processed}/{Configs.Count}", $"{itemLabel}: held for review");
                            AppendScrapeLog($"HELD {itemLabel} :: {detail}");
                            continue;
                        }

                        ApplyInferenceResultToItem(item, inference);
                        var json = BuildJsonForItem(item);
                        item.Json = json;
                        var rendered = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                        pendingWrites.Add(new PendingConfigWrite { Item = item, Json = rendered });
                        var source = string.IsNullOrWhiteSpace(inference.ValueSource) ? "Heuristic/local rules" : inference.ValueSource!;
                        completedItems.Add((item, inference, detail, source));
                        SetItemAutoFillState(item, "Running", "Queued", $"Prepared changes for {itemLabel}");
                    }
                    catch (OperationCanceledException)
                    {
                        canceled = true;
                        SetItemAutoFillState(item, "Canceled", "Canceled", "Operation canceled by user.");
                        AppendScrapeLog($"CANCELED {itemLabel}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        processed++;
                        SetItemAutoFillState(item, "Failed", "Error", ex.Message);
                        UpdateAutoFillProgress(processed, Configs.Count, $"Auto-filling all configs... {processed}/{Configs.Count}", $"Failed on {itemLabel}: {ex.Message}");
                        AppendScrapeLog($"FAILED {itemLabel} :: {ex}");
                    }

                    await Task.Yield();
                }

                if (completedItems.Count > 0)
                {
                    try
                    {
                        UpdateAutoFillProgress(processed, Configs.Count, $"Auto-filling all configs... {processed}/{Configs.Count}", $"Saving {sourceLabel} ({completedItems.Count} configs)");
                        WriteConfigBatch(pendingWrites, mirrorToVehicles: true);

                        foreach (var completed in completedItems)
                        {
                            completed.Item.NotifyChanges();
                            updated++;
                            saved++;
                            processed++;
                            SetItemAutoFillState(completed.Item, "Completed", completed.Source, completed.Detail);
                            UpdateAutoFillProgress(processed, Configs.Count, $"Auto-filling all configs... {processed}/{Configs.Count}", $"{DescribeItem(completed.Item)}: {completed.Detail}");
                            AppendScrapeLog($"SUCCESS {DescribeItem(completed.Item)} :: Source={completed.Source} :: {completed.Detail}");
                        }
                    }
                    catch (Exception ex)
                    {
                        foreach (var completed in completedItems)
                        {
                            failed++;
                            processed++;
                            SetItemAutoFillState(completed.Item, "Failed", "Error", ex.Message);
                            UpdateAutoFillProgress(processed, Configs.Count, $"Auto-filling all configs... {processed}/{Configs.Count}", $"Failed saving {DescribeItem(completed.Item)}: {ex.Message}");
                            AppendScrapeLog($"FAILED {DescribeItem(completed.Item)} :: {ex}");
                        }
                    }
                }

                if (canceled)
                {
                    break;
                }
            }

            _configsView?.Refresh();
            if (_selected != null)
            {
                LoadForm(_selected);
                SetDirty(false);
            }

            if (!canceled)
            {
                var reviewOutcome = await RunRenamerWizardIfNeededAsync();
                if (reviewOutcome.ReviewedCount > 0 || reviewOutcome.IgnoredCount > 0 || reviewOutcome.RetriedCount > 0)
                {
                    AppendScrapeLog($"REVIEW outcome :: retried={reviewOutcome.RetriedCount} ignored={reviewOutcome.IgnoredCount} reviewed={reviewOutcome.ReviewedCount}");
                }
            }

            if (canceled)
            {
                EndAutoFillProgress($"Auto-fill canceled at {processed}/{Configs.Count}. Updated: {updated}. Saved: {saved}. Failed: {failed}.", $"See scrape log: {ScrapeLogPath}");
            }
            else
            {
                EndAutoFillProgress($"Auto-fill complete. Updated: {updated}. Saved: {saved}. Failed: {failed}.", $"See scrape log: {ScrapeLogPath}");
                if (_openReviewQueueAfterAutoFill && FlaggedConfigs.Count > 0)
                {
                    SetWorkspacePage("ReviewQueue");
                }
            }
            AppendScrapeLog("=== Auto Fill All finished ===");
        }
        finally
        {
            _autoFillAllCts?.Dispose();
            _autoFillAllCts = null;
            _isAutoFillAllRunning = false;
            BrowseButton.IsEnabled = true;
            ScanButton.IsEnabled = true;
            AutoFillAllButton.IsEnabled = true;
            CancelAutoFillAllButton.IsEnabled = false;
            OpenAutoFillSettingsFromPageButton.IsEnabled = true;
            if (BulkEditFieldsButton != null) BulkEditFieldsButton.IsEnabled = true;
            try
            {
                _inferenceService.FlushPricingCache();
            }
            catch
            {
                // keep non-blocking
            }
            RealVehiclePricingService.SetBulkAutofillMode(false);
            HideAutoFillOverlay();
        }
    }

    private void CancelAutoFillAll_Click(object sender, RoutedEventArgs e)
    {
        _autoFillAllCts?.Cancel();
    }

    private JsonObject BuildJsonForItem(VehicleConfigItem item)
    {
        JsonObject root;
        if (item.Json is JsonObject existing)
        {
            root = (JsonObject)existing.DeepClone();
        }
        else
        {
            root = new JsonObject();
        }

        SetJsonStringOrRemove(root, "Name", item.VehicleName ?? item.ConfigKey);
        SetJsonStringOrRemove(root, "Brand", item.Brand);
        SetJsonStringOrRemove(root, "Country", item.Country);
        SetJsonStringOrRemove(root, "Type", item.Type);
        SetJsonStringOrRemove(root, "Body Style", item.BodyStyle);
        SetJsonStringOrRemove(root, "Config Type", item.ConfigType);
        SetJsonStringOrRemove(root, "Configuration", item.Configuration);
        SetJsonStringOrRemove(root, "InsuranceClass", item.InsuranceClass ?? DefaultInsuranceClass);
        SetJsonStringOrRemove(root, "Region", DefaultRegion);
        SetJsonNumberOrRemove(root, "Value", item.Value);
        SetJsonIntegerOrRemove(root, "Population", item.Population);
        SetYearsOrRemove(root, item.YearMin, item.YearMax);

        return root;
    }


    private JsonNode? ResolveVehicleInfoRoot(VehicleConfigItem item)
    {
        if (item.VehicleInfoJson != null)
        {
            return item.VehicleInfoJson;
        }

        var vehicleInfoPath = !string.IsNullOrWhiteSpace(item.VehicleInfoPath)
            ? item.VehicleInfoPath
            : BuildVehicleInfoPath(item.InfoPath, item.IsZip);
        var root = TryLoadVehicleInfoRoot(item.SourcePath, vehicleInfoPath, item.IsZip);
        if (root != null)
        {
            item.VehicleInfoJson = root;
            item.VehicleInfoPath = vehicleInfoPath;
        }

        return root;
    }

    private static void SetJsonStringOrRemove(JsonObject root, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            root.Remove(key);
            return;
        }

        root[key] = value.Trim();
    }

    private static void SetJsonNumberOrRemove(JsonObject root, string key, double? value)
    {
        if (!value.HasValue)
        {
            root.Remove(key);
            return;
        }

        root[key] = value.Value;
    }

    private static void SetJsonIntegerOrRemove(JsonObject root, string key, int? value)
    {
        if (!value.HasValue)
        {
            root.Remove(key);
            return;
        }

        root[key] = value.Value;
    }

    private static void SetYearsOrRemove(JsonObject root, int? min, int? max)
    {
        if (!min.HasValue && !max.HasValue)
        {
            root.Remove("Years");
            return;
        }

        var years = new JsonObject();
        if (min.HasValue)
        {
            years["min"] = min.Value;
        }
        if (max.HasValue)
        {
            years["max"] = max.Value;
        }
        root["Years"] = years;
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
        UpdateItemFromJson(_selected, updated, ResolveVehicleInfoRoot(_selected));

        var json = updated.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        try
        {
            var mirrorResult = WriteConfig(_selected, json);
            _selected.NotifyChanges();
            LoadForm(_selected);
            SetDirty(false);
            RefreshAllWorkspaceState(persist: true);
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
            UpdateItemFromJson(_selected, root, ResolveVehicleInfoRoot(_selected));
            _selected.NotifyChanges();
            LoadForm(_selected);
            SetDirty(false);
            RefreshAllWorkspaceState(persist: false);
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

        try
        {
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
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            errors++;
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

        if (MissingOnlyCheckBox?.IsChecked == true && !item.NeedsReview)
        {
            return false;
        }

        var query = SearchTextBox?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.ToLowerInvariant();
            bool contains(string? value) => !string.IsNullOrWhiteSpace(value) && value.Contains(q, StringComparison.OrdinalIgnoreCase);

            if (!(contains(item.ModName) ||
                  contains(item.ModelKey) ||
                  contains(item.ConfigKey) ||
                  contains(item.VehicleName) ||
                  contains(item.Brand) ||
                  contains(item.Configuration) ||
                  contains(item.ConfigType) ||
                  contains(item.BodyStyle) ||
                  contains(item.Type) ||
                  contains(item.Country) ||
                  contains(item.InsuranceClass) ||
                  contains(item.ContentCategory) ||
                  contains(item.SourceHintMake) ||
                  contains(item.SourceHintModel) ||
                  contains(item.LastAutoFillStatus) ||
                  contains(item.LastAutoFillSource)))
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
        _searchRefreshTimer.Stop();
        _searchRefreshTimer.Start();
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
            _configsView.SortDescriptions.Add(new SortDescription(nameof(VehicleConfigItem.NeedsReview), ListSortDirection.Descending));
            _configsView.SortDescriptions.Add(new SortDescription(nameof(VehicleConfigItem.IsSuspicious), ListSortDirection.Descending));
        }

        _configsView.SortDescriptions.Add(new SortDescription(nameof(VehicleConfigItem.VehicleName), ListSortDirection.Ascending));
    }

    private void AddFolderModConfigs(string modDir, string modName, List<VehicleConfigItem> items, ref int errors, CancellationToken token)
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

    private void AddZipModConfigs(string zipPath, List<VehicleConfigItem> items, ref int errors, CancellationToken token)
    {
        var modName = Path.GetFileNameWithoutExtension(zipPath);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var scanContext = new ZipScanContext(archive);
            foreach (var entry in scanContext.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (!IsVehicleInfoEntry(entry.FullName))
                {
                    continue;
                }

                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                var jsonText = reader.ReadToEnd();
                if (!TryCreateConfigItem(modName, zipPath, entry.FullName, true, jsonText, out var item, scanContext))
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

    private bool TryCreateConfigItem(string modName, string sourcePath, string infoPath, bool isZip, string jsonText, out VehicleConfigItem item, ZipScanContext? zipScanContext = null)
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

        var vehicleInfoPath = BuildVehicleInfoPath(infoPath, isZip);
        var vehicleRoot = TryLoadVehicleInfoRoot(sourcePath, vehicleInfoPath, isZip, zipScanContext);

        var configPcPath = BuildConfigPcPath(infoPath, configKey);
        var configPcExists = ConfigPcExists(sourcePath, configPcPath, isZip, zipScanContext);
        var currentLicensePlate = TryReadLicensePlate(sourcePath, configPcPath, isZip, zipScanContext);

        item = new VehicleConfigItem
        {
            ModName = modName,
            SourcePath = sourcePath,
            InfoPath = infoPath,
            IsZip = isZip,
            ModelKey = modelKey,
            ConfigKey = configKey,
            ConfigPcPath = configPcPath,
            HasConfigPc = configPcExists,
            CurrentLicensePlate = currentLicensePlate,
            Json = root,
            VehicleInfoJson = vehicleRoot,
            VehicleInfoPath = vehicleInfoPath
        };

        UpdateItemFromJson(item, root, vehicleRoot);
        item.NotifyChanges();
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


    private static string BuildConfigPcPath(string infoPath, string configKey)
    {
        var normalized = infoPath.Replace('\\', '/');
        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex < 0)
        {
            return configKey + ".pc";
        }

        return normalized.Substring(0, slashIndex + 1) + configKey + ".pc";
    }

    private static bool ConfigPcExists(string sourcePath, string configPcPath, bool isZip, ZipScanContext? zipScanContext = null)
    {
        try
        {
            if (!isZip)
            {
                return File.Exists(configPcPath.Replace('/', Path.DirectorySeparatorChar));
            }

            return zipScanContext?.Contains(configPcPath) ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadLicensePlate(string sourcePath, string configPcPath, bool isZip, ZipScanContext? zipScanContext = null)
    {
        try
        {
            string? jsonText;
            if (!isZip)
            {
                var filePath = configPcPath.Replace('/', Path.DirectorySeparatorChar);
                if (!File.Exists(filePath))
                {
                    return null;
                }

                jsonText = File.ReadAllText(filePath);
            }
            else
            {
                jsonText = zipScanContext?.ReadText(configPcPath);
                if (jsonText == null)
                {
                    return null;
                }
            }

            var root = ParseJson(jsonText ?? string.Empty);
            return ReadString(root, "licenseName");
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
        SelectedModInfoTextBlock.Text = $"Selected mod: {item.ModName} · Config: {item.ConfigKey}";
        RefreshLicensePlatesPageSummary();
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
        SelectedModInfoTextBlock.Text = "Select a config to enable config-only and mod-wide actions.";
        ResetFieldHighlighting();
        SetDirty(false);
        RefreshLicensePlatesPageSummary();
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
        if (VehicleConfigItem.IsMissingText(brand)) errors.Add("Brand");
        if (VehicleConfigItem.IsMissingText(country)) errors.Add("Country");
        if (VehicleConfigItem.IsMissingText(type)) errors.Add("Type");
        if (VehicleConfigItem.IsMissingText(bodyStyle)) errors.Add("Body Style");
        if (VehicleConfigItem.IsMissingText(configType)) errors.Add("Config Type");
        if (VehicleConfigItem.IsMissingText(configuration)) errors.Add("Configuration");
        if (VehicleConfigItem.IsMissingText(insuranceClass)) errors.Add("Insurance Class");

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
        if (!value.HasValue || value.Value <= 0)
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

    private static void UpdateItemFromJson(VehicleConfigItem item, JsonNode root, JsonNode? vehicleRoot = null)
    {
        if (vehicleRoot != null)
        {
            item.VehicleInfoJson = vehicleRoot;
        }

        var effectiveVehicleRoot = vehicleRoot ?? item.VehicleInfoJson;
        item.VehicleInfoName = ReadString(effectiveVehicleRoot, "Name");
        item.VehicleInfoBrand = ReadStringOrAggregate(effectiveVehicleRoot, "Brand");
        item.VehicleInfoCountry = ReadStringOrAggregate(effectiveVehicleRoot, "Country");
        item.VehicleInfoType = ReadStringOrAggregate(effectiveVehicleRoot, "Type");
        item.VehicleInfoBodyStyle = ReadStringOrAggregate(effectiveVehicleRoot, "Body Style");

        var vehicleYears = ReadYears(effectiveVehicleRoot);
        item.VehicleInfoYearMin = vehicleYears.min;
        item.VehicleInfoYearMax = vehicleYears.max;

        item.VehicleName = ReadString(root, "Name") ?? item.VehicleInfoName ?? ReadString(root, "Configuration") ?? item.ConfigKey;
        item.Brand = ReadStringOrAggregate(root, "Brand") ?? item.VehicleInfoBrand;
        item.Country = ReadStringOrAggregate(root, "Country") ?? item.VehicleInfoCountry;
        item.Type = ReadStringOrAggregate(root, "Type") ?? item.VehicleInfoType;
        item.BodyStyle = ReadStringOrAggregate(root, "Body Style") ?? item.VehicleInfoBodyStyle;
        item.ConfigType = ReadStringOrAggregate(root, "Config Type");
        item.Configuration = ReadString(root, "Configuration");
        item.InsuranceClass = ReadStringOrAggregate(root, "InsuranceClass") ?? DefaultInsuranceClass;

        var years = ReadYears(root);
        item.YearMin = years.min ?? vehicleYears.min;
        item.YearMax = years.max ?? vehicleYears.max;

        item.Value = ReadDouble(root, "Value");
        item.Population = ReadInt(root, "Population");
    }

    private static string BuildVehicleInfoPath(string configInfoPath, bool isZip)
    {
        if (isZip)
        {
            var normalized = configInfoPath.Replace('\\', '/');
            var slash = normalized.LastIndexOf('/');
            if (slash >= 0)
            {
                return normalized.Substring(0, slash + 1) + "info.json";
            }
            return "info.json";
        }

        var dir = Path.GetDirectoryName(configInfoPath) ?? string.Empty;
        return Path.Combine(dir, "info.json");
    }

    private static JsonNode? TryLoadVehicleInfoRoot(string sourcePath, string vehicleInfoPath, bool isZip, ZipScanContext? zipScanContext = null)
    {
        try
        {
            if (!isZip)
            {
                return File.Exists(vehicleInfoPath) ? ParseJson(File.ReadAllText(vehicleInfoPath)) : null;
            }

            return zipScanContext?.ReadJson(vehicleInfoPath);
        }
        catch
        {
            return null;
        }
    }

    private void UpdateMissingFromForm()
    {
        var missing = new List<string>();

        if (VehicleConfigItem.IsMissingText(BrandTextBox.Text)) missing.Add("Brand");
        if (VehicleConfigItem.IsMissingText(CountryTextBox.Text)) missing.Add("Country");
        if (VehicleConfigItem.IsMissingText(TypeTextBox.Text)) missing.Add("Type");
        if (VehicleConfigItem.IsMissingText(BodyStyleTextBox.Text)) missing.Add("Body Style");
        if (VehicleConfigItem.IsMissingText(ConfigTypeTextBox.Text)) missing.Add("Config Type");
        if (VehicleConfigItem.IsMissingText(ConfigurationTextBox.Text)) missing.Add("Configuration");
        if (VehicleConfigItem.IsMissingText(GetSelectedInsuranceClass())) missing.Add("Insurance Class");

        if (!ReadInteger(YearMinUpDown.Value).HasValue)
        {
            missing.Add("Years");
        }

        if (!ReadInteger(YearMaxUpDown.Value).HasValue)
        {
            if (!missing.Contains("Years")) missing.Add("Years");
        }

        if (!ValueUpDown.Value.HasValue || ValueUpDown.Value.Value <= 0)
        {
            missing.Add("Value");
        }

        var populationValue = ReadInteger(PopulationUpDown.Value);
        if (!populationValue.HasValue || populationValue.Value <= 0)
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
            _accentColorName = string.IsNullOrWhiteSpace(persisted?.AccentColorName) ? "Blue" : persisted!.AccentColorName!;
            _defaultStartupPage = string.IsNullOrWhiteSpace(persisted?.DefaultStartupPage) ? "Dashboard" : persisted!.DefaultStartupPage!;
            _reopenLastModsFolderOnStartup = persisted?.ReopenLastModsFolderOnStartup ?? true;
            _holdWeakMatchesForReview = persisted?.HoldWeakMatchesForReview ?? true;
            _openReviewQueueAfterAutoFill = persisted?.OpenReviewAfterAutoFill ?? false;
            _lookupTimeoutSeconds = Math.Clamp(persisted?.LookupTimeoutSeconds ?? 8, 3, 30);

            ApplyTheme(ThemeToggleSwitch.IsOn);
            LoadAutoFillSettingsIntoUi();
            SyncExtendedSettingsPageFromState();
            RealVehiclePricingService.ConfigureLookupTimeoutSeconds(_lookupTimeoutSeconds);
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
                AccentColorName = _accentColorName,
                DefaultStartupPage = _defaultStartupPage,
                ReopenLastModsFolderOnStartup = _reopenLastModsFolderOnStartup,
                HoldWeakMatchesForReview = _holdWeakMatchesForReview,
                OpenReviewAfterAutoFill = _openReviewQueueAfterAutoFill,
                LookupTimeoutSeconds = _lookupTimeoutSeconds,
                LastModsPath = ModsPathTextBox?.Text?.Trim() ?? string.Empty,
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
        LoadAutoFillSettingsIntoUi();
        AutoFillSettingsPopup.IsOpen = !AutoFillSettingsPopup.IsOpen;
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


    private void WorkspaceNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning || _isAutoFillAllRunning)
        {
            StatusTextBlock.Text = _isScanning ? "Wait for the scan to finish before switching pages." : "Wait for Auto Fill All to finish before switching pages.";
            return;
        }

        if (sender is System.Windows.Controls.Button button && button.Tag is string page)
        {
            SetWorkspacePage(page);
        }
    }

    private void OpenResultsPage_Click(object sender, RoutedEventArgs e) => SetWorkspacePage("Results");

    private void OpenReviewQueuePage_Click(object sender, RoutedEventArgs e) => SetWorkspacePage("ReviewQueue");

    private void OpenRenamerPage_Click(object sender, RoutedEventArgs e) => SetWorkspacePage("Renamer");

    private async void OpenRenamerWizardAgain_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var reviewOutcome = await RunRenamerWizardIfNeededAsync();
            RefreshWorkspaceSummary();
            RefreshWorkspacePageSummaries();
            RefreshSettingsPageSummary();

            if (reviewOutcome.ReviewedCount > 0 || reviewOutcome.IgnoredCount > 0 || reviewOutcome.RetriedCount > 0)
            {
                AppendScrapeLog($"MANUAL REVIEW outcome :: retried={reviewOutcome.RetriedCount} ignored={reviewOutcome.IgnoredCount} reviewed={reviewOutcome.ReviewedCount}");
            }
            else
            {
                System.Windows.MessageBox.Show("There are no unresolved mods waiting for the review wizard right now.", "Review Wizard", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            AppendScrapeLog($"Manual renamer launch failed: {ex}");
            System.Windows.MessageBox.Show(ex.Message, "Review Wizard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenAutoFillSettingsFromPage_Click(object sender, RoutedEventArgs e)
    {
        AutoFillSettingsPopup.IsOpen = false;
        AutoFillSettingsPopup.PlacementTarget = OpenAutoFillSettingsFromPageButton;
        AutoFillSettingsPopup.IsOpen = true;
    }

    private void SettingsThemeToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        ThemeToggleSwitch.IsOn = SettingsThemeToggleSwitch.IsOn;
    }

    private void SettingsBackupToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        BackupToggleSwitch.IsOn = SettingsBackupToggleSwitch.IsOn;
    }

    private void SettingsVehiclesToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        VehiclesToggleSwitch.IsOn = SettingsVehiclesToggleSwitch.IsOn;
    }

    private void SyncSettingsPageFromMain()
    {
        _suppressSettingsSave = true;
        SettingsThemeToggleSwitch.IsOn = ThemeToggleSwitch.IsOn;
        SettingsBackupToggleSwitch.IsOn = BackupToggleSwitch.IsOn;
        SettingsVehiclesToggleSwitch.IsOn = VehiclesToggleSwitch.IsOn;
        SyncExtendedSettingsPageFromState();
        _suppressSettingsSave = false;
    }

    private void SyncExtendedSettingsPageFromState()
    {
        SelectComboBoxItemByTag(SettingsAccentColorComboBox, _accentColorName);
        SelectComboBoxItemByTag(SettingsStartupPageComboBox, _defaultStartupPage);
        SettingsReopenLastFolderToggleSwitch.IsOn = _reopenLastModsFolderOnStartup;
        SettingsHoldWeakMatchesToggleSwitch.IsOn = _holdWeakMatchesForReview;
        SettingsOpenReviewAfterAutoFillToggleSwitch.IsOn = _openReviewQueueAfterAutoFill;
        SettingsLookupTimeoutUpDown.Value = _lookupTimeoutSeconds;
    }

    private static void SelectComboBoxItemByTag(System.Windows.Controls.ComboBox comboBox, string tag)
    {
        if (comboBox == null) return;
        foreach (var item in comboBox.Items)
        {
            if (item is System.Windows.Controls.ComboBoxItem comboBoxItem && string.Equals(comboBoxItem.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = comboBoxItem;
                return;
            }
        }

        if (comboBox.Items.Count > 0 && comboBox.SelectedIndex < 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }


    private void ResetSettingsDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this,
                "Reset the Settings page options back to their default values?",
                "Reset Settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _suppressSettingsSave = true;
        try
        {
            ThemeToggleSwitch.IsOn = false;
            BackupToggleSwitch.IsOn = false;
            VehiclesToggleSwitch.IsOn = false;

            _accentColorName = "Blue";
            _defaultStartupPage = "Dashboard";
            _reopenLastModsFolderOnStartup = true;
            _holdWeakMatchesForReview = true;
            _openReviewQueueAfterAutoFill = false;
            _lookupTimeoutSeconds = 8;

            ApplyTheme(false);
            SyncSettingsPageFromMain();
            SyncExtendedSettingsPageFromState();
            RealVehiclePricingService.ConfigureLookupTimeoutSeconds(_lookupTimeoutSeconds);
        }
        finally
        {
            _suppressSettingsSave = false;
        }

        SavePersistedSettings();
        StatusTextBlock.Text = "Settings page defaults restored.";
        StatusDetailTextBlock.Text = "Default application and workflow preferences were reapplied.";
        DashboardRecentActivityTextBlock.Text = StatusTextBlock.Text + Environment.NewLine + StatusDetailTextBlock.Text;
    }

    private void SettingsAccentColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        if (SettingsAccentColorComboBox.SelectedItem is ComboBoxItem item && !string.IsNullOrWhiteSpace(item.Tag?.ToString()))
        {
            _accentColorName = item.Tag!.ToString()!;
            ApplyTheme(ThemeToggleSwitch.IsOn);
            SavePersistedSettings();
        }
    }

    private void SettingsStartupPageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        if (SettingsStartupPageComboBox.SelectedItem is ComboBoxItem item && !string.IsNullOrWhiteSpace(item.Tag?.ToString()))
        {
            _defaultStartupPage = item.Tag!.ToString()!;
            SavePersistedSettings();
        }
    }

    private void SettingsReopenLastFolderToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        _reopenLastModsFolderOnStartup = SettingsReopenLastFolderToggleSwitch.IsOn;
        SavePersistedSettings();
    }

    private void SettingsHoldWeakMatchesToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        _holdWeakMatchesForReview = SettingsHoldWeakMatchesToggleSwitch.IsOn;
        SavePersistedSettings();
    }

    private void SettingsOpenReviewAfterAutoFillToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        _openReviewQueueAfterAutoFill = SettingsOpenReviewAfterAutoFillToggleSwitch.IsOn;
        SavePersistedSettings();
    }

    private void SettingsLookupTimeoutUpDown_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double?> e)
    {
        if (_suppressSettingsSave) return;
        var timeout = Math.Clamp((int)Math.Round(e.NewValue ?? 8d), 3, 30);
        _lookupTimeoutSeconds = timeout;
        RealVehiclePricingService.ConfigureLookupTimeoutSeconds(timeout);
        SavePersistedSettings();
    }

    private List<SourceReviewEntry> BuildRenamerEntriesSafe()
    {
        try
        {
            return BuildRenamerEntries();
        }
        catch (Exception ex)
        {
            AppendScrapeLog($"Renamer queue rebuild failed: {ex}");
            return new List<SourceReviewEntry>();
        }
    }


    private void RefreshFlaggedConfigsCollection()
    {
        FlaggedConfigs.Clear();
        foreach (var item in Configs.Where(x => x.NeedsReview)
                     .OrderByDescending(x => x.IsSuspicious)
                     .ThenByDescending(x => x.HasMissing)
                     .ThenBy(x => x.ModName)
                     .ThenBy(x => x.VehicleName))
        {
            FlaggedConfigs.Add(item);
        }
    }

    private void RefreshWorkspaceSummary()
    {
        try
        {
            var missing = Configs.Count(x => x.HasMissing);
            var suspicious = Configs.Count(x => x.IsSuspicious);
            var review = Configs.Count(x => x.NeedsReview);
            var uniqueMods = Configs
                .Select(x => !string.IsNullOrWhiteSpace(x.SourcePath) ? x.SourcePath : x.ModName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var uniqueBrands = Configs
                .Select(x => NormalizeSummaryLabel(x.Brand, "Unknown"))
                .Where(x => !string.Equals(x, "Unknown", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            DashboardMissingCountTextBlock.Text = missing.ToString();
            DashboardSuspiciousCountTextBlock.Text = suspicious.ToString();
            DashboardReviewCountTextBlock.Text = review.ToString();
            DashboardInstallSummaryTextBlock.Text = Configs.Count == 0
                ? "Scan a repo to show how many unique mods, brands, and flagged items were detected."
                : $"Loaded {Configs.Count} configs from {uniqueMods} unique mod(s) across {uniqueBrands} detected brand(s).";

            DashboardBrandStats.Clear();
            foreach (var stat in Configs
                         .GroupBy(x => NormalizeSummaryLabel(x.Brand, "Unknown"), StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(g => g.Count())
                         .ThenBy(g => g.Key)
                         .Take(10)
                         .Select(g => new SummaryCountItem
                         {
                             Label = g.Key,
                             Count = g.Count(),
                             Sources = g.GroupBy(x => !string.IsNullOrWhiteSpace(x.SourcePath) ? x.SourcePath : x.ModName, StringComparer.OrdinalIgnoreCase)
                                 .OrderByDescending(sg => sg.Count())
                                 .ThenBy(sg => Path.GetFileName(sg.Key))
                                 .Select(sg => new BrandSourceItem
                                 {
                                     DisplayName = Path.GetFileName(sg.Key),
                                     SourcePath = sg.Key,
                                     ConfigCount = sg.Count(),
                                     ReviewCount = sg.Count(i => i.NeedsReview)
                                 })
                                 .ToList()
                         }))
            {
                DashboardBrandStats.Add(stat);
            }

            DashboardIssueSummaryTextBlock.Text = BuildIssueSummaryText(missing, suspicious, review);

            var renamerEntries = BuildRenamerEntriesSafe();
            RenamerQueuedCountTextBlock.Text = renamerEntries.Count.ToString();
            DashboardModsCountTextBlock.Text = uniqueMods.ToString();

            var modLibraryItems = Configs
                .GroupBy(x => !string.IsNullOrWhiteSpace(x.SourcePath) ? x.SourcePath : x.ModName, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var displayName = Path.GetFileName(g.Key);
                    var hasVehicle = g.Any(item => !item.IsMapMod);
                    var hasMap = g.Any(item => item.IsMapMod);
                    var category = hasVehicle && hasMap
                        ? "Mixed"
                        : hasMap
                            ? "Map"
                            : "Vehicle";

                    return new ModLibraryItem
                    {
                        Label = string.IsNullOrWhiteSpace(displayName) ? g.Key : displayName,
                        Count = g.Count(),
                        ReviewCount = g.Count(item => item.NeedsReview),
                        Category = category
                    };
                })
                .OrderByDescending(item => item.ReviewCount)
                .ThenByDescending(item => item.Count)
                .ThenBy(item => item.Label)
                .ToList();

            ModLibraryItems.Clear();
            foreach (var item in modLibraryItems)
            {
                ModLibraryItems.Add(item);
            }

            RenamerDetectedModsCountTextBlock.Text = uniqueMods.ToString();
            RenamerVehicleModsCountTextBlock.Text = modLibraryItems.Count(item => item.Category is "Vehicle" or "Mixed").ToString();
            RenamerMapModsCountTextBlock.Text = modLibraryItems.Count(item => item.Category is "Map" or "Mixed").ToString();
            RenamerIgnoredCountTextBlock.Text = $"{Configs.Count(x => x.IgnoreFromRenamer)} mod(s) are currently ignored from review retry passes.";
            RefreshFlaggedConfigsCollection();
            ReviewQueueSummaryTextBlock.Text = review == 0
                ? "Nothing is currently flagged. Run a scan or Auto Fill All to populate this list when needed."
                : $"{review} config(s) currently need attention. Use this page to inspect individual missing or suspicious configs before making manual edits.";

            FooterRepoStateTextBlock.Text = Configs.Count == 0
                ? "Repo not loaded"
                : $"Repo loaded · {uniqueMods} mods · {review} review";
        }
        catch (Exception ex)
        {
            AppendScrapeLog($"Workspace summary refresh failed: {ex}");
        }
    }

    private void DashboardBrandStatsGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DashboardBrandStatsGrid.SelectedItem is not SummaryCountItem stat)
        {
            return;
        }

        var brand = NormalizeSummaryLabel(stat.Label, "Unknown");
        var sources = Configs
            .Where(x => string.Equals(NormalizeSummaryLabel(x.Brand, "Unknown"), brand, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => !string.IsNullOrWhiteSpace(x.SourcePath) ? x.SourcePath : x.ModName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => Path.GetFileName(g.Key))
            .Select(g => new BrandSourceBreakdownItem
            {
                DisplayName = Path.GetFileName(g.Key),
                SourcePath = g.Key,
                ConfigCount = g.Count(),
                ReviewCount = g.Count(i => i.NeedsReview),
                ConfigNames = g.Select(i =>
                        !string.IsNullOrWhiteSpace(i.VehicleName) ? i.VehicleName :
                        !string.IsNullOrWhiteSpace(i.Configuration) ? i.Configuration :
                        !string.IsNullOrWhiteSpace(i.ConfigKey) ? i.ConfigKey :
                        i.ModelKey)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .ToList();

        if (!sources.Any())
        {
            return;
        }

        var window = new BrandBreakdownWindow(brand, sources)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.ShowDialog();
        return;
    }

    private static string NormalizeSummaryLabel(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private string BuildIssueSummaryText(int missing, int suspicious, int review)
    {
        if (Configs.Count == 0)
        {
            return "No issue mods recorded yet. Scan a mods repo to populate review and error summaries.";
        }

        var reviewReasons = Configs
            .Select(x => !string.IsNullOrWhiteSpace(x.ReviewReason) ? x.ReviewReason!.Trim() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(x => x!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Take(3)
            .Select(g => $"{g.Key} ({g.Count()})")
            .ToList();

        var autoFillIssues = Configs
            .Select(x => !string.IsNullOrWhiteSpace(x.LastAutoFillStatus) ? x.LastAutoFillStatus!.Trim() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x) && x!.Contains("error", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Take(2)
            .Select(g => $"{g.Key} ({g.Count()})")
            .ToList();

        var combined = reviewReasons.Concat(autoFillIssues).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (combined.Count == 0)
        {
            return review == 0
                ? "No active issue mods. Scan health currently looks clean."
                : $"{review} review items remain, but no grouped issue reason text has been recorded yet.";
        }

        return $"Missing: {missing} · Suspicious: {suspicious} · Review: {review}\nTop issue mods: {string.Join("; ", combined)}";
    }

    private void RefreshWorkspacePageSummaries()
    {
        LogsSummaryTextBlock.Text = string.IsNullOrWhiteSpace(StatusTextBlock.Text)
            ? "Operational logs, scan messages, autofill progress, and recent status updates are collected here."
            : $"Latest status: {StatusTextBlock.Text}";

        var pageLabel = _currentWorkspacePage switch
        {
            "Renamer" => "Mod Library",
            "LicensePlates" => "License Plates",
            _ => _currentWorkspacePage
        };
        FooterWorkspacePageTextBlock.Text = $"Page: {pageLabel}";
    }

    private void RefreshDataPageSummary()
    {
        try
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
            var makesPath = Path.Combine(dataDir, "makes.json");
            var profilesPath = Path.Combine(dataDir, "vehicle-profiles.json");
            var trimPath = Path.Combine(dataDir, "trim-keywords.json");
            var pricingPath = Path.Combine(dataDir, "pricing-rules.json");

            DataMakesCountTextBlock.Text = CountJsonArrayEntries(makesPath).ToString();
            DataProfilesCountTextBlock.Text = CountJsonArrayEntries(profilesPath).ToString();
            DataTrimKeywordsCountTextBlock.Text = CountJsonArrayEntries(trimPath).ToString();
            DataPricingRulesCountTextBlock.Text = CountPricingRuleEntries(pricingPath).ToString();
            DataFolderPathTextBlock.Text = dataDir;
            var files = new[] { makesPath, profilesPath, trimPath, pricingPath };
            var present = files.Count(File.Exists);
            DataFilesStatusTextBlock.Text = $"{present}/{files.Length} required dataset files detected in the active Data folder.";
        }
        catch
        {
            DataMakesCountTextBlock.Text = "0";
            DataProfilesCountTextBlock.Text = "0";
            DataTrimKeywordsCountTextBlock.Text = "0";
            DataPricingRulesCountTextBlock.Text = "0";
            DataFolderPathTextBlock.Text = "Data folder unavailable.";
            DataFilesStatusTextBlock.Text = "Dataset files could not be read.";
        }

        RefreshSettingsPageSummary();
    }

    private void RefreshSettingsPageSummary()
    {
        SettingsCurrentFolderTextBlock.Text = string.IsNullOrWhiteSpace(ModsPathTextBox.Text)
            ? "Current mods folder: none selected."
            : $"Current mods folder: {ModsPathTextBox.Text}";

        var review = Configs.Count(x => x.NeedsReview);
        SettingsReviewSummaryTextBlock.Text = review == 0
            ? "Review status: clean. The current repo does not have flagged configs right now."
            : $"Review status: {review} config(s) still need attention across missing or suspicious matches.";

        SettingsDataSummaryTextBlock.Text = $"Data summary: {DataMakesCountTextBlock.Text} makes, {DataProfilesCountTextBlock.Text} profiles, {DataTrimKeywordsCountTextBlock.Text} trim keyword groups, {DataPricingRulesCountTextBlock.Text} pricing rules loaded.";
    }

    private void ReloadDataPageButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshDataPageSummary();
        StatusTextBlock.Text = "Data page refreshed.";
        StatusDetailTextBlock.Text = "Dataset counts were re-read from the active Data folder.";
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        if (Directory.Exists(dataDir))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dataDir,
                UseShellExecute = true
            });
        }
        else
        {
            System.Windows.MessageBox.Show("The Data folder could not be found.", "Open Data Folder", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static int CountJsonArrayEntries(string path)
    {
        if (!File.Exists(path)) return 0;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
    }

    private static int CountPricingRuleEntries(string path)
    {
        if (!File.Exists(path)) return 0;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return 0;

        var total = 0;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                total += prop.Value.GetArrayLength();
            }
            else if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                total += prop.Value.EnumerateObject().Count();
            }
        }

        return total;
    }

    private void SetWorkspaceNavigationEnabled(bool isEnabled)
    {
        DashboardNavButton.IsEnabled = isEnabled;
        ResultsNavButton.IsEnabled = isEnabled;
        ReviewQueueNavButton.IsEnabled = isEnabled;
        RenamerNavButton.IsEnabled = isEnabled;
        LicensePlatesNavButton.IsEnabled = isEnabled;
        LogsNavButton.IsEnabled = isEnabled;
        DataNavButton.IsEnabled = isEnabled;
        SettingsNavButton.IsEnabled = isEnabled;
    }

    private void BuildLicensePlatesPageSelectionSources()
    {
        if (LicensePageModPickerComboBox == null || LicensePageConfigPickerListBox == null)
        {
            return;
        }

        var allConfigs = Configs.Where(x => x != null).OrderBy(x => x.ModName).ThenBy(x => x.ConfigKey).ToList();

        _licensePageModChoices.Clear();
        _licensePageModChoices.AddRange(allConfigs
            .GroupBy(x => x.SourcePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PlateModChoiceItem
            {
                SourcePath = g.Key,
                ModName = g.FirstOrDefault()?.ModName ?? "Unknown mod",
                ConfigCount = g.Count(),
                DisplayLabel = $"{(g.FirstOrDefault()?.ModName ?? "Unknown mod")} ({g.Count()} config{(g.Count() == 1 ? string.Empty : "s")})"
            })
            .OrderBy(x => x.ModName, StringComparer.OrdinalIgnoreCase));

        _licensePageConfigChoices.Clear();
        _licensePageConfigChoices.AddRange(allConfigs.Select(item => new PlateConfigChoiceItem
        {
            Item = item,
            DisplayLabel = $"{item.ModName} / {item.ConfigKey}"
        }));

        var previouslySelectedMod = _licensePageSelectedModSourcePath;
        var previouslySelectedConfigs = _licensePageSelectedConfigKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        LicensePageModPickerComboBox.ItemsSource = null;
        LicensePageModPickerComboBox.ItemsSource = _licensePageModChoices;
        LicensePageConfigPickerListBox.ItemsSource = null;
        LicensePageConfigPickerListBox.ItemsSource = _licensePageConfigChoices;

        if (!string.IsNullOrWhiteSpace(previouslySelectedMod))
        {
            LicensePageModPickerComboBox.SelectedItem = _licensePageModChoices.FirstOrDefault(x => string.Equals(x.SourcePath, previouslySelectedMod, StringComparison.OrdinalIgnoreCase));
        }

        LicensePageConfigPickerListBox.SelectedItems.Clear();
        foreach (var choice in _licensePageConfigChoices.Where(x => previouslySelectedConfigs.Contains(GetLicenseConfigSelectionKey(x.Item))))
        {
            LicensePageConfigPickerListBox.SelectedItems.Add(choice);
        }
    }

    private string LicensePageSelectedScope => LicensePageConfigScopeRadioButton?.IsChecked == true ? "Config" : LicensePageModScopeRadioButton?.IsChecked == true ? "Mod" : LicensePageWorkspaceScopeRadioButton?.IsChecked == true ? "Workspace" : string.Empty;
    private bool LicensePageClearMode => LicensePageClearPlateRadioButton?.IsChecked == true;
    private string LicensePagePlateText => LicensePagePlateTextBox?.Text?.Trim() ?? string.Empty;
    private PlateModChoiceItem? GetLicensePageSelectedModChoice() => LicensePageModPickerComboBox?.SelectedItem as PlateModChoiceItem;
    private static string GetLicenseConfigSelectionKey(VehicleConfigItem item) => $"{item.SourcePath}|{item.ConfigKey}";

    private IEnumerable<VehicleConfigItem> ResolveLicensePageTargetItems()
    {
        if (LicensePageConfigScopeRadioButton?.IsChecked == true)
        {
            return LicensePageConfigPickerListBox.SelectedItems.Cast<PlateConfigChoiceItem>().Select(x => x.Item).Distinct();
        }
        if (LicensePageModScopeRadioButton?.IsChecked == true)
        {
            var selectedMod = GetLicensePageSelectedModChoice();
            if (selectedMod == null) return Enumerable.Empty<VehicleConfigItem>();
            return Configs.Where(x => string.Equals(x.SourcePath, selectedMod.SourcePath, StringComparison.OrdinalIgnoreCase));
        }
        if (LicensePageWorkspaceScopeRadioButton?.IsChecked == true)
        {
            return Configs;
        }
        return Enumerable.Empty<VehicleConfigItem>();
    }

    private IReadOnlyList<VehicleConfigItem> ResolveLicensePageActionableItems()
    {
        var items = new List<VehicleConfigItem>();
        var plateText = LicensePagePlateText;
        foreach (var item in ResolveLicensePageTargetItems())
        {
            if (!item.HasConfigPc) continue;
            if (LicensePageClearMode)
            {
                if (item.HasHardcodedPlate) items.Add(item);
                continue;
            }
            if (string.IsNullOrWhiteSpace(plateText)) continue;
            if (!string.Equals(item.CurrentLicensePlate ?? string.Empty, plateText, StringComparison.Ordinal)) items.Add(item);
        }
        return items;
    }

    private void RefreshLicensePageScopeSelectors()
    {
        var selectedScope = LicensePageSelectedScope;
        var isMod = selectedScope == "Mod";
        var isConfig = selectedScope == "Config";
        var hasScope = !string.IsNullOrWhiteSpace(selectedScope);
        var modReady = isMod && GetLicensePageSelectedModChoice() != null;
        var configReady = isConfig && LicensePageConfigPickerListBox != null && LicensePageConfigPickerListBox.SelectedItems.Count > 0;
        var workspaceReady = selectedScope == "Workspace";
        var selectionReady = modReady || configReady || workspaceReady;

        if (LicensePageModSelectionPanel != null)
        {
            LicensePageModSelectionPanel.Visibility = isMod ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(LicensePageModSelectionPanel, 0);
            Grid.SetColumnSpan(LicensePageModSelectionPanel, 1);
        }

        if (LicensePageConfigSelectionPanel != null)
        {
            LicensePageConfigSelectionPanel.Visibility = isConfig ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(LicensePageConfigSelectionPanel, isConfig ? 0 : 2);
            Grid.SetColumnSpan(LicensePageConfigSelectionPanel, isConfig ? 3 : 1);
        }

        if (LicensePageTargetSelectionBorder != null) LicensePageTargetSelectionBorder.Visibility = (isMod || isConfig) ? Visibility.Visible : Visibility.Collapsed;
        if (LicensePageActionModeBorder != null) LicensePageActionModeBorder.Visibility = hasScope ? Visibility.Visible : Visibility.Collapsed;
        if (LicensePageSummaryBorder != null) LicensePageSummaryBorder.Visibility = selectionReady ? Visibility.Visible : Visibility.Collapsed;
        if (LicensePageFooterBorder != null) LicensePageFooterBorder.Visibility = selectionReady ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshLicensePlatesPageUi()
    {
        if (_isRefreshingLicensePageUi || LicensePagePreviewSummaryTextBlock == null || LicensePageApplyButton == null || LicensePageFooterStatusTextBlock == null)
        {
            return;
        }

        _isRefreshingLicensePageUi = true;
        try
        {
            if (LicensePageModPickerComboBox != null && LicensePageModPickerComboBox.SelectedItem is PlateModChoiceItem modChoice)
            {
                _licensePageSelectedModSourcePath = modChoice.SourcePath;
            }
            else if (LicensePageSelectedScope != "Mod")
            {
                _licensePageSelectedModSourcePath = null;
            }

            if (LicensePageConfigPickerListBox != null)
            {
                _licensePageSelectedConfigKeys.Clear();
                foreach (var selected in LicensePageConfigPickerListBox.SelectedItems.OfType<PlateConfigChoiceItem>())
                {
                    _licensePageSelectedConfigKeys.Add(GetLicenseConfigSelectionKey(selected.Item));
                }
            }

            BuildLicensePlatesPageSelectionSources();
            RefreshLicensePageScopeSelectors();
            _licensePagePreviewItems.Clear();

            var selectedScope = LicensePageSelectedScope;
            var selectionProblem = selectedScope switch
            {
                "" => "Choose Mod, Config, or Everything to begin.",
                "Mod" when GetLicensePageSelectedModChoice() == null => "Choose a mod before continuing.",
                "Config" when LicensePageConfigPickerListBox == null || LicensePageConfigPickerListBox.SelectedItems.Count == 0 => "Choose at least one config before continuing.",
                _ => string.Empty
            };

            if (!string.IsNullOrWhiteSpace(selectionProblem))
            {
                LicensePagePreviewSummaryTextBlock.Text = selectionProblem;
                LicensePageFooterStatusTextBlock.Text = "Pick what you want to update first, then the summary will show exactly what will be changed.";
                LicensePageApplyButton.IsEnabled = false;
                if (LicensePagePlateTextBox != null) LicensePagePlateTextBox.IsEnabled = !LicensePageClearMode && !string.IsNullOrWhiteSpace(selectedScope);
                RefreshLicensePageScopeSelectors();
                return;
            }

            var plateText = LicensePagePlateText;
            var targetItems = ResolveLicensePageTargetItems().Where(x => x != null).OrderBy(x => x.ModName).ThenBy(x => x.ConfigKey).ToList();
            var actionableCount = 0;
            var missingPcCount = 0;
            var alreadyOkayCount = 0;

            foreach (var item in targetItems)
            {
                var currentPlate = string.IsNullOrWhiteSpace(item.CurrentLicensePlate) ? "(blank / dynamic)" : item.CurrentLicensePlate!;
                var resultPlate = LicensePageClearMode ? "(blank / dynamic)" : (string.IsNullOrWhiteSpace(plateText) ? "(enter plate text)" : plateText);
                string status;
                if (!item.HasConfigPc)
                {
                    status = "No .pc file found";
                    missingPcCount++;
                }
                else if (LicensePageClearMode)
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
                _licensePagePreviewItems.Add(new PlatePreviewItem { ModName = item.ModName, ConfigKey = item.ConfigKey, CurrentPlateDisplay = currentPlate, ResultPlateDisplay = resultPlate, Status = status });
            }

            var scopeLabel = selectedScope switch
            {
                "Config" => $"{targetItems.Count} selected config(s)",
                "Mod" => GetLicensePageSelectedModChoice()?.DisplayLabel ?? "the selected mod",
                _ => "everything in the workspace"
            };

            LicensePagePreviewSummaryTextBlock.Text = $"Ready to update {scopeLabel}. {actionableCount} change(s) are queued. {alreadyOkayCount} already match. {missingPcCount} do not have a .pc file available.";
            LicensePageFooterStatusTextBlock.Text = LicensePageClearMode
                ? "Remove mode deletes the hardcoded licenseName entry from each selected .pc file."
                : "Set mode writes the same licenseName text to each selected .pc file.";

            LicensePageApplyButton.IsEnabled = actionableCount > 0 && (LicensePageClearMode || !string.IsNullOrWhiteSpace(plateText));
            if (LicensePagePlateTextBox != null) LicensePagePlateTextBox.IsEnabled = !LicensePageClearMode;
            RefreshLicensePageScopeSelectors();
        }
        finally
        {
            _isRefreshingLicensePageUi = false;
        }
    }

    private void LicensePageScopeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender == LicensePageModScopeRadioButton)
        {
            _licensePageSelectedConfigKeys.Clear();
        }
        else if (sender == LicensePageConfigScopeRadioButton)
        {
            _licensePageSelectedModSourcePath = null;
        }
        else
        {
            _licensePageSelectedModSourcePath = null;
            _licensePageSelectedConfigKeys.Clear();
        }
        RefreshLicensePlatesPageUi();
    }
    private void LicensePageModeRadioButton_Checked(object sender, RoutedEventArgs e) => RefreshLicensePlatesPageUi();
    private void LicensePagePlateTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        EnforcePlateTextLimit(LicensePagePlateTextBox);
        RefreshLicensePlatesPageUi();
    }
    private void LicensePageModPickerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LicensePageModPickerComboBox?.SelectedItem is PlateModChoiceItem modChoice)
        {
            _licensePageSelectedModSourcePath = modChoice.SourcePath;
        }
        RefreshLicensePlatesPageUi();
    }
    private void LicensePageConfigPickerListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _licensePageSelectedConfigKeys.Clear();
        if (LicensePageConfigPickerListBox != null)
        {
            foreach (var selected in LicensePageConfigPickerListBox.SelectedItems.OfType<PlateConfigChoiceItem>())
            {
                _licensePageSelectedConfigKeys.Add(GetLicenseConfigSelectionKey(selected.Item));
            }
        }
        RefreshLicensePlatesPageUi();
    }

    private async void LicensePageApply_Click(object sender, RoutedEventArgs e)
    {
        var items = ResolveLicensePageActionableItems();
        var scopeLabel = LicensePageSelectedScope switch
        {
            "Config" => "Selected config(s)",
            "Mod" => GetLicensePageSelectedModChoice()?.DisplayLabel ?? "Selected mod",
            _ => "Everything"
        };
        await ApplyLicensePlateChangesAsync(items, LicensePageClearMode, LicensePagePlateText, scopeLabel);
        RefreshLicensePlatesPageUi();
    }

    private void RefreshLicensePlatesPageSummary()
    {
        if (LicensePagePreviewDataGrid != null && LicensePagePreviewDataGrid.ItemsSource == null)
        {
            LicensePagePreviewDataGrid.ItemsSource = _licensePagePreviewItems;
        }

        if (LicensePagePreviewSummaryTextBlock == null || LicensePageFooterStatusTextBlock == null)
        {
            return;
        }

        if (Configs.Count == 0)
        {
            LicensePagePreviewSummaryTextBlock.Text = "No repo loaded yet. Browse to a mods folder and run Scan before using the License Plates page.";
            LicensePageFooterStatusTextBlock.Text = "Set mode writes the same licenseName text to each selected .pc file.";
            return;
        }

        var uniqueMods = Configs
            .Select(x => x.SourcePath ?? x.ModName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var selectionLine = _selected == null
            ? "No config is currently selected in Configuration Editor. Config Plate stays config-specific after you select one."
            : $"Current Configuration Editor selection: {_selected.ModName} / {_selected.ConfigKey}.";

        LicensePagePreviewSummaryTextBlock.Text = $"Loaded {Configs.Count} config(s) across {uniqueMods} mod(s). {selectionLine}";
        LicensePageFooterStatusTextBlock.Text = LicensePageClearMode
            ? "Remove mode deletes the hardcoded licenseName entry from each selected .pc file."
            : "Set mode writes the same licenseName text to each selected .pc file.";
    }

    private void RefreshLogsPage(bool forceFullReload = false)
    {
        try
        {
            var statusSnapshot = string.Join(Environment.NewLine, new[]
            {
                StatusTextBlock.Text,
                StatusDetailTextBlock.Text
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            if (!string.IsNullOrWhiteSpace(statusSnapshot))
            {
                DashboardRecentActivityTextBlock.Text = statusSnapshot;
            }

            if (!forceFullReload)
            {
                if (LogsPageTextBox != null && string.IsNullOrWhiteSpace(LogsPageTextBox.Text))
                {
                    LogsPageTextBox.Text = statusSnapshot;
                }
                return;
            }

            if (File.Exists(ScrapeLogPath))
            {
                LogsPageTextBox.Text = File.ReadAllText(ScrapeLogPath);
                LogsPageTextBox.ScrollToEnd();
                return;
            }

            LogsPageTextBox.Text = statusSnapshot;
        }
        catch (Exception ex)
        {
            AppendScrapeLog($"Log refresh failed: {ex}");
        }
    }

    private void SetWorkspacePage(string page)
    {
        try
        {
            _currentWorkspacePage = page;

            DashboardPage.Visibility = Visibility.Collapsed;
            ResultsWorkspaceBorder.Visibility = Visibility.Collapsed;
            InspectorBorder.Visibility = Visibility.Collapsed;
            InspectorSplitter.Visibility = Visibility.Collapsed;
            ReviewQueuePage.Visibility = Visibility.Collapsed;
            RenamerPage.Visibility = Visibility.Collapsed;
            LicensePlatesPage.Visibility = Visibility.Collapsed;
            LogsPage.Visibility = Visibility.Collapsed;
            DataPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;

            switch (page)
            {
                case "Dashboard":
                    DashboardPage.Visibility = Visibility.Visible;
                    break;
                case "Results":
                    ResultsWorkspaceBorder.Visibility = Visibility.Visible;
                    InspectorBorder.Visibility = Visibility.Visible;
                    InspectorSplitter.Visibility = Visibility.Visible;
                    break;
                case "ReviewQueue":
                    ReviewQueuePage.Visibility = Visibility.Visible;
                    break;
                case "Renamer":
                    RenamerPage.Visibility = Visibility.Visible;
                    break;
                case "LicensePlates":
                    LicensePlatesPage.Visibility = Visibility.Visible;
                    break;
                case "Logs":
                    LogsPage.Visibility = Visibility.Visible;
                    break;
                case "Data":
                    DataPage.Visibility = Visibility.Visible;
                    break;
                case "Settings":
                    SettingsPage.Visibility = Visibility.Visible;
                    break;
                default:
                    ResultsWorkspaceBorder.Visibility = Visibility.Visible;
                    InspectorBorder.Visibility = Visibility.Visible;
                    InspectorSplitter.Visibility = Visibility.Visible;
                    _currentWorkspacePage = "Results";
                    break;
            }

            ApplyWorkspaceNavStyle(DashboardNavButton, _currentWorkspacePage == "Dashboard");
            ApplyWorkspaceNavStyle(ResultsNavButton, _currentWorkspacePage == "Results");
            ApplyWorkspaceNavStyle(ReviewQueueNavButton, _currentWorkspacePage == "ReviewQueue");
            ApplyWorkspaceNavStyle(RenamerNavButton, _currentWorkspacePage == "Renamer");
            ApplyWorkspaceNavStyle(LicensePlatesNavButton, _currentWorkspacePage == "LicensePlates");
            ApplyWorkspaceNavStyle(LogsNavButton, _currentWorkspacePage == "Logs");
            ApplyWorkspaceNavStyle(DataNavButton, _currentWorkspacePage == "Data");
            ApplyWorkspaceNavStyle(SettingsNavButton, _currentWorkspacePage == "Settings");

            RefreshWorkspaceSummary();
            RefreshWorkspacePageSummaries();
            RefreshLicensePlatesPageSummary();
            RefreshLogsPage();
            SyncSettingsPageFromMain();
            RefreshSettingsPageSummary();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "A page transition failed.";
            StatusDetailTextBlock.Text = ex.Message;
        }
    }

    private void LeftNavToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isLeftNavCollapsed = !_isLeftNavCollapsed;
        ApplyLeftNavState();
    }

    private void ApplyLeftNavState()
    {
        if (LeftNavColumn is null || LeftNavSpacerColumn is null || NavigationRailBorder is null || LeftNavToggleButton is null)
        {
            return;
        }

        LeftNavColumn.Width = _isLeftNavCollapsed ? new GridLength(0) : _expandedLeftNavWidth;
        LeftNavSpacerColumn.Width = _isLeftNavCollapsed ? new GridLength(0) : _expandedLeftNavSpacerWidth;
        NavigationRailBorder.Visibility = _isLeftNavCollapsed ? Visibility.Collapsed : Visibility.Visible;
        LeftNavToggleButton.ToolTip = _isLeftNavCollapsed ? "Open navigation" : "Collapse navigation";
        UpdateOverlayPageMargins();
    }

    private void UpdateOverlayPageMargins()
    {
        var left = _isLeftNavCollapsed ? 14d : 182d;
        var pageMargin = new Thickness(left, 14, 14, 12);

        if (DashboardPage is not null) DashboardPage.Margin = pageMargin;
        if (ReviewQueuePage is not null) ReviewQueuePage.Margin = pageMargin;
        if (RenamerPage is not null) RenamerPage.Margin = pageMargin;
        if (LicensePlatesPage is not null) LicensePlatesPage.Margin = pageMargin;
        if (LogsPage is not null) LogsPage.Margin = pageMargin;
        if (DataPage is not null) DataPage.Margin = pageMargin;
        if (SettingsPage is not null) SettingsPage.Margin = pageMargin;
    }

    private void ApplyWorkspaceNavStyle(System.Windows.Controls.Button button, bool isActive)
    {
        var key = isActive ? "NavigationButtonActiveStyle" : "NavigationButtonStyle";
        if (FindResource(key) is Style style)
        {
            button.Style = style;
        }
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
        if (_suppressSettingsSave) return;
        ApplyTheme(ThemeToggleSwitch.IsOn);
        SyncSettingsPageFromMain();
        SavePersistedSettings();
    }

    private void BackupToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        SyncSettingsPageFromMain();
        SavePersistedSettings();
    }

    private void VehiclesToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        SyncSettingsPageFromMain();
        SavePersistedSettings();
    }

    private void ApplyTheme(bool isDark)
    {
        ThemeManager.Current.ChangeTheme(this, isDark ? $"Dark.{_accentColorName}" : $"Light.{_accentColorName}");
        ReplaceThemeDictionary(isDark ? DarkThemeDictionaryUri : LightThemeDictionaryUri);
        ApplyAccentPalette(_accentColorName, isDark);
    }

    private void ApplyAccentPalette(string accentName, bool isDark)
    {
        var accentColor = accentName?.ToLowerInvariant() switch
        {
            "green" => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isDark ? "#62DB8A" : "#1D7A3C"),
            "purple" => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isDark ? "#B490FF" : "#7C4DFF"),
            "orange" => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isDark ? "#FFB25B" : "#C96A00"),
            "red" => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isDark ? "#FF7676" : "#B42318"),
            _ => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isDark ? "#6AB8FF" : "#1F7DD6")
        };

        var accentForeground = isDark ? Colors.White : Colors.White;
        var titleBarStart = isDark ? ParseColor("#14233A") : ParseColor("#D3DEEF");
        var titleBarEnd = isDark ? ParseColor("#0F1B2E") : ParseColor("#F0F6FF");
        var cardGlow = Blend(accentColor, isDark ? ParseColor("#18385C") : ParseColor("#D6E2F3"), isDark ? 0.48 : 0.20);
        var shellOutline = Blend(accentColor, isDark ? ParseColor("#33547A") : ParseColor("#97AEC9"), isDark ? 0.38 : 0.18);
        var accentPanelStart = Blend(accentColor, isDark ? ParseColor("#132238") : ParseColor("#F0F6FF"), isDark ? 0.34 : 0.14);
        var accentPanelEnd = Blend(accentColor, isDark ? ParseColor("#0F1B2E") : ParseColor("#FCFEFF"), isDark ? 0.22 : 0.08);
        var buttonBackground = Blend(accentColor, isDark ? ParseColor("#1A2940") : ParseColor("#DFE8F6"), isDark ? 0.18 : 0.08);
        var hoverBackground = Blend(accentColor, isDark ? ParseColor("#1B2E47") : ParseColor("#E0EBFA"), isDark ? 0.26 : 0.14);
        var statusBadge = Blend(accentColor, isDark ? ParseColor("#1E2F49") : ParseColor("#D4E2F6"), isDark ? 0.24 : 0.14);
        var gridHeader = Blend(accentColor, isDark ? ParseColor("#16253C") : ParseColor("#CCDAEE"), isDark ? 0.22 : 0.12);

        Resources["AccentBrush"] = CreateBrush(accentColor);
        Resources["AccentForegroundBrush"] = CreateBrush(accentForeground);
        Resources["TitleBarBackgroundBrush"] = CreateBrush(titleBarStart);
        Resources["ButtonBackgroundBrush"] = CreateBrush(buttonBackground);
        Resources["HoverBrush"] = CreateBrush(hoverBackground);
        Resources["StatusBadgeBackgroundBrush"] = CreateBrush(statusBadge);
        Resources["GridHeaderBackgroundBrush"] = CreateBrush(gridHeader);
        Resources["CardGlowBrush"] = CreateBrush(cardGlow);
        Resources["ShellOutlineBrush"] = CreateBrush(shellOutline);
        Resources["TitleBarGradientBrush"] = new LinearGradientBrush(titleBarStart, titleBarEnd, 0);
        Resources["AccentCardGradientBrush"] = new LinearGradientBrush(accentPanelStart, accentPanelEnd, 45);
    }

    private static SolidColorBrush CreateBrush(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Color ParseColor(string hex)
        => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    private static System.Windows.Media.Color Blend(System.Windows.Media.Color source, System.Windows.Media.Color target, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte blend(byte from, byte to) => (byte)Math.Clamp((int)Math.Round(from + ((to - from) * amount)), 0, 255);
        return System.Windows.Media.Color.FromArgb(
            blend(source.A, target.A),
            blend(source.R, target.R),
            blend(source.G, target.G),
            blend(source.B, target.B));
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
        if (!value.HasValue || value.Value <= 0)
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

    private void UpdateWindowChromeState()
    {
        RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(16);
        if (MaxRestoreGlyph is not null)
        {
            MaxRestoreGlyph.Text = WindowState == WindowState.Maximized ? "❐" : "□";
        }

        if (MaxRestoreButton is not null)
        {
            MaxRestoreButton.ToolTip = WindowState == WindowState.Maximized ? "Restore down" : "Maximize window";
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
        public string AccentColorName { get; set; } = "Blue";
        public string DefaultStartupPage { get; set; } = "Dashboard";
        public bool ReopenLastModsFolderOnStartup { get; set; } = true;
        public bool HoldWeakMatchesForReview { get; set; } = true;
        public bool OpenReviewAfterAutoFill { get; set; }
        public int LookupTimeoutSeconds { get; set; } = 8;
        public string LastModsPath { get; set; } = string.Empty;
        public AutoFillSettings AutoFill { get; set; } = AutoFillSettings.CreateDefaults();
    }

    private sealed class AutoFillSettings
    {
        public string Brand { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string Type { get; set; } = "Car";
        public string BodyStyle { get; set; } = string.Empty;
        public string ConfigType { get; set; } = "Factory";
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

        if (SaveChangesButton != null)
        {
            SaveChangesButton.IsEnabled = _hasUnsavedChanges && _selected != null;
        }
    }

    private VehiclesMirrorResult WriteConfig(VehicleConfigItem item, string json)
    {
        WriteConfigBatch(new[]
        {
            new PendingConfigWrite
            {
                Item = item,
                Json = json
            }
        }, mirrorToVehicles: false);

        if (VehiclesToggleSwitch?.IsOn != true)
        {
            return VehiclesMirrorResult.NotRequested();
        }

        return MirrorConfigBundleToVehicles(item, json, ModsPathTextBox.Text.Trim());
    }

    private void WriteConfigBatch(IReadOnlyCollection<PendingConfigWrite> writes, bool mirrorToVehicles)
    {
        if (writes == null || writes.Count == 0)
        {
            return;
        }

        foreach (var sourceGroup in writes.GroupBy(x => x.Item.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var sourceWrites = sourceGroup.ToList();
            var firstItem = sourceWrites[0].Item;
            EnsureBackupExists(firstItem);

            if (firstItem.IsZip)
            {
                var entryJsonMap = sourceWrites.ToDictionary(
                    x => NormalizeZipEntryPath(x.Item.InfoPath),
                    x => x.Json,
                    StringComparer.OrdinalIgnoreCase);
                var replacementNames = sourceWrites.ToDictionary(
                    x => NormalizeZipEntryPath(x.Item.InfoPath),
                    x => x.Item.InfoPath,
                    StringComparer.OrdinalIgnoreCase);
                WriteZipEntries(firstItem.SourcePath, entryJsonMap, replacementNames);
            }
            else
            {
                foreach (var write in sourceWrites)
                {
                    File.WriteAllText(write.Item.InfoPath, write.Json, new UTF8Encoding(false));
                }
            }

            if (mirrorToVehicles && VehiclesToggleSwitch?.IsOn == true)
            {
                var modsPath = ModsPathTextBox.Text.Trim();
                foreach (var write in sourceWrites)
                {
                    MirrorConfigBundleToVehicles(write.Item, write.Json, modsPath);
                }
            }
        }
    }

    private void EnsureBackupExists(VehicleConfigItem item)
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

    private static ZipArchiveEntry? FindZipEntry(ZipArchive archive, string entryPath)
    {
        if (archive == null || string.IsNullOrWhiteSpace(entryPath))
        {
            return null;
        }

        var normalizedTarget = NormalizeZipEntryPath(entryPath);
        return archive.Entries.FirstOrDefault(entry =>
            string.Equals(NormalizeZipEntryPath(entry.FullName), normalizedTarget, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeZipEntryPath(string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('\\', '/').TrimStart('/');
    }

    private static void WriteZipEntry(string zipPath, string entryPath, string json)
    {
        WriteZipEntries(
            zipPath,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [NormalizeZipEntryPath(entryPath)] = json
            },
            null);
    }

    private static void WriteZipEntries(string zipPath, IReadOnlyDictionary<string, string> entryJsonMap, IReadOnlyDictionary<string, string>? replacementNames)
    {
        if (string.IsNullOrWhiteSpace(zipPath))
        {
            throw new ArgumentException("Zip path is required.", nameof(zipPath));
        }

        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("Zip file not found.", zipPath);
        }

        if (entryJsonMap == null || entryJsonMap.Count == 0)
        {
            return;
        }

        var normalizedMap = entryJsonMap.ToDictionary(kvp => NormalizeZipEntryPath(kvp.Key), kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
        var zipDirectory = Path.GetDirectoryName(zipPath);
        if (string.IsNullOrWhiteSpace(zipDirectory))
        {
            zipDirectory = Environment.CurrentDirectory;
        }

        var tempZipPath = Path.Combine(zipDirectory, Path.GetFileName(zipPath) + ".writing");
        if (File.Exists(tempZipPath))
        {
            File.Delete(tempZipPath);
        }

        try
        {
            using (var sourceArchive = ZipFile.OpenRead(zipPath))
            using (var tempArchive = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
            {
                var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var existingEntry in sourceArchive.Entries)
                {
                    var normalizedExisting = NormalizeZipEntryPath(existingEntry.FullName);
                    if (normalizedMap.TryGetValue(normalizedExisting, out var replacementJson))
                    {
                        var replacementEntryName = replacementNames != null && replacementNames.TryGetValue(normalizedExisting, out var knownName)
                            ? knownName
                            : existingEntry.FullName;
                        var replacementEntry = tempArchive.CreateEntry(replacementEntryName, CompressionLevel.Optimal);
                        replacementEntry.LastWriteTime = DateTimeOffset.Now;
                        using var replacementWriter = new StreamWriter(replacementEntry.Open(), new UTF8Encoding(false));
                        replacementWriter.Write(replacementJson);
                        written.Add(normalizedExisting);
                        continue;
                    }

                    var copiedEntry = tempArchive.CreateEntry(existingEntry.FullName, CompressionLevel.Optimal);
                    copiedEntry.LastWriteTime = existingEntry.LastWriteTime;
                    using var sourceStream = existingEntry.Open();
                    using var targetStream = copiedEntry.Open();
                    sourceStream.CopyTo(targetStream);
                }

                foreach (var pending in normalizedMap.Where(kvp => !written.Contains(kvp.Key)))
                {
                    var pendingName = replacementNames != null && replacementNames.TryGetValue(pending.Key, out var knownName)
                        ? knownName
                        : pending.Key;
                    var newEntry = tempArchive.CreateEntry(pendingName, CompressionLevel.Optimal);
                    newEntry.LastWriteTime = DateTimeOffset.Now;
                    using var writer = new StreamWriter(newEntry.Open(), new UTF8Encoding(false));
                    writer.Write(pending.Value);
                }
            }

            File.Delete(zipPath);
            File.Move(tempZipPath, zipPath);
        }
        catch
        {
            if (File.Exists(tempZipPath))
            {
                File.Delete(tempZipPath);
            }

            throw;
        }
    }

    private string ReadConfigText(VehicleConfigItem item)
    {
        if (!item.IsZip)
        {
            return File.ReadAllText(item.InfoPath);
        }

        using var archive = ZipFile.OpenRead(item.SourcePath);
        var entry = FindZipEntry(archive, item.InfoPath);
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

    private static (int? min, int? max) ReadYears(JsonNode? root)
    {
        if (root == null)
        {
            return (null, null);
        }

        var years = root["Years"] as JsonObject ?? root["aggregates"]?["Years"] as JsonObject;
        if (years == null)
        {
            return (null, null);
        }

        var min = ReadIntFromNode(years["min"]);
        var max = ReadIntFromNode(years["max"]);
        return (min, max);
    }

    private static string? ReadString(JsonNode? root, string key)
    {
        if (root == null)
        {
            return null;
        }

        if (root[key] is JsonValue val && val.TryGetValue<string>(out var result))
        {
            return result;
        }

        return null;
    }

    private static string? ReadStringOrAggregate(JsonNode? root, string key)
    {
        if (root == null)
        {
            return null;
        }

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

    private static double? ReadDouble(JsonNode? root, string key)
    {
        if (root == null)
        {
            return null;
        }

        if (root[key] is JsonValue val && val.TryGetValue<double>(out var result))
        {
            return result;
        }

        return null;
    }

    private static int? ReadInt(JsonNode? root, string key)
    {
        if (root == null)
        {
            return null;
        }

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

