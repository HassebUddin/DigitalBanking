using DigitalBanking.Identity.Api.Domain;

namespace DigitalBanking.Identity.Api.Repository;

public interface IEmployeeRepository
{
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);
    void AddUser(User user);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
