using System.Windows.Data;
using System.IO;

namespace WpfApp1;

public partial class MainWindow
{
    private static readonly string PersistedModMemoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BeamNGMarketplaceConfigEditor",
        "mod-memory.json");

    private readonly ModMemoryStore _modMemoryStore = new(PersistedModMemoryPath);

    private void ApplyPersistedModMemoryToConfigs()
    {
        var memory = _modMemoryStore.Load();
        foreach (var item in Configs)
        {
            var configKey = ModMemoryStore.BuildConfigKey(item);
            if (memory.Configs.TryGetValue(configKey, out var configMemory))
            {
                configMemory.ApplyTo(item);
            }

            var modKey = ModMemoryStore.BuildModKey(item);
            if (memory.Mods.TryGetValue(modKey, out var modMemory))
            {
                item.ContentCategory = modMemory.ContentCategory ?? item.ContentCategory;
                item.IsMapMod = modMemory.IsMapMod || item.IsMapMod;
                item.IgnoreFromRenamer = modMemory.IgnoreFromReview || item.IgnoreFromRenamer;
                item.SourceHintMake = modMemory.SourceHintMake ?? item.SourceHintMake;
                item.SourceHintModel = modMemory.SourceHintModel ?? item.SourceHintModel;
                item.SourceHintYearMin = modMemory.SourceHintYearMin ?? item.SourceHintYearMin;
                item.SourceHintYearMax = modMemory.SourceHintYearMax ?? item.SourceHintYearMax;
            }

            item.NotifyChanges();
        }
    }

    private void SaveModMemorySnapshot()
    {
        _modMemoryStore.SaveFrom(Configs);
    }

    private void RefreshAllWorkspaceState(bool persist = false)
    {
        RefreshWorkspaceSummary();
        RefreshWorkspacePageSummaries();
        RefreshDataPageSummary();
        RefreshLogsPage(forceFullReload: _currentWorkspacePage == "Logs");
        _configsView?.Refresh();
        if (CollectionViewSource.GetDefaultView(FlaggedConfigs) is { } flaggedView)
        {
            flaggedView.Refresh();
        }

        RefreshLicensePlatesPageSummary();
        BuildLicensePlatesPageSelectionSources();
        RefreshLicensePlatesPageUi();

        if (_selected != null && !Configs.Contains(_selected))
        {
            _selected = null;
            ClearForm();
        }

        UpdateEmptyState();
        if (persist)
        {
            SaveModMemorySnapshot();
        }
    }

    private void FlaggedConfigsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid || grid.SelectedItem is not VehicleConfigItem item)
        {
            return;
        }

        _selected = item;
        SetWorkspacePage("Results");
        ConfigsGrid.SelectedItem = item;
        ConfigsGrid.ScrollIntoView(item);
        SetDirty(false);
        LoadForm(item);
    }
}
