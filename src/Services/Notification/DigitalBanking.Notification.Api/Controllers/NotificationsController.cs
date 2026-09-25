using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.Notification.Api.Application;
using DigitalBanking.Notification.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DigitalBanking.Notification.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/notifications")]
public sealed class NotificationsController(NotificationService notificationService, CurrentUser currentUser) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserNotification>>> List(CancellationToken cancellationToken)
    {
        return Ok(await notificationService.ListAsync(currentUser.UserId, cancellationToken));
    }

    [HttpPost("{notificationId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid notificationId, CancellationToken cancellationToken)
    {
        await notificationService.MarkReadAsync(currentUser.UserId, notificationId, cancellationToken);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        await notificationService.MarkAllReadAsync(currentUser.UserId, cancellationToken);
        return NoContent();
    }
}
