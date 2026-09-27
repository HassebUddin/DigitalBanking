using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.Customer.Api.Contracts;
using DigitalBanking.Customer.Api.Domain;
using DigitalBanking.Customer.Api.Repository;

namespace DigitalBanking.Customer.Api.Application;

public sealed class CustomerService(ICustomerRepository customerRepository)
{
    public async Task<CustomerResponse> GetOrCreateCustomerProfileAsync(Guid userId, string email, string fullName, CancellationToken cancellationToken)
    {
        var customer = await customerRepository.GetCustomerByUserIdAsync(userId, cancellationToken);
        if (customer is not null)
            return new CustomerResponse { Id = customer.Id, UserId = customer.UserId, FullName = customer.FullName, Email = customer.Email, NationalId = customer.NationalId, PhoneNumber = customer.PhoneNumber, Address = customer.Address, KycStatus = customer.KycStatus, CreatedAtUtc = customer.CreatedAtUtc };

        customer = await CreateCustomerProfileAsync(userId, email, string.IsNullOrWhiteSpace(fullName) ? email : fullName, await UniqueNationalIdAsync(userId, cancellationToken), string.Empty, string.Empty, cancellationToken);
        return new CustomerResponse { Id = customer.Id, UserId = customer.UserId, FullName = customer.FullName, Email = customer.Email, NationalId = customer.NationalId, PhoneNumber = customer.PhoneNumber, Address = customer.Address, KycStatus = customer.KycStatus, CreatedAtUtc = customer.CreatedAtUtc };
    }

    public async Task<CustomerResponse> GetCustomerByIdAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await customerRepository.GetCustomerByIdAsync(customerId, cancellationToken) ?? throw new NotFoundException("Customer profile was not found.");
        return new CustomerResponse { Id = customer.Id, UserId = customer.UserId, FullName = customer.FullName, Email = customer.Email, NationalId = customer.NationalId, PhoneNumber = customer.PhoneNumber, Address = customer.Address, KycStatus = customer.KycStatus, CreatedAtUtc = customer.CreatedAtUtc };
    }

    public async Task<IReadOnlyList<CustomerResponse>> GetCustomersAsync(CancellationToken cancellationToken)
    {
        var customers = await customerRepository.GetCustomersAsync(cancellationToken);
        return customers.Select(customer => new CustomerResponse { Id = customer.Id, UserId = customer.UserId, FullName = customer.FullName, Email = customer.Email, NationalId = customer.NationalId, PhoneNumber = customer.PhoneNumber, Address = customer.Address, KycStatus = customer.KycStatus, CreatedAtUtc = customer.CreatedAtUtc }).ToList();
    }

    public async Task<CustomerResponse> UpdateCustomerAsync(Guid userId, string email, UpdateCustomerRequest request, CancellationToken cancellationToken)
    {
        var customer = await customerRepository.GetCustomerByUserIdAsync(userId, cancellationToken);
        if (customer is null)
        {
            customer = await CreateCustomerProfileAsync(userId, email, request.FullName.Trim(), await UniqueNationalIdAsync(userId, cancellationToken), request.PhoneNumber.Trim(), request.Address.Trim(), cancellationToken);
            return new CustomerResponse { Id = customer.Id, UserId = customer.UserId, FullName = customer.FullName, Email = customer.Email, NationalId = customer.NationalId, PhoneNumber = customer.PhoneNumber, Address = customer.Address, KycStatus = customer.KycStatus, CreatedAtUtc = customer.CreatedAtUtc };
        }

        customer.FullName = request.FullName.Trim();
        customer.PhoneNumber = request.PhoneNumber.Trim();
        customer.Address = request.Address.Trim();
        customer.UpdatedAtUtc = DateTime.UtcNow;
        await customerRepository.SaveCustomerChangesAsync(cancellationToken);
        return new CustomerResponse { Id = customer.Id, UserId = customer.UserId, FullName = customer.FullName, Email = customer.Email, NationalId = customer.NationalId, PhoneNumber = customer.PhoneNumber, Address = customer.Address, KycStatus = customer.KycStatus, CreatedAtUtc = customer.CreatedAtUtc };
    }

    public async Task<CustomerResponse> UpdateKycStatusAsync(Guid customerId, string status, CancellationToken cancellationToken)
    {
        var customer = await customerRepository.GetCustomerByIdAsync(customerId, cancellationToken) ?? throw new NotFoundException("Customer profile was not found.");
        customer.KycStatus = status;
        customer.UpdatedAtUtc = DateTime.UtcNow;
        await customerRepository.SaveCustomerChangesAsync(cancellationToken);
        return new CustomerResponse { Id = customer.Id, UserId = customer.UserId, FullName = customer.FullName, Email = customer.Email, NationalId = customer.NationalId, PhoneNumber = customer.PhoneNumber, Address = customer.Address, KycStatus = customer.KycStatus, CreatedAtUtc = customer.CreatedAtUtc };
    }

    public async Task DeleteCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await customerRepository.GetCustomerByIdAsync(customerId, cancellationToken) ?? throw new NotFoundException("Customer profile was not found.");
        customerRepository.RemoveCustomer(customer);
        await customerRepository.SaveCustomerChangesAsync(cancellationToken);
    }

    public async Task CreateCustomerFromRegistrationAsync(Guid userId, string email, string fullName, string nationalId, string phoneNumber, string address, CancellationToken cancellationToken)
    {
        if (await customerRepository.CustomerExistsByUserIdAsync(userId, cancellationToken))
        {
            return;
        }

        var uniqueNationalId = nationalId.Trim();
        var nationalIdTaken = !string.IsNullOrWhiteSpace(uniqueNationalId) && await customerRepository.CustomerExistsByNationalIdAsync(uniqueNationalId, cancellationToken);
        if (string.IsNullOrWhiteSpace(uniqueNationalId) || nationalIdTaken)
            uniqueNationalId = await UniqueNationalIdAsync(userId, cancellationToken);

        await CreateCustomerProfileAsync(userId, email, fullName, uniqueNationalId, phoneNumber, address, cancellationToken);
    }

    private async Task<CustomerProfile> CreateCustomerProfileAsync(Guid userId, string email, string fullName, string nationalId, string phoneNumber, string address, CancellationToken cancellationToken)
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

        customerRepository.AddCustomer(customer);
        await customerRepository.SaveCustomerChangesAsync(cancellationToken);
        return customer;
    }

    private async Task<string> UniqueNationalIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        var candidate = $"TMP-{userId:N}"[..20];
        var taken = await customerRepository.CustomerExistsByNationalIdAsync(candidate, cancellationToken);
        return taken ? $"TMP-{Guid.NewGuid():N}"[..20] : candidate;
    }
}
