using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.Identity.Api.Application;
using DigitalBanking.Identity.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DigitalBanking.Identity.Api.Controllers;

[ApiController]
[Route("api/users")]
public sealed class UsersController(UserService userService, CurrentUser currentUser) : ControllerBase
{
    [Authorize]
    [HttpGet("get-current-user")]
    public async Task<ActionResult<CurrentUserResponse>> GetCurrentUser(CancellationToken cancellationToken)
    {
        return Ok(await userService.GetCurrentUserAsync(currentUser.UserId, cancellationToken));
    }

    [Authorize]
    [HttpGet("active-users-except")]
    public async Task<ActionResult<IReadOnlyList<DirectoryUserResponse>>> GetActiveUsersExcept(CancellationToken cancellationToken)
    {
        return Ok(await userService.GetActiveUsersExceptAsync(currentUser.UserId, currentUser.Role, cancellationToken));
    }

    [Authorize]
    [HttpGet("user-by-id/{userId:guid}")]
    public async Task<ActionResult<DirectoryUserResponse>> GetUserById(Guid userId, CancellationToken cancellationToken)
    {
        return Ok(await userService.GetUserByIdAsync(userId, cancellationToken));
    }
}
