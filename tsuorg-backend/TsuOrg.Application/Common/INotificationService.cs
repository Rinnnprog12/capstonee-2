namespace TsuOrg.Application.Common;

public interface INotificationService
{
    Task SendAsync(
        Guid userId,
        string title,
        string message,
        Guid? relatedDocumentId = null,
        CancellationToken ct = default);
}
