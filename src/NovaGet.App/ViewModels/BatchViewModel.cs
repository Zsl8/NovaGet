using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaGet.App.Localization;
using NovaGet.Core.ImportExport;

namespace NovaGet.App.ViewModels;

/// <summary>"Add batch download" (section 13.1): an address with <c>*</c>, a range, and a preview of the first and last file.</summary>
public sealed partial class BatchViewModel : ObservableObject
{
    public BatchViewModel(string? initialAddress = null)
    {
        _address = initialAddress ?? string.Empty;
        Update();
    }

    [ObservableProperty]
    private string _address;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLetters), nameof(IsSizeEnabled))]
    private bool _isNumbers = true;

    [ObservableProperty]
    private string _from = "1";

    [ObservableProperty]
    private string _to = "10";

    [ObservableProperty]
    private int _wildcardSize = 1;

    [ObservableProperty]
    private bool _useAuthorization;

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _firstFile = string.Empty;

    [ObservableProperty]
    private string _lastFile = string.Empty;

    [ObservableProperty]
    private string _countText = string.Empty;

    [ObservableProperty]
    private string? _error;

    public bool IsLetters
    {
        get => !IsNumbers;
        set => IsNumbers = !value;
    }

    /// <summary>The wildcard size pads numbers; letters are always one character.</summary>
    public bool IsSizeEnabled => IsNumbers;

    public BatchGenerator Generator => new()
    {
        Template = Address,
        Mode = IsNumbers ? BatchMode.Numbers : BatchMode.Letters,
        From = From,
        To = To,
        WildcardSize = WildcardSize,
    };

    [RelayCommand]
    private void IncreaseSize() => WildcardSize = Math.Min(BatchGenerator.MaxWildcardSize, WildcardSize + 1);

    [RelayCommand]
    private void DecreaseSize() => WildcardSize = Math.Max(1, WildcardSize - 1);

    partial void OnIsNumbersChanged(bool value)
    {
        // Each mode has its own kind of range; switching starts from that mode's usual one.
        (From, To) = value ? ("1", "10") : ("a", "z");
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Address) or nameof(IsNumbers) or nameof(From) or nameof(To) or nameof(WildcardSize))
        {
            Update();
        }
    }

    /// <summary>The message for a batch error, or null.</summary>
    public static string? ErrorText(BatchError? error) => error switch
    {
        null => null,
        BatchError.TooMany => Localizer.Format("Batch_Error_TooMany", BatchGenerator.MaxCount),
        BatchError.BadWildcardSize => Localizer.Format("Batch_Error_BadWildcardSize", BatchGenerator.MaxWildcardSize),
        _ => Localizer.Get($"Batch_Error_{error}"),
    };

    private void Update()
    {
        var generator = Generator;
        Error = string.IsNullOrWhiteSpace(Address) ? null : ErrorText(generator.Error);
        FirstFile = generator.First ?? string.Empty;
        LastFile = generator.Last ?? string.Empty;
        CountText = generator.Count > 0 ? Localizer.Format("Batch_Count", generator.Count) : string.Empty;
    }
}
