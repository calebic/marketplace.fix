using System;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WpfApp1;

public sealed class SourceReviewEntry : INotifyPropertyChanged
{
    private string _status = "Pending";
    private string _userMake = string.Empty;
    private string _userModel = string.Empty;
    private string _userYearMinText = string.Empty;
    private string _userYearMaxText = string.Empty;
    private string _sourcePath = string.Empty;
    private string _displayName = string.Empty;
    private string? _previewImagePath;
    private string _pendingZipName = string.Empty;

    public string OriginalSourcePath { get; init; } = string.Empty;

    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            if (_sourcePath == value) return;
            _sourcePath = value;
            OnPropertyChanged();
        }
    }

    public bool IsZip { get; init; }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value) return;
            _displayName = value;
            OnPropertyChanged();
        }
    }
    public string DirectoryPath { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public string ReviewCategory { get; init; } = string.Empty;
    public int ReviewPriority { get; init; }
    public string Signature { get; init; } = string.Empty;

    public string? PreviewImagePath
    {
        get => _previewImagePath;
        set
        {
            if (_previewImagePath == value) return;
            _previewImagePath = value;
            OnPropertyChanged();
        }
    }

    public string PendingZipName
    {
        get => _pendingZipName;
        set
        {
            if (_pendingZipName == value) return;
            _pendingZipName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasQueuedRename));
        }
    }

    public bool HasQueuedRename =>
        IsZip &&
        !string.IsNullOrWhiteSpace(PendingZipName) &&
        !string.Equals(PendingZipName.Trim(), Path.GetFileNameWithoutExtension(SourcePath), StringComparison.OrdinalIgnoreCase);

    public string UserMake
    {
        get => _userMake;
        set
        {
            if (_userMake == value) return;
            _userMake = value;
            OnPropertyChanged();
        }
    }

    public string UserModel
    {
        get => _userModel;
        set
        {
            if (_userModel == value) return;
            _userModel = value;
            OnPropertyChanged();
        }
    }

    public string UserYearMinText
    {
        get => _userYearMinText;
        set
        {
            if (_userYearMinText == value) return;
            _userYearMinText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UserYearSummary));
        }
    }

    public string UserYearMaxText
    {
        get => _userYearMaxText;
        set
        {
            if (_userYearMaxText == value) return;
            _userYearMaxText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UserYearSummary));
        }
    }

    public int? UserYearMin => int.TryParse(UserYearMinText, out var value) ? value : null;
    public int? UserYearMax => int.TryParse(UserYearMaxText, out var value) ? value : null;

    public string UserYearSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(UserYearMinText) && string.IsNullOrWhiteSpace(UserYearMaxText))
            {
                return string.Empty;
            }

            return $"{UserYearMinText}-{UserYearMaxText}".Trim('-');
        }
    }

    public bool HasRetryInput =>
        !string.IsNullOrWhiteSpace(UserMake) ||
        !string.IsNullOrWhiteSpace(UserModel) ||
        UserYearMin.HasValue ||
        UserYearMax.HasValue;

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public readonly record struct RenamerReviewOutcome(int ReviewedCount, int RetriedCount, int IgnoredCount, bool Rescanned);
