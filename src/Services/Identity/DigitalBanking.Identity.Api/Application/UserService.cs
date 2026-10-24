using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.Contracts;
using DigitalBanking.Identity.Api.Contracts;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Identity.Api.Domain;
using DigitalBanking.Identity.Api.Repository;
using Microsoft.AspNetCore.Identity;

namespace DigitalBanking.Identity.Api.Application;

public sealed class UserService(IUserRepository userRepository, PasswordHasher<User> passwordHasher)
{
    // Bulk-loaded customers get the same demo password as the seeded demo customers.
    public const string DemoCustomerPassword = "Customer@12345";
    private const string BulkEmailDomain = "@bulk.digitalbank.local";

    // Creates the identity user of a bulk-loaded customer on demand (millions are loaded, few ever sign in),
    // with the profile's user id and province so the services find their data.
    public async Task<DemoLoginResponse> EnableDemoLoginAsync(EnableDemoLoginRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (!email.EndsWith(BulkEmailDomain, StringComparison.Ordinal) || request.UserId == Guid.Empty)
        {
            throw new ValidationException("Only bulk-loaded demo customers can be given a login here.");
        }

        var existing = await userRepository.GetUserByEmailAsync(email, cancellationToken);
        if (existing is not null)
        {
            return new DemoLoginResponse { Email = existing.Email, Password = DemoCustomerPassword };
        }

        var user = new User
        {
            Id = request.UserId,
            Email = email,
            FullName = request.FullName.Trim(),
            Role = UserRoles.Customer,
            Region = BankRegions.Normalize(request.Region),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        user.PasswordHash = passwordHasher.HashPassword(user, DemoCustomerPassword);
        userRepository.AddUser(user);
        await userRepository.SaveChangesAsync(cancellationToken);
        return new DemoLoginResponse { Email = email, Password = DemoCustomerPassword, Created = true };
    }

    public async Task<CurrentUserResponse> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetUserByIdAsync(userId, cancellationToken)
            ?? throw new NotFoundException("User was not found.");

        return new CurrentUserResponse
        {
            UserId = user.Id,
            Email = user.Email,
            FullName = user.FullName,
            Role = user.Role
        };
    }

    public async Task<IReadOnlyList<DirectoryUserResponse>> GetActiveUsersExceptAsync(Guid currentUserId, string currentRole, CancellationToken cancellationToken)
    {
        var users = (await userRepository.GetActiveUsersExceptAsync(currentUserId, cancellationToken)).ToList();
        if (currentRole == UserRoles.Customer)
        {
            users = users.Where(user => user.Role is UserRoles.InternalEmployee or UserRoles.Admin).ToList();
        }

        return users.Select(user => new DirectoryUserResponse
        {
            UserId = user.Id,
            Email = user.Email,
            FullName = string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName,
            Role = user.Role
        }).ToList();
    }

    public async Task<DirectoryUserResponse> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetUserByIdAsync(userId, cancellationToken)
            ?? throw new NotFoundException("User was not found.");
        return new DirectoryUserResponse
        {
            UserId = user.Id,
            Email = user.Email,
            FullName = string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName,
            Role = user.Role
        };
    }
}
