using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.Customer.Api.Contracts;
using DigitalBanking.Customer.Api.Domain;
using DigitalBanking.Customer.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Customer.Api.Application;

public sealed class CustomerService(CustomerDbContext dbContext)
{
    public async Task<CustomerResponse> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        var customer = await dbContext.Customers.FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken)
            ?? throw new NotFoundException("Customer profile was not found.");
        return Map(customer);
    }

    public async Task<CustomerResponse> GetOrCreateMineAsync(Guid userId, string email, string fullName, CancellationToken cancellationToken)
    {
        var customer = await dbContext.Customers.FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (customer is not null)
        {
            return Map(customer);
        }

        customer = await CreateProfileAsync(
            userId,
            email,
            string.IsNullOrWhiteSpace(fullName) ? email : fullName,
            await UniqueNationalIdAsync(userId, cancellationToken),
            string.Empty,
            string.Empty,
            cancellationToken);
        return Map(customer);
    }

    public async Task<CustomerResponse> GetByIdAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await dbContext.Customers.FirstOrDefaultAsync(item => item.Id == customerId, cancellationToken)
            ?? throw new NotFoundException("Customer profile was not found.");
        return Map(customer);
    }

    public async Task<IReadOnlyList<CustomerResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var customers = await dbContext.Customers
            .OrderByDescending(customer => customer.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        return customers.Select(Map).ToList();
    }

    public async Task<CustomerResponse> UpdateAsync(Guid userId, string email, UpdateCustomerRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            throw new ValidationException("Full name is required.");
        }

        var customer = await dbContext.Customers.FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (customer is null)
        {
            customer = await CreateProfileAsync(
                userId,
                email,
                request.FullName.Trim(),
                await UniqueNationalIdAsync(userId, cancellationToken),
                request.PhoneNumber.Trim(),
                request.Address.Trim(),
                cancellationToken);
            return Map(customer);
        }

        customer.FullName = request.FullName.Trim();
        customer.PhoneNumber = request.PhoneNumber.Trim();
        customer.Address = request.Address.Trim();
        customer.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(customer);
    }

    public async Task DeleteAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await dbContext.Customers.FirstOrDefaultAsync(item => item.Id == customerId, cancellationToken)
            ?? throw new NotFoundException("Customer profile was not found.");
        dbContext.Customers.Remove(customer);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task CreateFromRegistrationAsync(Guid userId, string email, string fullName, string nationalId, string phoneNumber, string address, CancellationToken cancellationToken)
    {
        var exists = await dbContext.Customers.AnyAsync(customer => customer.UserId == userId, cancellationToken);
        if (exists)
        {
            return;
        }

        var uniqueNationalId = nationalId.Trim();
        var nationalIdTaken = !string.IsNullOrWhiteSpace(uniqueNationalId)
            && await dbContext.Customers.AnyAsync(customer => customer.NationalId == uniqueNationalId, cancellationToken);
        if (string.IsNullOrWhiteSpace(uniqueNationalId) || nationalIdTaken)
        {
            uniqueNationalId = await UniqueNationalIdAsync(userId, cancellationToken);
        }

        await CreateProfileAsync(userId, email, fullName, uniqueNationalId, phoneNumber, address, cancellationToken);
    }

    private async Task<CustomerProfile> CreateProfileAsync(
        Guid userId,
        string email,
        string fullName,
        string nationalId,
        string phoneNumber,
        string address,
        CancellationToken cancellationToken)
    {
        var customer = new CustomerProfile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = string.IsNullOrWhiteSpace(email) ? $"{userId:N}@pending.digitalbank" : email.Trim(),
            FullName = string.IsNullOrWhiteSpace(fullName) ? "Customer" : fullName.Trim(),
            NationalId = nationalId,
            PhoneNumber = phoneNumber.Trim(),
            Address = address.Trim(),
            KycStatus = "Pending",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync(cancellationToken);
        return customer;
    }

    private async Task<string> UniqueNationalIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        var candidate = $"TMP-{userId:N}"[..20];
        var taken = await dbContext.Customers.AnyAsync(customer => customer.NationalId == candidate, cancellationToken);
        return taken ? $"TMP-{Guid.NewGuid():N}"[..20] : candidate;
    }

    private static CustomerResponse Map(CustomerProfile customer)
    {
        return new CustomerResponse
        {
            Id = customer.Id,
            UserId = customer.UserId,
            FullName = customer.FullName,
            Email = customer.Email,
            NationalId = customer.NationalId,
            PhoneNumber = customer.PhoneNumber,
            Address = customer.Address,
            KycStatus = customer.KycStatus,
            CreatedAtUtc = customer.CreatedAtUtc
        };
    }
}
