using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodexSwitcher.App.Features.Launch;

public sealed partial class CodexLaunchProfileItemViewModel : ObservableObject
{
    public CodexLaunchProfile Profile { get; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    public string Name => Profile.Name;
    public string TargetLabel { get; }
    public string RuntimeLabel => Profile.RuntimeKind == CodexLaunchRuntimeKind.Desktop
        ? "Codex Desktop · Experimental"
        : "Codex CLI";
    public string ModelLabel => Profile.Model ?? string.Empty;
    public string WorkingDirectoryLabel => string.IsNullOrWhiteSpace(Profile.WorkingDirectory)
        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : Profile.WorkingDirectory;
    public string ActionLabel => IsRunning
        ? Strings.Current.TrayLaunchProfileStop
        : Strings.Current.TrayLaunchProfileStart;

    public CodexLaunchProfileItemViewModel(CodexLaunchProfile profile, string targetLabel, bool isRunning)
    {
        Profile = profile;
        TargetLabel = targetLabel;
        IsRunning = isRunning;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsRunning))
            {
                OnPropertyChanged(nameof(ActionLabel));
            }
        };
    }
}
