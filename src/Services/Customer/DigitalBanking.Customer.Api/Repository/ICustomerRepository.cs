using DigitalBanking.Customer.Api.Domain;

namespace DigitalBanking.Customer.Api.Repository;

public interface ICustomerRepository
{
    Task<CustomerProfile?> GetCustomerByUserIdAsync(Guid userId, CancellationToken cancellationToken);
    Task<CustomerProfile?> GetCustomerByIdAsync(Guid customerId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerProfile>> GetCustomersAsync(CancellationToken cancellationToken);
    Task<bool> CustomerExistsByUserIdAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> CustomerExistsByNationalIdAsync(string nationalId, CancellationToken cancellationToken);
    Task AddCustomerAsync(CustomerProfile customer, CancellationToken cancellationToken);
    void RemoveCustomer(CustomerProfile customer);
    Task SaveCustomerChangesAsync(CancellationToken cancellationToken);
}
