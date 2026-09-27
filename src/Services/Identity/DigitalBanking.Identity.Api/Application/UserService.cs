using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.Contracts;
using DigitalBanking.Identity.Api.Contracts;
using DigitalBanking.Identity.Api.Repository;

namespace DigitalBanking.Identity.Api.Application;

public sealed class UserService(IUserRepository userRepository)
{
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
