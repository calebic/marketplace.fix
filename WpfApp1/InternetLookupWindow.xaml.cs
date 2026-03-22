using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace WpfApp1;

public partial class InternetLookupWindow : Window
{
    private readonly RealVehiclePricingService _pricingService = RealVehiclePricingService.CreateDefault();
    private CancellationTokenSource? _lookupCts;
    private string _latestProgressMessage = string.Empty;
    public InternetLookupResult? LookupResult { get; private set; }
    public string LastRunSummary { get; private set; } = string.Empty;
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
        UpdateLookupStatus("Idle");
        UpdateLookupDiagnostics("Run a lookup to see why a result ranked well, whether fallbacks were used, or why nothing strong was found.");
        Closing += InternetLookupWindow_Closing;
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        _lookupCts?.Cancel();
        _lookupCts?.Dispose();
        var manualLookupTimeoutSeconds = Math.Clamp(Math.Max(RealVehiclePricingService.GetLookupTimeoutSeconds() * 2, 18), 12, 45);
        _lookupCts = new CancellationTokenSource(TimeSpan.FromSeconds(manualLookupTimeoutSeconds));

        SearchOnlineButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        _currentOptions = new List<InternetLookupOption>();
        ResultListBox.ItemsSource = _currentOptions;
        ResultListBox.SelectedItem = null;
        LookupResult = null;
        LastRunSummary = string.Empty;
        _latestProgressMessage = string.Empty;
        NoResultTextBlock.Visibility = Visibility.Visible;
        NoResultTextBlock.Text = "Searching online...";
        UpdateLookupStatus("Running");
        UpdateLookupDiagnostics("Starting strict lookup with the current make, model, year, and body style.");

        var make = MakeTextBox.Text?.Trim();
        var model = ModelTextBox.Text?.Trim();
        var bodyStyle = BodyStyleTextBox.Text?.Trim();
        int? yearMin = int.TryParse(YearMinTextBox.Text, out var ymin) ? ymin : null;
        int? yearMax = int.TryParse(YearMaxTextBox.Text, out var ymax) ? ymax : null;

        try
        {
            var summary = await Task.Run(() => RunLookupWithFallbacks(
                make,
                model,
                yearMin,
                yearMax,
                bodyStyle,
                _lookupCts.Token), _lookupCts.Token);

            _currentOptions = summary.Results.Select((result, index) => InternetLookupOption.FromResult(result, index == 0)).ToList();
            ResultListBox.ItemsSource = _currentOptions;

            if (_currentOptions.Count == 0)
            {
                UpdateLookupStatus(summary.StatusLabel);
                UpdateLookupDiagnostics(summary.Detail);
                NoResultTextBlock.Text = summary.Headline;
                LastRunSummary = $"{summary.StatusLabel}: {summary.Headline} :: {summary.Detail}";
                return;
            }

            LookupResult = _currentOptions.First().Result;
            ResultListBox.SelectedIndex = 0;
            NoResultTextBlock.Visibility = Visibility.Collapsed;
            var best = _currentOptions[0];
            UpdateLookupStatus(summary.StatusLabel);
            UpdateLookupDiagnostics(summary.Detail);
            LastRunSummary = $"{summary.StatusLabel}: returned {_currentOptions.Count} candidate(s); best={best.Title}; {summary.Detail}";
        }
        catch (OperationCanceledException)
        {
            UpdateLookupStatus("Timed out");
            var detail = BuildAttemptSummary(bodyStyle, yearMin, yearMax, timedOut: true);
            UpdateLookupDiagnostics(detail);
            NoResultTextBlock.Text = "Lookup budget expired before a strong result came back. Try again or narrow the vehicle details.";
            LastRunSummary = $"Timed out: {detail}";
        }
        catch (Exception ex)
        {
            UpdateLookupStatus("Failed");
            UpdateLookupDiagnostics("The lookup pipeline threw an error before it could rank candidates.");
            NoResultTextBlock.Text = "Lookup failed: " + ex.Message;
            LastRunSummary = $"Failed: {ex.Message}";
        }
        finally
        {
            SearchOnlineButton.IsEnabled = true;
        }
    }

    private LookupRunSummary RunLookupWithFallbacks(string? make, string? model, int? yearMin, int? yearMax, string? bodyStyle, CancellationToken cancellationToken)
    {
        var summary = new LookupRunSummary();

        List<InternetLookupResult> Attempt(string? attemptBodyStyle, bool relaxedYears, string stageLabel)
        {
            summary.Attempts.Add(stageLabel);
            return _pricingService.ManualLookupOptions(
                make,
                model,
                relaxedYears ? null : yearMin,
                relaxedYears ? null : yearMax,
                attemptBodyStyle,
                message =>
                {
                    _latestProgressMessage = message;
                    Dispatcher.BeginInvoke(new Action(() => NoResultTextBlock.Text = message));
                },
                cancellationToken);
        }

        summary.Results = Attempt(bodyStyle, relaxedYears: false, stageLabel: "strict match");
        if (summary.Results.Count > 0)
        {
            summary.StatusLabel = "Ready";
            summary.Headline = $"Returned {summary.Results.Count} lookup candidate(s).";
            summary.Detail = BuildAttemptSummary(bodyStyle, yearMin, yearMax, timedOut: false) + " Best candidates kept only when they were strong enough to apply.";
            return summary;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(bodyStyle))
        {
            summary.UsedBodyStyleFallback = true;
            Dispatcher.BeginInvoke(new Action(() => NoResultTextBlock.Text = "No strong result yet. Retrying without body style..."));
            summary.Results = Attempt(null, relaxedYears: false, stageLabel: "no-body-style fallback");
            if (summary.Results.Count > 0)
            {
                summary.StatusLabel = "Fallback hit";
                summary.Headline = $"Returned {summary.Results.Count} lookup candidate(s).";
                summary.Detail = BuildAttemptSummary(bodyStyle, yearMin, yearMax, timedOut: false) + " Body style was relaxed to surface a usable identity match.";
                return summary;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (yearMin.HasValue || yearMax.HasValue)
        {
            summary.UsedRelaxedYearFallback = true;
            Dispatcher.BeginInvoke(new Action(() => NoResultTextBlock.Text = "No strong result yet. Retrying with relaxed year matching..."));
            summary.Results = Attempt(bodyStyle, relaxedYears: true, stageLabel: "relaxed-year fallback");
            if (summary.Results.Count > 0)
            {
                summary.StatusLabel = "Fallback hit";
                summary.Headline = $"Returned {summary.Results.Count} lookup candidate(s).";
                summary.Detail = BuildAttemptSummary(bodyStyle, yearMin, yearMax, timedOut: false) + " Year matching was relaxed to avoid rejecting an otherwise good model match.";
                return summary;
            }

            if (!string.IsNullOrWhiteSpace(bodyStyle))
            {
                summary.Results = Attempt(null, relaxedYears: true, stageLabel: "relaxed-year and no-body-style fallback");
                if (summary.Results.Count > 0)
                {
                    summary.StatusLabel = "Fallback hit";
                    summary.Headline = $"Returned {summary.Results.Count} lookup candidate(s).";
                    summary.Detail = BuildAttemptSummary(bodyStyle, yearMin, yearMax, timedOut: false) + " Both year and body style were relaxed to find a believable match.";
                    return summary;
                }
            }
        }

        summary.StatusLabel = "No strong match";
        summary.Headline = "No strong identity match was found for that search.";
        summary.Detail = BuildAttemptSummary(bodyStyle, yearMin, yearMax, timedOut: false) +
                         (string.IsNullOrWhiteSpace(_latestProgressMessage)
                             ? string.Empty
                             : $" Last lookup step: {_latestProgressMessage}.");
        return summary;
    }

    private string BuildAttemptSummary(string? bodyStyle, int? yearMin, int? yearMax, bool timedOut)
    {
        var attempts = new List<string> { "strict match" };
        if (!string.IsNullOrWhiteSpace(bodyStyle))
        {
            attempts.Add("no-body-style fallback");
        }

        if (yearMin.HasValue || yearMax.HasValue)
        {
            attempts.Add("relaxed-year fallback");
            if (!string.IsNullOrWhiteSpace(bodyStyle))
            {
                attempts.Add("relaxed-year + no-body-style fallback");
            }
        }

        var summary = "Tried " + string.Join(" → ", attempts) + ".";
        if (timedOut && !string.IsNullOrWhiteSpace(_latestProgressMessage))
        {
            summary += " Lookup budget expired while it was on: " + _latestProgressMessage + ".";
        }

        return summary;
    }

    private void UpdateLookupStatus(string status)
    {
        LookupStatusBadgeText.Text = status;
    }

    private void UpdateLookupDiagnostics(string detail)
    {
        LookupDiagnosticsTextBlock.Text = string.IsNullOrWhiteSpace(detail)
            ? "No extra lookup diagnostics yet."
            : detail;
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
        if (LookupResult == null || ResultListBox.SelectedItem is not InternetLookupOption option)
        {
            DialogResult = false;
            return;
        }

        LastRunSummary = $"Applied: {option.Title} :: {option.MatchSummary}";
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private sealed class LookupRunSummary
    {
        public List<InternetLookupResult> Results { get; set; } = new();
        public List<string> Attempts { get; } = new();
        public bool UsedBodyStyleFallback { get; set; }
        public bool UsedRelaxedYearFallback { get; set; }
        public string StatusLabel { get; set; } = "Idle";
        public string Headline { get; set; } = "No lookup run yet.";
        public string Detail { get; set; } = string.Empty;
    }
}

public sealed class InternetLookupOption
{
    public InternetLookupResult Result { get; init; } = new();
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string MatchSummary { get; init; } = string.Empty;
    public string EvidenceLine { get; init; } = string.Empty;
    public string SourceHostLabel { get; init; } = string.Empty;
    public string SourceDisplayUrl { get; init; } = string.Empty;
    public string SourceUrl { get; init; } = string.Empty;
    public string VerificationStatus { get; init; } = string.Empty;
    public string ConfidenceCaption { get; init; } = string.Empty;
    public string SourceCaption { get; init; } = string.Empty;

    public static InternetLookupOption FromResult(InternetLookupResult result, bool isBestCandidate)
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
                    ? uri.Host[4..]
                    : uri.Host;
                sourceDisplayUrl = uri.AbsoluteUri;
            }
            catch
            {
                sourceLabel = "Open source";
                sourceDisplayUrl = sourceUrl;
            }
        }

        var matchParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.VerificationStatus))
        {
            matchParts.Add($"Match quality: {result.VerificationStatus}");
        }
        if (result.ConfidenceScore > 0)
        {
            matchParts.Add($"Confidence {result.ConfidenceScore}");
        }
        if (isBestCandidate)
        {
            matchParts.Add("Best current candidate");
        }
        if (result.EstimatedValue.HasValue && result.Population.HasValue)
        {
            matchParts.Add("Identity and valuation both available");
        }
        else if (result.EstimatedValue.HasValue)
        {
            matchParts.Add("Pricing available; population inferred");
        }
        else
        {
            matchParts.Add("Identity-first result");
        }

        return new InternetLookupOption
        {
            Result = result,
            Title = title,
            Summary = string.Join(Environment.NewLine, parts),
            MatchSummary = string.Join(" · ", matchParts),
            EvidenceLine = string.IsNullOrWhiteSpace(result.Evidence) ? string.Empty : $"Evidence: {result.Evidence}",
            SourceUrl = sourceUrl,
            SourceHostLabel = sourceLabel,
            SourceDisplayUrl = sourceDisplayUrl,
            VerificationStatus = string.IsNullOrWhiteSpace(result.VerificationStatus) ? "Fallback" : result.VerificationStatus!,
            ConfidenceCaption = result.ConfidenceScore > 0 ? $"Confidence {result.ConfidenceScore}" : "Confidence n/a",
            SourceCaption = sourceCaption
        };
    }
}
