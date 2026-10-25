using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Web;
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
            return new CustomerResponse { Id = customer.Id, UserId = customer.UserId, FullName = customer.FullName, Email = customer.Email, NationalId = customer.NationalId, PhoneNumber = customer.PhoneNumber, Address = customer.Address, KycStatus = customer.KycStatus, Region = customer.Region, CreatedAtUtc = customer.CreatedAtUtc };

        customer = await CreateCustomerProfileAsync(userId, email, string.IsNullOrWhiteSpace(fullName) ? email : fullName, await UniqueNationalIdAsync(userId, cancellationToken), string.Empty, string.Empty, cancellationToken);
        return new CustomerResponse { Id = customer.Id, UserId = customer.UserId, FullName = customer.FullName, Email = customer.Email, NationalId = customer.NationalId, PhoneNumber = customer.PhoneNumber, Address = customer.Address, KycStatus = customer.KycStatus, Region = customer.Region, CreatedAtUtc = customer.CreatedAtUtc };
    }

    public async Task<CustomerResponse> GetCustomerByIdAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await customerRepository.GetCustomerByIdAsync(customerId, cancellationToken) ?? throw new NotFoundException("Customer profile was not found.");
        return new CustomerResponse { Id = customer.Id, UserId = customer.UserId, FullName = customer.FullName, Email = customer.Email, NationalId = customer.NationalId, PhoneNumber = customer.PhoneNumber, Address = customer.Address, KycStatus = customer.KycStatus, Region = customer.Region, CreatedAtUtc = customer.CreatedAtUtc };
    }

    public async Task<PagedResult<CustomerResponse>> SearchCustomersAsync(string? search, string? kyc, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = PagedResult<CustomerResponse>.Normalize(page, pageSize);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (customers, total, strategy) = await customerRepository.SearchCustomersAsync(search, kyc, page, pageSize, cancellationToken);
        var items = customers.Select(customer => new CustomerResponse { Id = customer.Id, UserId = customer.UserId, FullName = customer.FullName, Email = customer.Email, NationalId = customer.NationalId, PhoneNumber = customer.PhoneNumber, Address = customer.Address, KycStatus = customer.KycStatus, Region = customer.Region, CreatedAtUtc = customer.CreatedAtUtc }).ToList();
        return new PagedResult<CustomerResponse>(items, total, page, pageSize) { Strategy = strategy, TookMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1) };
    }

    public async Task<IReadOnlyList<CustomerResponse>> GetCustomersAsync(CancellationToken cancellationToken)
    {
        var customers = await customerRepository.GetCustomersAsync(cancellationToken);
        return customers.Select(customer => new CustomerResponse { Id = customer.Id, UserId = customer.UserId, FullName = customer.FullName, Email = customer.Email, NationalId = customer.NationalId, PhoneNumber = customer.PhoneNumber, Address = customer.Address, KycStatus = customer.KycStatus, Region = customer.Region, CreatedAtUtc = customer.CreatedAtUtc }).ToList();
    }

    public async Task<CustomerResponse> UpdateCustomerAsync(Guid userId, string email, UpdateCustomerRequest request, CancellationToken cancellationToken)
    {
        var customer = await customerRepository.GetCustomerByUserIdAsync(userId, cancellationToken);
        if (customer is null)
        {
            customer = await CreateCustomerProfileAsync(userId, email, request.FullName.Trim(), await UniqueNationalIdAsync(userId, cancellationToken), request.PhoneNumber.Trim(), request.Address.Trim(), cancellationToken);
            return new CustomerResponse { Id = customer.Id, UserId = customer.UserId, FullName = customer.FullName, Email = customer.Email, NationalId = customer.NationalId, PhoneNumber = customer.PhoneNumber, Address = customer.Address, KycStatus = customer.KycStatus, Region = customer.Region, CreatedAtUtc = customer.CreatedAtUtc };
        }

        customer.FullName = request.FullName.Trim();
        customer.PhoneNumber = request.PhoneNumber.Trim();
        customer.Address = request.Address.Trim();
        customer.UpdatedAtUtc = DateTime.UtcNow;
        await customerRepository.SaveCustomerChangesAsync(cancellationToken);
        return new CustomerResponse { Id = customer.Id, UserId = customer.UserId, FullName = customer.FullName, Email = customer.Email, NationalId = customer.NationalId, PhoneNumber = customer.PhoneNumber, Address = customer.Address, KycStatus = customer.KycStatus, Region = customer.Region, CreatedAtUtc = customer.CreatedAtUtc };
    }

    public async Task<CustomerResponse> UpdateKycStatusAsync(Guid customerId, string status, CancellationToken cancellationToken)
    {
        var customer = await customerRepository.GetCustomerByIdAsync(customerId, cancellationToken) ?? throw new NotFoundException("Customer profile was not found.");
        // KYC is an identity check: without a real CNIC on file (auto-created profiles get a TMP- placeholder) there is nothing to verify.
        if (status == "Verified" && (string.IsNullOrWhiteSpace(customer.NationalId) || customer.NationalId.StartsWith("TMP-", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ValidationException("CNIC is missing, so KYC cannot be verified. Add the customer's CNIC first.");
        }

        customer.KycStatus = status;
        customer.UpdatedAtUtc = DateTime.UtcNow;
        await customerRepository.SaveCustomerChangesAsync(cancellationToken);
        return new CustomerResponse { Id = customer.Id, UserId = customer.UserId, FullName = customer.FullName, Email = customer.Email, NationalId = customer.NationalId, PhoneNumber = customer.PhoneNumber, Address = customer.Address, KycStatus = customer.KycStatus, Region = customer.Region, CreatedAtUtc = customer.CreatedAtUtc };
    }

    public async Task DeleteCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await customerRepository.GetCustomerByIdAsync(customerId, cancellationToken) ?? throw new NotFoundException("Customer profile was not found.");
        customerRepository.RemoveCustomer(customer);
        await customerRepository.SaveCustomerChangesAsync(cancellationToken);
    }

    public Task<bool> NationalIdTakenAsync(string nationalId, CancellationToken cancellationToken)
    {
        var cnic = nationalId.Trim();
        return cnic.Length == 0 ? Task.FromResult(false) : customerRepository.CustomerExistsByNationalIdAsync(cnic, cancellationToken);
    }

    public async Task CreateCustomerFromRegistrationAsync(Guid userId, string email, string fullName, string nationalId, string phoneNumber, string address, string region, CancellationToken cancellationToken)
    {
        // The app may already have created a placeholder profile (the customer signed in before this event
        // arrived): fill in what they gave at registration instead of dropping it.
        // Registration names the province, so only that province is looked at (another one being down is irrelevant).
        var placeholder = string.IsNullOrWhiteSpace(region)
            ? await customerRepository.GetCustomerByUserIdAsync(userId, cancellationToken)
            : await customerRepository.GetCustomerByUserIdInRegionAsync(userId, region, cancellationToken);
        if (placeholder is { } existing)
        {
            var cnic = nationalId.Trim();
            if (existing.NationalId.StartsWith("TMP-", StringComparison.Ordinal) && cnic.Length > 0
                && !await customerRepository.CustomerExistsByNationalIdAsync(cnic, cancellationToken))
            {
                existing.NationalId = cnic;
            }

            if (string.IsNullOrWhiteSpace(existing.PhoneNumber)) existing.PhoneNumber = phoneNumber.Trim();
            if (string.IsNullOrWhiteSpace(existing.Address)) existing.Address = address.Trim();
            if (string.IsNullOrWhiteSpace(existing.Region)) existing.Region = region;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            await customerRepository.SaveCustomerChangesAsync(cancellationToken);
            return;
        }

        var uniqueNationalId = nationalId.Trim();
        var nationalIdTaken = !string.IsNullOrWhiteSpace(uniqueNationalId) && await customerRepository.CustomerExistsByNationalIdAsync(uniqueNationalId, cancellationToken);
        if (string.IsNullOrWhiteSpace(uniqueNationalId) || nationalIdTaken)
            uniqueNationalId = await UniqueNationalIdAsync(userId, cancellationToken);

        await CreateCustomerProfileAsync(userId, email, fullName, uniqueNationalId, phoneNumber, address, cancellationToken, region);
    }

    private async Task<CustomerProfile> CreateCustomerProfileAsync(Guid userId, string email, string fullName, string nationalId, string phoneNumber, string address, CancellationToken cancellationToken, string region = "")
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
            Region = region,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await customerRepository.AddCustomerAsync(customer, cancellationToken);
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
