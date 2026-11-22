using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.App.Localization;

namespace CodexSwitcher.App.Features.Launch;

public sealed class CodexLaunchProfileItemViewModel
{
    public CodexLaunchProfile Profile { get; }

    public CodexLaunchSession? RunningSession { get; }
    public bool IsRunning => RunningSession is not null;

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

    public CodexLaunchProfileItemViewModel(CodexLaunchProfile profile, string targetLabel, CodexLaunchSession? runningSession)
    {
        Profile = profile;
        TargetLabel = targetLabel;
        RunningSession = runningSession;
    }
}
