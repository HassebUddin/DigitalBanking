using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Identity.Api.Contracts;
using DigitalBanking.Identity.Api.Domain;
using DigitalBanking.Identity.Api.Repository;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DigitalBanking.Identity.Api.Application;

public sealed class AuthService(
    IIdentityRepository identityRepository,
    JwtTokenIssuer tokenIssuer,
    PasswordHasher<User> passwordHasher,
    IOptions<JwtOptions> jwtOptions,
    CustomerProfileClient customerProfileClient,
    RegionalDatabaseOptions regionalDatabases)
{
    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await identityRepository.EmailExistsAsync(email, cancellationToken))
        {
            throw new ConflictException("An account with this email already exists.");
        }

        // One person (CNIC), one customer.
        if (await customerProfileClient.NationalIdTakenAsync(request.NationalId, cancellationToken))
        {
            throw new ConflictException("A customer with this CNIC is already registered.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = request.FullName.Trim(),
            Role = UserRoles.Customer,
            Region = BankRegions.Normalize(request.Region),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        identityRepository.AddUser(user);
        var registration = new CustomerRegisteredEvent
        {
            UserId = user.Id,
            Email = user.Email,
            FullName = request.FullName.Trim(),
            NationalId = request.NationalId.Trim(),
            PhoneNumber = request.PhoneNumber.Trim(),
            Address = request.Address.Trim(),
            Region = user.Region
        };
        identityRepository.AddEvent(registration);

        await identityRepository.SaveChangesAsync(cancellationToken);
        var profileCreated = await customerProfileClient.SendAsync(registration, cancellationToken);
        var response = await IssueTokensAsync(user, cancellationToken);
        response.ProfilePending = !profileCreated;
        return response;
    }

    private async Task<bool> ProvinceReachableAsync(string region, CancellationToken cancellationToken)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(regionalDatabases.RegionConnectionString(region)) { ConnectTimeout = 3 };
            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return true;
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            return false;
        }
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await identityRepository.GetUserByEmailAsync(email, cancellationToken);
        if (user is null || passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            throw new UnauthorizedException("Email or password is incorrect.");
        }

        if (!user.IsActive)
        {
            throw new ForbiddenException("This account is disabled.");
        }

        // A customer's data lives in their province's database. If that province is down (master and slave),
        // the app would only show an empty dashboard, so sign-in waits until the province is back.
        if (user.Role == UserRoles.Customer && !await ProvinceReachableAsync(user.Region, cancellationToken))
        {
            throw new ServiceUnavailableException($"The bank's database for {BankRegions.DisplayName(user.Region)} is under maintenance right now. Your data is safe; please sign in again in a few minutes.");
        }

        user.LastLoginAtUtc = DateTime.UtcNow;
        identityRepository.AddEvent(new UserLoggedInEvent
        {
            UserId = user.Id,
            Email = user.Email
        });

        await identityRepository.SaveChangesAsync(cancellationToken);
        return await IssueTokensAsync(user, cancellationToken);
    }

    public async Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var tokenHash = JwtTokenIssuer.HashToken(request.RefreshToken);
        var storedToken = await identityRepository.GetRefreshTokenAsync(tokenHash, cancellationToken);
        if (storedToken is null || storedToken.RevokedAtUtc is not null || storedToken.ExpiresAtUtc < DateTime.UtcNow)
        {
            throw new UnauthorizedException("The refresh token is invalid or expired.");
        }

        storedToken.RevokedAtUtc = DateTime.UtcNow;
        return await IssueTokensAsync(storedToken.User, cancellationToken);
    }

    public async Task LogoutAsync(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var tokenHash = JwtTokenIssuer.HashToken(request.RefreshToken);
        var storedToken = await identityRepository.GetRefreshTokenAsync(tokenHash, cancellationToken);
        if (storedToken is null)
        {
            return;
        }

        storedToken.RevokedAtUtc = DateTime.UtcNow;
        await identityRepository.SaveChangesAsync(cancellationToken);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await identityRepository.GetUserByIdAsync(userId, cancellationToken) ?? throw new NotFoundException("User was not found.");

        if (passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            throw new UnauthorizedException("The current password is incorrect.");
        }

        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
        await identityRepository.RevokeActiveRefreshTokensAsync(userId, cancellationToken);
        identityRepository.AddEvent(new PasswordChangedEvent
        {
            UserId = user.Id,
            Email = user.Email
        });

        await identityRepository.SaveChangesAsync(cancellationToken);
    }

    public async Task<ForgotPasswordResponse> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await identityRepository.GetUserByEmailAsync(email, cancellationToken);
        if (user is null)
        {
            return new ForgotPasswordResponse
            {
                Message = "If the email exists, a reset code has been created."
            };
        }

        var resetCode = Random.Shared.Next(100000, 999999).ToString();
        identityRepository.AddPasswordResetToken(new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = JwtTokenIssuer.HashToken(resetCode),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
            CreatedAtUtc = DateTime.UtcNow
        });

        identityRepository.AddEvent(new PasswordResetRequestedEvent
        {
            UserId = user.Id,
            Email = user.Email,
            ResetCode = resetCode
        });

        await identityRepository.SaveChangesAsync(cancellationToken);
        return new ForgotPasswordResponse
        {
            Message = "A password reset code has been created.",
            ResetCode = resetCode
        };
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await identityRepository.GetUserByEmailAsync(email, cancellationToken)
            ?? throw new NotFoundException("User was not found.");

        var tokenHash = JwtTokenIssuer.HashToken(request.ResetCode.Trim());
        var resetToken = await identityRepository.GetUnusedResetTokenAsync(user.Id, tokenHash, cancellationToken);
        if (resetToken is null || resetToken.ExpiresAtUtc < DateTime.UtcNow)
        {
            throw new ValidationException("The reset code is invalid or expired.");
        }

        resetToken.UsedAtUtc = DateTime.UtcNow;
        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
        await identityRepository.SaveChangesAsync(cancellationToken);
    }

    private async Task<AuthResponse> IssueTokensAsync(User user, CancellationToken cancellationToken)
    {
        var refreshToken = tokenIssuer.CreateRefreshToken();
        identityRepository.AddRefreshToken(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = JwtTokenIssuer.HashToken(refreshToken),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(jwtOptions.Value.RefreshTokenDays),
            CreatedAtUtc = DateTime.UtcNow
        });

        await identityRepository.SaveChangesAsync(cancellationToken);

        return new AuthResponse
        {
            UserId = user.Id,
            Email = user.Email,
            FullName = user.FullName,
            Role = user.Role,
            AccessToken = tokenIssuer.CreateAccessToken(user.Id, user.Email, user.Role, user.FullName),
            RefreshToken = refreshToken
        };
    }
}
