using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WpfApp1;

public sealed class InferenceRunOptions
{
    public static InferenceRunOptions Default { get; } = new();
    public static InferenceRunOptions LocalOnly { get; } = new()
    {
        AllowInternetIdentity = false,
        AllowOnlinePricing = false
    };

    public bool AllowInternetIdentity { get; init; } = true;
    public bool AllowOnlinePricing { get; init; } = true;
}

public sealed class ConfigWriteRequest
{
    public required VehicleConfigItem Item { get; init; }
    public required string Json { get; init; }
}

public sealed class PreparedConfigWrite
{
    public required VehicleConfigItem Item { get; init; }
    public required string Json { get; init; }
    public required Action Commit { get; init; }
}

public sealed class AutoFillWorkflowProgress
{
    public int Processed { get; init; }
    public int Total { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

public sealed class AutoFillSingleResult
{
    public required VehicleInferenceResult Inference { get; init; }
    public bool UsedOnlineStage { get; init; }
}

public sealed class AutoFillWorkflowResult
{
    public int Processed { get; set; }
    public int Updated { get; set; }
    public int Saved { get; set; }
    public int Failed { get; set; }
    public int HeldForReview { get; set; }
    public int LocalOnlyMatches { get; set; }
    public int OnlineMatches { get; set; }
    public bool WasCanceled { get; set; }
}

public sealed class WorkspaceRefreshCoordinator
{
    private readonly Action<bool> _refreshAction;
    private int _deferDepth;
    private bool _pendingRefresh;
    private bool _pendingPersist;

    public WorkspaceRefreshCoordinator(Action<bool> refreshAction)
    {
        _refreshAction = refreshAction ?? throw new ArgumentNullException(nameof(refreshAction));
    }

    public void BeginDeferredRefresh()
    {
        _deferDepth++;
    }

    public void RequestRefresh(bool persist)
    {
        if (_deferDepth > 0)
        {
            _pendingRefresh = true;
            _pendingPersist |= persist;
            return;
        }

        _refreshAction(persist);
    }

    public void EndDeferredRefresh(bool defaultPersist = false)
    {
        if (_deferDepth <= 0)
        {
            return;
        }

        _deferDepth--;
        if (_deferDepth != 0 || !_pendingRefresh)
        {
            return;
        }

        var persist = _pendingPersist || defaultPersist;
        _pendingRefresh = false;
        _pendingPersist = false;
        _refreshAction(persist);
    }
}

public sealed class AutoFillWorkflowService
{
    private readonly Func<VehicleConfigItem, InferenceRunOptions, Action<string>?, CancellationToken, Task<VehicleInferenceResult>> _inferAsync;
    private readonly Func<VehicleConfigItem, VehicleInferenceResult, bool> _shouldUseOnlinePass;
    private readonly Func<VehicleConfigItem, VehicleInferenceResult, bool> _shouldHoldForReview;
    private readonly Action<VehicleConfigItem, VehicleInferenceResult, string> _markNeedsReview;
    private readonly Func<VehicleConfigItem, VehicleInferenceResult, PreparedConfigWrite> _prepareWrite;
    private readonly Action<IReadOnlyCollection<ConfigWriteRequest>, bool> _writeBatch;
    private readonly Action<VehicleConfigItem, string, string, string> _setItemState;
    private readonly Func<VehicleConfigItem, string> _describeItem;
    private readonly Func<VehicleInferenceResult, string> _buildDetail;
    private readonly Action<string> _log;

    public AutoFillWorkflowService(
        Func<VehicleConfigItem, InferenceRunOptions, Action<string>?, CancellationToken, Task<VehicleInferenceResult>> inferAsync,
        Func<VehicleConfigItem, VehicleInferenceResult, bool> shouldUseOnlinePass,
        Func<VehicleConfigItem, VehicleInferenceResult, bool> shouldHoldForReview,
        Action<VehicleConfigItem, VehicleInferenceResult, string> markNeedsReview,
        Func<VehicleConfigItem, VehicleInferenceResult, PreparedConfigWrite> prepareWrite,
        Action<IReadOnlyCollection<ConfigWriteRequest>, bool> writeBatch,
        Action<VehicleConfigItem, string, string, string> setItemState,
        Func<VehicleConfigItem, string> describeItem,
        Func<VehicleInferenceResult, string> buildDetail,
        Action<string> log)
    {
        _inferAsync = inferAsync ?? throw new ArgumentNullException(nameof(inferAsync));
        _shouldUseOnlinePass = shouldUseOnlinePass ?? throw new ArgumentNullException(nameof(shouldUseOnlinePass));
        _shouldHoldForReview = shouldHoldForReview ?? throw new ArgumentNullException(nameof(shouldHoldForReview));
        _markNeedsReview = markNeedsReview ?? throw new ArgumentNullException(nameof(markNeedsReview));
        _prepareWrite = prepareWrite ?? throw new ArgumentNullException(nameof(prepareWrite));
        _writeBatch = writeBatch ?? throw new ArgumentNullException(nameof(writeBatch));
        _setItemState = setItemState ?? throw new ArgumentNullException(nameof(setItemState));
        _describeItem = describeItem ?? throw new ArgumentNullException(nameof(describeItem));
        _buildDetail = buildDetail ?? throw new ArgumentNullException(nameof(buildDetail));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<AutoFillSingleResult> RunSingleAsync(
        VehicleConfigItem item,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));

        progress?.Invoke("Running local vehicle inference...");
        var local = await _inferAsync(item, InferenceRunOptions.LocalOnly, progress, cancellationToken);
        if (!_shouldUseOnlinePass(item, local))
        {
            return new AutoFillSingleResult
            {
                Inference = local,
                UsedOnlineStage = false
            };
        }

        progress?.Invoke("Verifying weak match with online sources...");
        var final = await _inferAsync(item, InferenceRunOptions.Default, progress, cancellationToken);
        return new AutoFillSingleResult
        {
            Inference = final,
            UsedOnlineStage = true
        };
    }

    public async Task<AutoFillWorkflowResult> RunBatchAsync(
        IReadOnlyCollection<VehicleConfigItem> items,
        bool mirrorToVehicles,
        CancellationToken cancellationToken,
        Action<AutoFillWorkflowProgress>? progress = null,
        string operationLabel = "Auto-filling configs")
    {
        var result = new AutoFillWorkflowResult();
        if (items == null || items.Count == 0)
        {
            return result;
        }

        var total = items.Count;
        var lastProgressUtc = DateTime.MinValue;

        void ReportProgress(string status, string detail, bool force = false)
        {
            if (progress == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (!force && (now - lastProgressUtc).TotalMilliseconds < 350)
            {
                return;
            }

            lastProgressUtc = now;
            progress(new AutoFillWorkflowProgress
            {
                Processed = result.Processed,
                Total = total,
                Status = status,
                Detail = detail
            });
        }

        try
        {
            foreach (var sourceGroup in items.GroupBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceItems = sourceGroup.ToList();
                var pendingWrites = new List<ConfigWriteRequest>();
                var completedItems = new List<(VehicleConfigItem Item, string Detail, string Source, Action Commit)>();
                var sourceLabel = sourceItems.Count > 0 ? sourceItems[0].ModName : sourceGroup.Key;

                foreach (var item in sourceItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var itemLabel = _describeItem(item);
                    ReportProgress($"{operationLabel}... {result.Processed}/{total}", $"Analyzing {itemLabel}", force: result.Processed == 0);
                    _log($"WORKFLOW START {itemLabel}");

                    try
                    {
                        var local = await _inferAsync(item, InferenceRunOptions.LocalOnly, detail =>
                        {
                            ReportProgress($"{operationLabel}... {result.Processed}/{total}", $"{itemLabel}: {detail}");
                        }, cancellationToken);

                        var final = local;
                        var usedOnlineStage = false;
                        if (_shouldUseOnlinePass(item, local))
                        {
                            usedOnlineStage = true;
                            ReportProgress($"{operationLabel}... {result.Processed}/{total}", $"Verifying {itemLabel} with online sources", force: true);

                            try
                            {
                                final = await _inferAsync(item, InferenceRunOptions.Default, detail =>
                                {
                                    ReportProgress($"{operationLabel}... {result.Processed}/{total}", $"{itemLabel}: {detail}");
                                }, cancellationToken);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                _log($"WORKFLOW ONLINE FALLBACK {itemLabel} :: {ex}");
                                final = local;
                            }
                        }

                        var detail = _buildDetail(final);
                        if (_shouldHoldForReview(item, final))
                        {
                            _markNeedsReview(item, final, detail);
                            result.HeldForReview++;
                            result.Processed++;
                            _setItemState(item, "Needs review", "Held for review", detail);
                            ReportProgress($"{operationLabel}... {result.Processed}/{total}", $"{itemLabel}: held for review", force: true);
                            _log($"WORKFLOW HELD {itemLabel} :: {detail}");
                            continue;
                        }

                        var preparedWrite = _prepareWrite(item, final);
                        pendingWrites.Add(new ConfigWriteRequest
                        {
                            Item = preparedWrite.Item,
                            Json = preparedWrite.Json
                        });
                        var resultSource = string.IsNullOrWhiteSpace(final.ValueSource)
                            ? (usedOnlineStage ? "Internet/local workflow" : "Local workflow")
                            : final.ValueSource!;
                        completedItems.Add((item, detail, resultSource, preparedWrite.Commit));

                        if (usedOnlineStage)
                        {
                            result.OnlineMatches++;
                        }
                        else
                        {
                            result.LocalOnlyMatches++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        result.WasCanceled = true;
                        _setItemState(item, "Canceled", "Canceled", "Operation canceled by user.");
                        _log($"WORKFLOW CANCELED {itemLabel}");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        result.Failed++;
                        result.Processed++;
                        _setItemState(item, "Failed", "Error", ex.Message);
                        ReportProgress($"{operationLabel}... {result.Processed}/{total}", $"Failed on {itemLabel}: {ex.Message}", force: true);
                        _log($"WORKFLOW FAILED {itemLabel} :: {ex}");
                    }
                }

                if (completedItems.Count == 0)
                {
                    continue;
                }

                ReportProgress($"{operationLabel}... {result.Processed}/{total}", $"Saving {sourceLabel} ({completedItems.Count} configs)", force: true);

                try
                {
                    await Task.Run(() => _writeBatch(pendingWrites, mirrorToVehicles), cancellationToken);
                    foreach (var completed in completedItems)
                    {
                        completed.Commit();
                        result.Processed++;
                        result.Updated++;
                        result.Saved++;
                        _setItemState(completed.Item, "Completed", completed.Source, completed.Detail);
                    }

                    if (completedItems.Count > 0)
                    {
                        var lastCompleted = completedItems[^1];
                        ReportProgress($"{operationLabel}... {result.Processed}/{total}", $"{_describeItem(lastCompleted.Item)}: {lastCompleted.Detail}", force: true);
                    }

                    foreach (var completed in completedItems)
                    {
                        _log($"WORKFLOW SUCCESS {_describeItem(completed.Item)} :: Source={completed.Source} :: {completed.Detail}");
                    }
                }
                catch (Exception ex)
                {
                    foreach (var completed in completedItems)
                    {
                        result.Processed++;
                        result.Failed++;
                        _setItemState(completed.Item, "Failed", "Error", ex.Message);
                        _log($"WORKFLOW SAVE FAILED {_describeItem(completed.Item)} :: {ex}");
                    }

                    ReportProgress($"{operationLabel}... {result.Processed}/{total}", $"Failed saving {sourceLabel}: {ex.Message}", force: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            result.WasCanceled = true;
        }

        return result;
    }
}
