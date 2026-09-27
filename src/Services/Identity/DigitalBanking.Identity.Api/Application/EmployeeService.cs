using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.Identity.Api.Contracts;
using DigitalBanking.Identity.Api.Domain;
using DigitalBanking.Identity.Api.Repository;
using Microsoft.AspNetCore.Identity;

namespace DigitalBanking.Identity.Api.Application;

public sealed class EmployeeService(IEmployeeRepository employeeRepository, PasswordHasher<User> passwordHasher)
{
    public async Task<DirectoryUserResponse> CreateEmployeeAsync(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await employeeRepository.EmailExistsAsync(email, cancellationToken))
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
        employeeRepository.AddUser(user);
        await employeeRepository.SaveChangesAsync(cancellationToken);
        return new DirectoryUserResponse
        {
            UserId = user.Id,
            Email = user.Email,
            FullName = string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName,
            Role = user.Role
        };
    }
}
