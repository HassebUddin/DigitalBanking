using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Outbox;
using DigitalBanking.Contracts;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Identity.Api.Contracts;
using DigitalBanking.Identity.Api.Domain;
using DigitalBanking.Identity.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DigitalBanking.Identity.Api.Application;

public sealed class AuthService(
    IdentityDbContext dbContext,
    JwtTokenIssuer tokenIssuer,
    PasswordHasher<User> passwordHasher,
    IOptions<JwtOptions> jwtOptions)
{
    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        ValidatePassword(request.Password);
        ValidateRequired(request.Email, "Email");
        ValidateRequired(request.FullName, "Full name");
        ValidateRequired(request.NationalId, "National ID");

        var email = request.Email.Trim().ToLowerInvariant();
        var emailTaken = await dbContext.Users.AnyAsync(user => user.Email == email, cancellationToken);
        if (emailTaken)
        {
            throw new ConflictException("An account with this email already exists.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = request.FullName.Trim(),
            Role = UserRoles.Customer,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        dbContext.Users.Add(user);
        OutboxWriter.AddEvent(dbContext, new CustomerRegisteredEvent
        {
            UserId = user.Id,
            Email = user.Email,
            FullName = request.FullName.Trim(),
            NationalId = request.NationalId.Trim(),
            PhoneNumber = request.PhoneNumber.Trim(),
            Address = request.Address.Trim()
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return await IssueTokensAsync(user, cancellationToken);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await dbContext.Users.FirstOrDefaultAsync(item => item.Email == email, cancellationToken);
        if (user is null || passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            throw new UnauthorizedException("Email or password is incorrect.");
        }

        if (!user.IsActive)
        {
            throw new ForbiddenException("This account is disabled.");
        }

        user.LastLoginAtUtc = DateTime.UtcNow;
        OutboxWriter.AddEvent(dbContext, new UserLoggedInEvent
        {
            UserId = user.Id,
            Email = user.Email
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return await IssueTokensAsync(user, cancellationToken);
    }

    public async Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var tokenHash = JwtTokenIssuer.HashToken(request.RefreshToken);
        var storedToken = await dbContext.RefreshTokens
            .Include(token => token.User)
            .FirstOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

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
        var storedToken = await dbContext.RefreshTokens.FirstOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);
        if (storedToken is null)
        {
            return;
        }

        storedToken.RevokedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        ValidatePassword(request.NewPassword);
        var user = await dbContext.Users.FirstOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new NotFoundException("User was not found.");

        if (passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            throw new UnauthorizedException("The current password is incorrect.");
        }

        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
        foreach (var refreshToken in await dbContext.RefreshTokens.Where(token => token.UserId == userId && token.RevokedAtUtc == null).ToListAsync(cancellationToken))
        {
            refreshToken.RevokedAtUtc = DateTime.UtcNow;
        }

        OutboxWriter.AddEvent(dbContext, new PasswordChangedEvent
        {
            UserId = user.Id,
            Email = user.Email
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ForgotPasswordResponse> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await dbContext.Users.FirstOrDefaultAsync(item => item.Email == email, cancellationToken);
        if (user is null)
        {
            return new ForgotPasswordResponse
            {
                Message = "If the email exists, a reset code has been created."
            };
        }

        var resetCode = Random.Shared.Next(100000, 999999).ToString();
        dbContext.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = JwtTokenIssuer.HashToken(resetCode),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
            CreatedAtUtc = DateTime.UtcNow
        });

        OutboxWriter.AddEvent(dbContext, new PasswordResetRequestedEvent
        {
            UserId = user.Id,
            Email = user.Email,
            ResetCode = resetCode
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return new ForgotPasswordResponse
        {
            Message = "A password reset code has been created.",
            ResetCode = resetCode
        };
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        ValidatePassword(request.NewPassword);
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await dbContext.Users.FirstOrDefaultAsync(item => item.Email == email, cancellationToken)
            ?? throw new NotFoundException("User was not found.");

        var tokenHash = JwtTokenIssuer.HashToken(request.ResetCode.Trim());
        var resetToken = await dbContext.PasswordResetTokens
            .Where(token => token.UserId == user.Id && token.TokenHash == tokenHash && token.UsedAtUtc == null)
            .OrderByDescending(token => token.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (resetToken is null || resetToken.ExpiresAtUtc < DateTime.UtcNow)
        {
            throw new ValidationException("The reset code is invalid or expired.");
        }

        resetToken.UsedAtUtc = DateTime.UtcNow;
        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<CurrentUserResponse> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new NotFoundException("User was not found.");

        return new CurrentUserResponse
        {
            UserId = user.Id,
            Email = user.Email,
            FullName = user.FullName,
            Role = user.Role
        };
    }

    public async Task<DirectoryUserResponse> CreateEmployeeAsync(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        ValidatePassword(request.Password);
        ValidateRequired(request.Email, "Email");
        ValidateRequired(request.FullName, "Full name");

        if (request.Role is not (UserRoles.InternalEmployee or UserRoles.ExternalEmployee))
        {
            throw new ValidationException("Employee role must be InternalEmployee or ExternalEmployee.");
        }

        var email = request.Email.Trim().ToLowerInvariant();
        var emailTaken = await dbContext.Users.AnyAsync(user => user.Email == email, cancellationToken);
        if (emailTaken)
        {
            throw new ConflictException("An account with this email already exists.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = request.FullName.Trim(),
            Role = request.Role,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapDirectoryUser(user);
    }

    public async Task<IReadOnlyList<DirectoryUserResponse>> GetDirectoryAsync(Guid currentUserId, string currentRole, CancellationToken cancellationToken)
    {
        var users = await dbContext.Users
            .Where(user => user.Id != currentUserId && user.IsActive)
            .OrderBy(user => user.FullName)
            .ThenBy(user => user.Email)
            .ToListAsync(cancellationToken);

        if (currentRole == UserRoles.Customer)
        {
            users = users.Where(user => user.Role is UserRoles.InternalEmployee or UserRoles.Admin).ToList();
        }

        return users.Select(MapDirectoryUser).ToList();
    }

    public async Task<DirectoryUserResponse> GetDirectoryUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new NotFoundException("User was not found.");
        return MapDirectoryUser(user);
    }

    private async Task<AuthResponse> IssueTokensAsync(User user, CancellationToken cancellationToken)
    {
        var refreshToken = tokenIssuer.CreateRefreshToken();
        dbContext.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = JwtTokenIssuer.HashToken(refreshToken),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(jwtOptions.Value.RefreshTokenDays),
            CreatedAtUtc = DateTime.UtcNow
        });

        await dbContext.SaveChangesAsync(cancellationToken);

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

    private static DirectoryUserResponse MapDirectoryUser(User user)
    {
        return new DirectoryUserResponse
        {
            UserId = user.Id,
            Email = user.Email,
            FullName = string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName,
            Role = user.Role
        };
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new ValidationException("Password must be at least 8 characters.");
        }
    }

    private static void ValidateRequired(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ValidationException($"{fieldName} is required.");
        }
    }
}
