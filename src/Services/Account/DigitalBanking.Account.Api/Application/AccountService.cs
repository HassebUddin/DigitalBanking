using DigitalBanking.Account.Api.Contracts;
using Microsoft.AspNetCore.Http;
using DigitalBanking.Account.Api.Domain;
using DigitalBanking.Account.Api.Repository;
using DigitalBanking.BuildingBlocks.Caching;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.Contracts.Events;

namespace DigitalBanking.Account.Api.Application;

public sealed class AccountService(IAccountRepository accountRepository, ICacheService cacheService)
{
    public async Task<AccountResponse> AddAccountAsync(Guid userId, Guid customerId, OpenAccountRequest request, CancellationToken cancellationToken)
    {
        var accountType = string.Equals(request.AccountType, AccountTypes.Current, StringComparison.OrdinalIgnoreCase) ? AccountTypes.Current : AccountTypes.Savings;
        var account = new BankAccount { Id = Guid.NewGuid(), CustomerId = customerId, UserId = userId, AccountNumber = await CreateAccountNumberAsync(cancellationToken), AccountType = accountType, Balance = 0, Currency = "PKR", Status = AccountStatuses.Active, CreatedAtUtc = DateTime.UtcNow };
      
        accountRepository.AddAccount(account);
        accountRepository.AddAccountEvent(new AccountCreatedEvent
        {
            AccountId = account.Id,
            CustomerId = customerId,
            UserId = userId,
            AccountNumber = account.AccountNumber,
            AccountType = account.AccountType
        });

        await accountRepository.SaveAccountChangesAsync(cancellationToken);
        await cacheService.SetAsync(BalanceCacheKey(account.Id), account.Balance, TimeSpan.FromMinutes(5), cancellationToken);
        return new AccountResponse { Id = account.Id, CustomerId = account.CustomerId, UserId = account.UserId, AccountNumber = account.AccountNumber, AccountType = account.AccountType, Balance = account.Balance, Currency = account.Currency, Status = account.Status, CreatedAtUtc = account.CreatedAtUtc };
    }

    public async Task<IReadOnlyList<AccountResponse>> GetAccountsAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var accounts = await accountRepository.GetAccountsAsync(userId, isAdmin, cancellationToken);
        return accounts.Select(account => new AccountResponse { Id = account.Id, CustomerId = account.CustomerId, UserId = account.UserId, AccountNumber = account.AccountNumber, AccountType = account.AccountType, Balance = account.Balance, Currency = account.Currency, Status = account.Status, CreatedAtUtc = account.CreatedAtUtc }).ToList();
    }

    public async Task<AccountResponse> GetAccountByIdAsync(Guid accountId, Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var account = await accountRepository.GetAccountByIdAsync(accountId, cancellationToken) ?? throw new NotFoundException("Account was not found.");
        if (!isAdmin && account.UserId != userId)
            throw new ForbiddenException("You cannot access this account.");
        return new AccountResponse { Id = account.Id, CustomerId = account.CustomerId, UserId = account.UserId, AccountNumber = account.AccountNumber, AccountType = account.AccountType, Balance = account.Balance, Currency = account.Currency, Status = account.Status, CreatedAtUtc = account.CreatedAtUtc };
    }

    public async Task<AccountResponse> GetInternalEmployeeAccountByIdAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await accountRepository.GetAccountByIdAsync(accountId, cancellationToken) ?? throw new NotFoundException("Account was not found.");
        return new AccountResponse { Id = account.Id, CustomerId = account.CustomerId, UserId = account.UserId, AccountNumber = account.AccountNumber, AccountType = account.AccountType, Balance = account.Balance, Currency = account.Currency, Status = account.Status, CreatedAtUtc = account.CreatedAtUtc };
    }

    public async Task<AccountResponse> GetAccountByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken)
    {
        var account = await accountRepository.GetAccountByAccountNumberAsync(accountNumber, cancellationToken) ?? throw new NotFoundException("Account was not found.");
        return new AccountResponse { Id = account.Id, CustomerId = account.CustomerId, UserId = account.UserId, AccountNumber = account.AccountNumber, AccountType = account.AccountType, Balance = account.Balance, Currency = account.Currency, Status = account.Status, CreatedAtUtc = account.CreatedAtUtc };
    }

    public async Task<AccountResponse> UpdateAccountStatusAsync(Guid accountId, string status, CancellationToken cancellationToken)
    {
        var account = await accountRepository.GetAccountByIdAsync(accountId, cancellationToken) ?? throw new NotFoundException("Account was not found.");
        if (account.Status == AccountStatuses.Closed)
            throw new BusinessRuleException("A closed account cannot change status.");

        account.Status = status;
        await accountRepository.SaveAccountChangesAsync(cancellationToken);
        await cacheService.RemoveAsync(BalanceCacheKey(account.Id), cancellationToken);
        return new AccountResponse { Id = account.Id, CustomerId = account.CustomerId, UserId = account.UserId, AccountNumber = account.AccountNumber, AccountType = account.AccountType, Balance = account.Balance, Currency = account.Currency, Status = account.Status, CreatedAtUtc = account.CreatedAtUtc };
    }

    public async Task<AccountApplicationResponse> RequestAccountApplicationAsync(Guid userId, Guid customerId, string accountType, string purpose, IFormFile identityDocument, IFormFile identityBackDocument, IFormFile? addressDocument, IFormFile signature, AccountFileStore fileStore, CancellationToken cancellationToken)
    {
        var pendingExists = await accountRepository.AccountApplicationPendingExistsAsync(userId, cancellationToken);
        if (pendingExists)
            throw new BusinessRuleException("You already have an account request waiting for bank approval.");

        var type = string.Equals(accountType, AccountTypes.Current, StringComparison.OrdinalIgnoreCase) ? AccountTypes.Current : AccountTypes.Savings;
        var application = new AccountApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CustomerId = customerId,
            AccountType = type,
            Purpose = string.IsNullOrWhiteSpace(purpose) ? "Personal banking" : purpose.Trim(),
            IdentityDocumentUrl = await fileStore.SaveAsync(identityDocument, "identity", cancellationToken),
            IdentityBackDocumentUrl = await fileStore.SaveAsync(identityBackDocument, "identity", cancellationToken),
            AddressDocumentUrl = addressDocument is null? string.Empty  : await fileStore.SaveAsync(addressDocument, "address", cancellationToken),
            SignatureUrl = await fileStore.SaveAsync(signature, "signatures", cancellationToken),
            TermsAccepted = true,
            Status = ApplicationStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };

        accountRepository.AddAccountApplication(application);
        await accountRepository.SaveAccountChangesAsync(cancellationToken);
        return new AccountApplicationResponse { Id = application.Id, UserId = application.UserId, AccountType = application.AccountType, Purpose = application.Purpose ?? string.Empty, Status = application.Status, ReviewNote = application.ReviewNote ?? string.Empty, HasIdentityDocument = !string.IsNullOrWhiteSpace(application.IdentityDocumentUrl), HasIdentityBackDocument = !string.IsNullOrWhiteSpace(application.IdentityBackDocumentUrl), HasAddressDocument = !string.IsNullOrWhiteSpace(application.AddressDocumentUrl), HasSignature = !string.IsNullOrWhiteSpace(application.SignatureUrl), IdentityDocumentUrl = application.IdentityDocumentUrl, IdentityBackDocumentUrl = application.IdentityBackDocumentUrl, AddressDocumentUrl = application.AddressDocumentUrl, SignatureUrl = application.SignatureUrl, CreatedAccountId = application.CreatedAccountId, CreatedAtUtc = application.CreatedAtUtc };
    }

    public async Task<IReadOnlyList<AccountApplicationResponse>> GetAccountApplicationsAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var applications = await accountRepository.GetAccountApplicationsAsync(userId, isAdmin, cancellationToken);
        return applications.Select(application => new AccountApplicationResponse { Id = application.Id, UserId = application.UserId, AccountType = application.AccountType, Purpose = application.Purpose ?? string.Empty, Status = application.Status, ReviewNote = application.ReviewNote ?? string.Empty, HasIdentityDocument = !string.IsNullOrWhiteSpace(application.IdentityDocumentUrl), HasIdentityBackDocument = !string.IsNullOrWhiteSpace(application.IdentityBackDocumentUrl), HasAddressDocument = !string.IsNullOrWhiteSpace(application.AddressDocumentUrl), HasSignature = !string.IsNullOrWhiteSpace(application.SignatureUrl), IdentityDocumentUrl = application.IdentityDocumentUrl, IdentityBackDocumentUrl = application.IdentityBackDocumentUrl, AddressDocumentUrl = application.AddressDocumentUrl, SignatureUrl = application.SignatureUrl, CreatedAccountId = application.CreatedAccountId, CreatedAtUtc = application.CreatedAtUtc }).ToList();
    }

    public async Task<AccountApplicationResponse> UpdateAccountApplicationStatusAsync(Guid applicationId, string status, string note, CancellationToken cancellationToken)
    {
        var application = await accountRepository.GetAccountApplicationByIdAsync(applicationId, cancellationToken) ?? throw new NotFoundException("Account request was not found.");
        if (status == ApplicationStatuses.Approved)
        {
            if (application.Status is not (ApplicationStatuses.Pending or ApplicationStatuses.Rejected))
                throw new BusinessRuleException("Only pending or rejected requests can be approved.");

            var account = await AddAccountAsync(application.UserId, application.CustomerId, new OpenAccountRequest { AccountType = application.AccountType }, cancellationToken);
            application.Status = ApplicationStatuses.Approved;
            application.ReviewNote = string.IsNullOrWhiteSpace(note) ? "Approved by bank admin." : note.Trim();
            application.CreatedAccountId = account.Id;
            application.ReviewedAtUtc = DateTime.UtcNow;
        }
        else if (status == ApplicationStatuses.Rejected)
        {
            if (application.Status != ApplicationStatuses.Pending)
                throw new BusinessRuleException("Only pending requests can be rejected.");

            application.Status = ApplicationStatuses.Rejected;
            application.ReviewNote = string.IsNullOrWhiteSpace(note) ? "Documents need correction." : note.Trim();
            application.ReviewedAtUtc = DateTime.UtcNow;
            accountRepository.AddAccountEvent(new AccountApplicationRejectedEvent { ApplicationId = application.Id, UserId = application.UserId, AccountType = application.AccountType, Note = application.ReviewNote });
        }
        else
        {
            if (application.Status != ApplicationStatuses.Rejected)
                throw new BusinessRuleException("Only rejected requests can be returned to review.");

            application.Status = ApplicationStatuses.Pending;
            application.ReviewNote = string.IsNullOrWhiteSpace(note) ? "Returned to review by the bank." : note.Trim();
            application.ReviewedAtUtc = null;
            accountRepository.AddAccountEvent(new AccountApplicationReopenedEvent { ApplicationId = application.Id, UserId = application.UserId, AccountType = application.AccountType });
        }

        await accountRepository.SaveAccountChangesAsync(cancellationToken);
        return new AccountApplicationResponse { Id = application.Id, UserId = application.UserId, AccountType = application.AccountType, Purpose = application.Purpose ?? string.Empty, Status = application.Status, ReviewNote = application.ReviewNote ?? string.Empty, HasIdentityDocument = !string.IsNullOrWhiteSpace(application.IdentityDocumentUrl), HasIdentityBackDocument = !string.IsNullOrWhiteSpace(application.IdentityBackDocumentUrl), HasAddressDocument = !string.IsNullOrWhiteSpace(application.AddressDocumentUrl), HasSignature = !string.IsNullOrWhiteSpace(application.SignatureUrl), IdentityDocumentUrl = application.IdentityDocumentUrl, IdentityBackDocumentUrl = application.IdentityBackDocumentUrl, AddressDocumentUrl = application.AddressDocumentUrl, SignatureUrl = application.SignatureUrl, CreatedAccountId = application.CreatedAccountId, CreatedAtUtc = application.CreatedAtUtc };
    }

    public async Task DebitAsync(Guid accountId, MoneyMovementRequest request, CancellationToken cancellationToken)
    {
        var account = await accountRepository.GetAccountByIdAsync(accountId, cancellationToken) ?? throw new NotFoundException("Account was not found.");
       
        if (account.Status == AccountStatuses.Frozen)
            throw new BusinessRuleException("This account is frozen.");
        if (account.Status == AccountStatuses.Closed)
            throw new BusinessRuleException("This account is closed.");
        if (account.Balance < request.Amount)
            throw new BusinessRuleException("Insufficient account balance.");

        account.Balance -= request.Amount;
        await accountRepository.SaveAccountChangesAsync(cancellationToken);
        await cacheService.SetAsync(BalanceCacheKey(account.Id), account.Balance, TimeSpan.FromMinutes(5), cancellationToken);
    }

    public async Task CreditAsync(Guid accountId, MoneyMovementRequest request, CancellationToken cancellationToken)
    {
        var account = await accountRepository.GetAccountByIdAsync(accountId, cancellationToken) ?? throw new NotFoundException("Account was not found.");
        if (account.Status == AccountStatuses.Frozen)
            throw new BusinessRuleException("This account is frozen.");
        if (account.Status == AccountStatuses.Closed)
            throw new BusinessRuleException("This account is closed.");

        account.Balance += request.Amount;
        await accountRepository.SaveAccountChangesAsync(cancellationToken);
        await cacheService.SetAsync(BalanceCacheKey(account.Id), account.Balance, TimeSpan.FromMinutes(5), cancellationToken);
    }

    private async Task<string> CreateAccountNumberAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var accountNumber = $"10{Random.Shared.NextInt64(1000000000, 9999999999)}";
            if (!await accountRepository.AccountNumberExistsAsync(accountNumber, cancellationToken))
            {
                return accountNumber;
            }
        }
    }

    private static string BalanceCacheKey(Guid accountId) => $"account:balance:{accountId}";
}
