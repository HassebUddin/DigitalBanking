using DigitalBanking.Customer.Api.Domain;
using DigitalBanking.Customer.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Customer.Api.Repository;

public sealed class CustomerRepository(CustomerDbContext dbContext) : ICustomerRepository
{
    public Task<CustomerProfile?> GetCustomerByUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return dbContext.Customers.FirstOrDefaultAsync(customer => customer.UserId == userId, cancellationToken);
    }

    public Task<CustomerProfile?> GetCustomerByIdAsync(Guid customerId, CancellationToken cancellationToken)
    {
        return dbContext.Customers.FirstOrDefaultAsync(customer => customer.Id == customerId, cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerProfile>> GetCustomersAsync(CancellationToken cancellationToken)
    {
        return await dbContext.Customers.OrderByDescending(customer => customer.CreatedAtUtc).ToListAsync(cancellationToken);
    }

    public Task<bool> CustomerExistsByUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return dbContext.Customers.AnyAsync(customer => customer.UserId == userId, cancellationToken);
    }

    public Task<bool> CustomerExistsByNationalIdAsync(string nationalId, CancellationToken cancellationToken)
    {
        return dbContext.Customers.AnyAsync(customer => customer.NationalId == nationalId, cancellationToken);
    }

    public void AddCustomer(CustomerProfile customer)
    {
        dbContext.Customers.Add(customer);
    }

    public void RemoveCustomer(CustomerProfile customer)
    {
        dbContext.Customers.Remove(customer);
    }

    public Task SaveCustomerChangesAsync(CancellationToken cancellationToken)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }
}
