using System.Collections.ObjectModel;
using Zyber.Services;

namespace Zyber.Models;

public sealed record GitChangeItem(GitChange Change, bool Staged)
{
    public string Path => Change.Path;
    public string Status => Change.Conflict ? "Conflict" : Change.Untracked ? "New" : (Staged ? Change.Index : Change.WorkingTree).ToString();
    public string Action => Staged ? "Unstage" : "Stage";
    public string Group => Staged ? "Staged" : "Unstaged";
}
public sealed class GitPanelState : ObservableState
{
    public ObservableCollection<GitChangeItem> Changes { get; } = [];
    public ObservableCollection<GitCommit> History { get; } = [];
    private bool historyVisible;
    public bool ShowHistory { get => historyVisible; set { historyVisible = value; Notify(); Notify(nameof(ShowChanges)); } }
    public bool ShowChanges => !ShowHistory;
    private bool busy;
    public bool Busy { get => busy; set { busy = value; Notify(); Notify(nameof(CanAct)); } }
    private bool repository;
    public bool HasRepository { get => repository; set { repository = value; Notify(); Notify(nameof(CanAct)); Notify(nameof(NoRepository)); } }
    public bool NoRepository => !HasRepository;
    public bool CanAct => HasRepository && !Busy;
    private string branch = "";
    public string Branch { get => branch; set { branch = value; Notify(); } }
    private string sync = "Push";
    public string Sync { get => sync; set { sync = value; Notify(); } }
    private string message = "Open a project to view Git changes";
    public string Message { get => message; set { message = value; Notify(); Notify(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    private string commitMessage = "";
    public string CommitMessage { get => commitMessage; set { commitMessage = value; Notify(); } }
    public string CommitLabel => Changes.Any(change => change.Staged) ? "Commit" : "Commit Tracked";
    public bool IsClean => HasRepository && Changes.Count == 0;
    public string LatestCommit => History.FirstOrDefault()?.Subject ?? "No commits yet";
    public void Changed() { Notify(nameof(CommitLabel)); Notify(nameof(IsClean)); Notify(nameof(LatestCommit)); }
}
