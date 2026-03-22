using System;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MahApps.Metro.Controls;

namespace WpfApp1;

public partial class RenamerWizardWindow : MetroWindow
{
    public ObservableCollection<SourceReviewEntry> Entries { get; }

    public List<SourceReviewEntry> RetriedEntries { get; } = new();
    public List<SourceReviewEntry> IgnoredEntries { get; } = new();
    public List<SourceReviewEntry> ReviewedEntries { get; } = new();

    private SourceReviewEntry? _current;

    public RenamerWizardWindow(IEnumerable<SourceReviewEntry> entries)
    {
        Entries = new ObservableCollection<SourceReviewEntry>(entries);
        InitializeComponent();
        DataContext = this;
        if (Entries.Count > 0)
        {
            EntriesGrid.SelectedIndex = 0;
        }
        UpdateFooter();
    }

    private void EntriesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _current = EntriesGrid.SelectedItem as SourceReviewEntry;
        RefreshCurrent();
    }

    private void RefreshCurrent()
    {
        if (_current == null)
        {
            SourceNameText.Text = "Select a mod";
            ReasonText.Text = string.Empty;
            PathText.Text = string.Empty;
            PreviewImage.Source = null;
            PreviewFallbackText.Visibility = System.Windows.Visibility.Visible;
            ZipRenameTextBox.Text = string.Empty;
            MakeTextBox.Text = string.Empty;
            ModelTextBox.Text = string.Empty;
            YearMinTextBox.Text = string.Empty;
            YearMaxTextBox.Text = string.Empty;
            return;
        }

        SourceNameText.Text = _current.DisplayName;
        ReasonText.Text = _current.Reason ?? "This mod needs review.";
        PathText.Text = _current.SourcePath;
        LoadPreviewImage(_current.PreviewImagePath);
        ZipRenameTextBox.Text = _current.PendingZipName;
        MakeTextBox.Text = _current.UserMake;
        ModelTextBox.Text = _current.UserModel;
        YearMinTextBox.Text = _current.UserYearMinText;
        YearMaxTextBox.Text = _current.UserYearMaxText;
        MakeTextBox.Focus();
        MakeTextBox.SelectAll();
    }


    private void LoadPreviewImage(string? previewPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(previewPath) || !File.Exists(previewPath))
            {
                PreviewImage.Source = null;
                PreviewFallbackText.Visibility = System.Windows.Visibility.Visible;
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(previewPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            PreviewImage.Source = bitmap;
            PreviewFallbackText.Visibility = System.Windows.Visibility.Collapsed;
        }
        catch
        {
            PreviewImage.Source = null;
            PreviewFallbackText.Visibility = System.Windows.Visibility.Visible;
        }
    }


    private void ZipRenameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_current != null)
        {
            _current.PendingZipName = ZipRenameTextBox.Text.Trim();
        }
    }

    private void MakeTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_current != null)
        {
            _current.UserMake = MakeTextBox.Text.Trim();
        }
    }

    private void ModelTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_current != null)
        {
            _current.UserModel = ModelTextBox.Text.Trim();
        }
    }

    private void YearMinTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_current != null)
        {
            _current.UserYearMinText = YearMinTextBox.Text.Trim();
        }
    }

    private void YearMaxTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_current != null)
        {
            _current.UserYearMaxText = YearMaxTextBox.Text.Trim();
        }
    }

    private void RetryCurrent_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }

        _current.Status = _current.HasRetryInput ? "Retry queued" : "Retry queued (no hints)";
        if (!ReviewedEntries.Contains(_current)) ReviewedEntries.Add(_current);
        if (!RetriedEntries.Contains(_current)) RetriedEntries.Add(_current);
        AdvanceSelection();
    }

    private void SkipCurrent_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }

        _current.Status = "Skipped";
        if (!ReviewedEntries.Contains(_current)) ReviewedEntries.Add(_current);
        AdvanceSelection();
    }

    private void IgnoreCurrent_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }

        _current.Status = "Ignored";
        if (!ReviewedEntries.Contains(_current)) ReviewedEntries.Add(_current);
        if (!IgnoredEntries.Contains(_current)) IgnoredEntries.Add(_current);
        AdvanceSelection();
    }

    private void OpenLocation_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }

        try
        {
            var target = File.Exists(_current.SourcePath) ? _current.SourcePath : Path.GetDirectoryName(_current.SourcePath) ?? _current.SourcePath;
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private void Done_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Entries.Any(x => x.Status == "Pending"))
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    private void AdvanceSelection()
    {
        UpdateFooter();
        var pendingIndex = Entries.IndexOf(Entries.FirstOrDefault(x => x.Status == "Pending")!);
        if (pendingIndex >= 0)
        {
            EntriesGrid.SelectedIndex = pendingIndex;
            return;
        }

        DialogResult = true;
        Close();
    }

    private void UpdateFooter()
    {
        var pending = Entries.Count(x => x.Status == "Pending");
        FooterText.Text = pending > 0
            ? $"{pending} mod(s) still need a choice."
            : "All mods have been handled. The app will retry only the selected mods.";

        if (DoneButton != null)
        {
            DoneButton.IsEnabled = pending == 0;
        }
    }
}
