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
        return db.FindForUserAsync(userId, context => context.Customers.FirstOrDefaultAsync(customer => customer.UserId == userId, cancellationToken), cancellationToken);
    }

    // Only the given province: a customer's profile lives in the province they registered in,
    // so another province being down does not matter here.
    public Task<CustomerProfile?> GetCustomerByUserIdInRegionAsync(Guid userId, string region, CancellationToken cancellationToken)
    {
        return db.FindInAsync(BankRegions.Normalize(region), context => context.Customers.FirstOrDefaultAsync(customer => customer.UserId == userId, cancellationToken));
    }

    public Task<CustomerProfile?> GetCustomerByIdAsync(Guid customerId, CancellationToken cancellationToken)
    {
        return db.FindAsync(context => context.Customers.FirstOrDefaultAsync(customer => customer.Id == customerId, cancellationToken));
    }

    public async Task<IReadOnlyList<CustomerProfile>> GetCustomersAsync(CancellationToken cancellationToken)
    {
        // Customers who registered in the app (names on account requests, staff people list, overview). Bulk-loaded
        // customers (millions possible) are left out here; the paged search-customers list includes everyone.
        const int newest = 2000;
        var customers = await db.AllAsync(context => context.Customers
            .Where(customer => !customer.Email.EndsWith("@bulk.digitalbank.local"))
            .OrderByDescending(customer => customer.CreatedAtUtc).Take(newest).ToListAsync(cancellationToken));
        return customers.OrderByDescending(customer => customer.CreatedAtUtc).Take(newest).ToList();
    }

    // Picks the search the indexes can answer fastest: CNIC, phone and email are prefix seeks on their index;
    // anything else is a name search (a scan; full-text search such as Elasticsearch is planned for that).
    public static (string Kind, string Text, string Strategy) PlanSearch(string? search)
    {
        var text = search?.Trim() ?? string.Empty;
        if (text.Length == 0) return ("none", text, "Newest customers (index on created date)");
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d{5}(-\d{0,7}(-\d?)?|\d{0,8})$"))
        {
            // 4210112345671 -> 42101-1234567-1, the stored format.
            var digits = text.Replace("-", string.Empty);
            var formatted = digits.Length <= 5 ? digits : digits.Length <= 12 ? $"{digits[..5]}-{digits[5..]}" : $"{digits[..5]}-{digits[5..12]}-{digits[12..]}";
            return ("cnic", formatted, "Index seek on CNIC");
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^03\d{0,2}-?\d{0,7}$"))
        {
            // 03001234567 -> 0300-1234567, the stored format.
            var digits = text.Replace("-", string.Empty);
            return ("phone", digits.Length <= 4 ? digits : $"{digits[..4]}-{digits[4..]}", "Index seek on phone");
        }

        return text.Contains('@')
            ? ("email", text, "Index seek on email")
            : ("name", text, "Name search (scan)");
    }

    public async Task<(List<CustomerProfile> Items, long Total, string Strategy)> SearchCustomersAsync(string? search, string? kyc, int page, int pageSize, CancellationToken cancellationToken)
    {
        var (kind, text, strategy) = PlanSearch(search);
        var verified = string.Equals(kyc, "verified", StringComparison.OrdinalIgnoreCase);
        var pending = string.Equals(kyc, "pending", StringComparison.OrdinalIgnoreCase);
        var (items, total) = await db.PageAsync(context =>
        {
            var query = context.Customers.AsNoTracking();
            query = kind switch
            {
                "cnic" => query.Where(customer => customer.NationalId.StartsWith(text)),
                "phone" => query.Where(customer => customer.PhoneNumber.StartsWith(text)),
                "email" => query.Where(customer => customer.Email.StartsWith(text)),
                "name" => query.Where(customer => customer.FullName.Contains(text)),
                _ => query
            };

            if (verified)
            {
                query = query.Where(customer => customer.KycStatus == "Verified");
            }
            else if (pending)
            {
                query = query.Where(customer => customer.KycStatus != "Verified");
            }

            return query.OrderByDescending(customer => customer.CreatedAtUtc);
        }, customer => customer.CreatedAtUtc, page, pageSize, cancellationToken);
        return (items, total, strategy);
    }

    public Task<bool> CustomerExistsByUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return db.AnyAsync(context => context.Customers.AnyAsync(customer => customer.UserId == userId, cancellationToken));
    }

    // CNIC must be unique across the whole bank, not just one province.
    // Compared on digits only: "42101-1234567-1" and "4210112345671" are the same CNIC.
    public Task<bool> CustomerExistsByNationalIdAsync(string nationalId, CancellationToken cancellationToken)
    {
        var digits = new string(nationalId.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            return db.AnyAsync(context => context.Customers.AnyAsync(customer => customer.NationalId == nationalId, cancellationToken));
        }

        return db.AnyAsync(context => context.Customers.AnyAsync(customer => customer.NationalId.Replace("-", "") == digits, cancellationToken));
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
