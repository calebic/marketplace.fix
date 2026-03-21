using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Threading;

namespace WpfApp1;

public partial class InternetLookupWindow : Window
{
    private readonly RealVehiclePricingService _pricingService = RealVehiclePricingService.CreateDefault();
    private CancellationTokenSource? _lookupCts;
    public InternetLookupResult? LookupResult { get; private set; }
    private List<InternetLookupOption> _currentOptions = new();

    public InternetLookupWindow(VehicleConfigItem item)
    {
        InitializeComponent();
        MakeTextBox.Text = item.VehicleInfoBrand ?? item.Brand ?? item.SourceHintMake ?? string.Empty;
        ModelTextBox.Text = item.VehicleInfoName ?? item.VehicleName ?? item.SourceHintModel ?? string.Empty;
        YearMinTextBox.Text = (item.VehicleInfoYearMin ?? item.YearMin)?.ToString() ?? string.Empty;
        YearMaxTextBox.Text = (item.VehicleInfoYearMax ?? item.YearMax)?.ToString() ?? string.Empty;
        BodyStyleTextBox.Text = item.VehicleInfoBodyStyle ?? item.BodyStyle ?? string.Empty;
        NoResultTextBlock.Text = "No lookup run yet.";
        _currentOptions = new List<InternetLookupOption>();
        ResultListBox.ItemsSource = _currentOptions;
        ApplyButton.IsEnabled = false;
        Closing += InternetLookupWindow_Closing;
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        _lookupCts?.Cancel();
        _lookupCts?.Dispose();
        _lookupCts = new CancellationTokenSource(TimeSpan.FromSeconds(RealVehiclePricingService.GetLookupTimeoutSeconds()));

        SearchOnlineButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        _currentOptions = new List<InternetLookupOption>();
        ResultListBox.ItemsSource = _currentOptions;
        ResultListBox.SelectedItem = null;
        LookupResult = null;
        NoResultTextBlock.Visibility = Visibility.Visible;
        NoResultTextBlock.Text = "Searching online...";

        var make = MakeTextBox.Text?.Trim();
        var model = ModelTextBox.Text?.Trim();
        var bodyStyle = BodyStyleTextBox.Text?.Trim();
        int? yearMin = int.TryParse(YearMinTextBox.Text, out var ymin) ? ymin : null;
        int? yearMax = int.TryParse(YearMaxTextBox.Text, out var ymax) ? ymax : null;

        try
        {
            var results = await Task.Run(() => RunLookupWithFallbacks(
                make,
                model,
                yearMin,
                yearMax,
                bodyStyle,
                _lookupCts.Token), _lookupCts.Token);

            _currentOptions = results.Select(InternetLookupOption.FromResult).ToList();
            LookupResult = _currentOptions.FirstOrDefault()?.Result;
            ResultListBox.ItemsSource = _currentOptions;

            if (_currentOptions.Count == 0)
            {
                NoResultTextBlock.Text = "No result was found. Try a more specific make/model, or remove extra detail and search again.";
                return;
            }

            ResultListBox.SelectedIndex = 0;
            NoResultTextBlock.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            NoResultTextBlock.Text = "Lookup timed out before a result came back. Try again or narrow the make, model, or year.";
        }
        catch (Exception ex)
        {
            NoResultTextBlock.Text = "Lookup failed: " + ex.Message;
        }
        finally
        {
            SearchOnlineButton.IsEnabled = true;
        }
    }


    private List<InternetLookupResult> RunLookupWithFallbacks(string? make, string? model, int? yearMin, int? yearMax, string? bodyStyle, CancellationToken cancellationToken)
    {
        List<InternetLookupResult> Attempt(string? attemptBodyStyle, bool relaxedYears)
        {
            return _pricingService.ManualLookupOptions(
                make,
                model,
                relaxedYears ? null : yearMin,
                relaxedYears ? null : yearMax,
                attemptBodyStyle,
                message => Dispatcher.BeginInvoke(new Action(() => NoResultTextBlock.Text = message)),
                cancellationToken);
        }

        var results = Attempt(bodyStyle, relaxedYears: false);
        if (results.Count > 0)
        {
            return results;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(bodyStyle))
        {
            Dispatcher.BeginInvoke(new Action(() => NoResultTextBlock.Text = "No result yet. Retrying without body style..."));
            results = Attempt(null, relaxedYears: false);
            if (results.Count > 0)
            {
                return results;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (yearMin.HasValue || yearMax.HasValue)
        {
            Dispatcher.BeginInvoke(new Action(() => NoResultTextBlock.Text = "No result yet. Retrying with relaxed year matching..."));
            results = Attempt(bodyStyle, relaxedYears: true);
            if (results.Count > 0)
            {
                return results;
            }

            if (!string.IsNullOrWhiteSpace(bodyStyle))
            {
                results = Attempt(null, relaxedYears: true);
            }
        }

        return results;
    }

    private void InternetLookupWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _lookupCts?.Cancel();
        _lookupCts?.Dispose();
        _lookupCts = null;
    }

    private void ResultListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LookupResult = (ResultListBox.SelectedItem as InternetLookupOption)?.Result;
        ApplyButton.IsEnabled = LookupResult != null;
    }

    private void SourceLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
            // non-blocking
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (LookupResult == null || ResultListBox.SelectedItem is not InternetLookupOption)
        {
            DialogResult = false;
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

public sealed class InternetLookupOption
{
    public InternetLookupResult Result { get; init; } = new();
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string EvidenceLine { get; init; } = string.Empty;
    public string SourceHostLabel { get; init; } = string.Empty;
    public string SourceDisplayUrl { get; init; } = string.Empty;
    public string SourceUrl { get; init; } = string.Empty;
    public string VerificationStatus { get; init; } = string.Empty;
    public string SourceCaption { get; init; } = string.Empty;

    public static InternetLookupOption FromResult(InternetLookupResult result)
    {
        var title = string.Join(" ", new[] { result.Brand, result.Model }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Lookup result";
        }

        var parts = new List<string>();
        if (result.YearMin.HasValue || result.YearMax.HasValue)
        {
            parts.Add($"Years: {result.YearMin?.ToString() ?? "?"} - {result.YearMax?.ToString() ?? "?"}");
        }

        if (!string.IsNullOrWhiteSpace(result.BodyStyle))
        {
            parts.Add($"Body style: {result.BodyStyle}");
        }

        if (result.EstimatedValue.HasValue)
        {
            parts.Add($"Recommended value: ${Math.Round(result.EstimatedValue.Value):N0}");
        }

        if (result.Population.HasValue)
        {
            parts.Add($"Recommended population: {result.Population.Value:N0}");
        }

        var sourceUrl = string.IsNullOrWhiteSpace(result.SourceUrl)
            ? string.Empty
            : result.SourceUrl!;

        string sourceLabel;
        string sourceDisplayUrl;
        var sourceCaption = string.IsNullOrWhiteSpace(result.SourceName) ? "Source" : result.SourceName!;
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            sourceLabel = "No direct source link";
            sourceDisplayUrl = "The lookup did not yield a stable page URL.";
        }
        else
        {
            try
            {
                var uri = new Uri(sourceUrl);
                sourceLabel = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                    ? uri.Host.Substring(4)
                    : uri.Host;
                sourceDisplayUrl = uri.AbsoluteUri;
            }
            catch
            {
                sourceLabel = "Open source";
                sourceDisplayUrl = sourceUrl;
            }
        }

        return new InternetLookupOption
        {
            Result = result,
            Title = title,
            Summary = string.Join(Environment.NewLine, parts),
            EvidenceLine = string.IsNullOrWhiteSpace(result.Evidence) ? string.Empty : $"Evidence: {result.Evidence}",
            SourceUrl = sourceUrl,
            SourceHostLabel = sourceLabel,
            SourceDisplayUrl = sourceDisplayUrl,
            VerificationStatus = string.IsNullOrWhiteSpace(result.VerificationStatus) ? "Fallback" : result.VerificationStatus!,
            SourceCaption = sourceCaption
        };
    }
}
