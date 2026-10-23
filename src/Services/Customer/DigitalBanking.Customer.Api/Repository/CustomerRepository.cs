using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Customer.Api.Domain;
using DigitalBanking.Customer.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Customer.Api.Repository;

// Each customer profile lives in their province's database.
public sealed class CustomerRepository(RegionalDb<CustomerDbContext> db) : ICustomerRepository
{
    public Task<CustomerProfile?> GetCustomerByUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return db.FindAsync(context => context.Customers.FirstOrDefaultAsync(customer => customer.UserId == userId, cancellationToken));
    }

    public Task<CustomerProfile?> GetCustomerByIdAsync(Guid customerId, CancellationToken cancellationToken)
    {
        return db.FindAsync(context => context.Customers.FirstOrDefaultAsync(customer => customer.Id == customerId, cancellationToken));
    }

    public async Task<IReadOnlyList<CustomerProfile>> GetCustomersAsync(CancellationToken cancellationToken)
    {
        var customers = await db.AllAsync(context => context.Customers.ToListAsync(cancellationToken));
        return customers.OrderByDescending(customer => customer.CreatedAtUtc).ToList();
    }

    public Task<bool> CustomerExistsByUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return db.AnyAsync(context => context.Customers.AnyAsync(customer => customer.UserId == userId, cancellationToken));
    }

    // CNIC must be unique across the whole bank, not just one province.
    public Task<bool> CustomerExistsByNationalIdAsync(string nationalId, CancellationToken cancellationToken)
    {
        return db.AnyAsync(context => context.Customers.AnyAsync(customer => customer.NationalId == nationalId, cancellationToken));
    }

    public async Task AddCustomerAsync(CustomerProfile customer, CancellationToken cancellationToken)
    {
        // Registration passes the province; otherwise use the one the user signed up with.
        if (string.IsNullOrWhiteSpace(customer.Region))
        {
            await db.ForUserAsync(customer.UserId, cancellationToken);
        }
        else
        {
            db.Pin(customer.Region);
        }

        customer.Region = db.CurrentRegion;
        db.Current.Customers.Add(customer);
    }

    public void RemoveCustomer(CustomerProfile customer)
    {
        db.Current.Customers.Remove(customer);
    }

    public Task SaveCustomerChangesAsync(CancellationToken cancellationToken)
    {
        return db.SaveChangesAsync(cancellationToken);
    }
}
