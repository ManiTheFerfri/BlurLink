using System.Collections.ObjectModel;
using BlurLink.Shell.ViewModels;

namespace BlurLink.Shell.Notifications;

public enum NoticeSeverity { Info, Progress, Warning, Error }

public sealed record Notice(string Title, string Message, NoticeSeverity Severity, DateTime TimestampUtc);

public sealed class NotificationCenter : ShellViewModelBase
{
    public ObservableCollection<Notice> Notices { get; } = new();

    public RelayCommand DismissCommand { get; }

    public NotificationCenter()
    {
        DismissCommand = new RelayCommand(p => { if (p is Notice n) Dismiss(n); });
    }

    public void Notify(string title, string message, NoticeSeverity Severity)
        => Notices.Add(new Notice(title, message, Severity, DateTime.UtcNow));

    public void Dismiss(Notice notice) => Notices.Remove(notice);

    public void Clear() => Notices.Clear();
}
