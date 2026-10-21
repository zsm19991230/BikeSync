namespace BikeSync.App.Services;

public enum NotificationKind
{
    Informational,
    Success,
    Warning,
    Error,
}

public sealed record AppNotification(string Message, NotificationKind Kind);

public sealed class NotificationService
{
    public event EventHandler<AppNotification>? Published;

    public void Info(string message) => Show(message, NotificationKind.Informational);

    public void Success(string message) => Show(message, NotificationKind.Success);

    public void Warning(string message) => Show(message, NotificationKind.Warning);

    public void Error(string message) => Show(message, NotificationKind.Error);

    private void Show(string message, NotificationKind kind)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Published?.Invoke(this, new AppNotification(message.Trim(), kind));
    }
}
