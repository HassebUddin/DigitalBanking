using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.Identity.Api.Application;
using DigitalBanking.Identity.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DigitalBanking.Identity.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService authService, CurrentUser currentUser) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        return Ok(await authService.RegisterAsync(request, cancellationToken));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        return Ok(await authService.LoginAsync(request, cancellationToken));
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        return Ok(await authService.RefreshAsync(request, cancellationToken));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        await authService.LogoutAsync(request, cancellationToken);
        return NoContent();
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        await authService.ChangePasswordAsync(currentUser.UserId, request, cancellationToken);
        return NoContent();
    }

    [HttpPost("forgot-password")]
    public async Task<ActionResult<ForgotPasswordResponse>> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        return Ok(await authService.ForgotPasswordAsync(request, cancellationToken));
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        await authService.ResetPasswordAsync(request, cancellationToken);
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<CurrentUserResponse>> Me(CancellationToken cancellationToken)
    {
        return Ok(await authService.GetCurrentUserAsync(currentUser.UserId, cancellationToken));
    }

    [Authorize]
    [HttpGet("directory")]
    public async Task<ActionResult<IReadOnlyList<DirectoryUserResponse>>> Directory(CancellationToken cancellationToken)
    {
        return Ok(await authService.GetDirectoryAsync(currentUser.UserId, currentUser.Role, cancellationToken));
    }

    [Authorize]
    [HttpGet("directory/{userId:guid}")]
    public async Task<ActionResult<DirectoryUserResponse>> DirectoryUser(Guid userId, CancellationToken cancellationToken)
    {
        return Ok(await authService.GetDirectoryUserAsync(userId, cancellationToken));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("employees")]
    public async Task<ActionResult<DirectoryUserResponse>> CreateEmployee(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        return Ok(await authService.CreateEmployeeAsync(request, cancellationToken));
    }
}
