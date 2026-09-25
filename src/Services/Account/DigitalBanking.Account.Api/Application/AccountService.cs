using DigitalBanking.Account.Api.Contracts;
using Microsoft.AspNetCore.Http;
using DigitalBanking.Account.Api.Domain;
using DigitalBanking.Account.Api.Infrastructure;
using DigitalBanking.BuildingBlocks.Caching;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Outbox;
using DigitalBanking.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Account.Api.Application;

public sealed class AccountService(AccountDbContext dbContext, ICacheService cacheService)
{
    public async Task<AccountResponse> OpenAccountAsync(Guid userId, Guid customerId, OpenAccountRequest request, CancellationToken cancellationToken)
    {
        var accountType = string.Equals(request.AccountType, AccountTypes.Current, StringComparison.OrdinalIgnoreCase)
            ? AccountTypes.Current
            : AccountTypes.Savings;

        var account = new BankAccount
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            UserId = userId,
            AccountNumber = await CreateUniqueAccountNumberAsync(cancellationToken),
            AccountType = accountType,
            Balance = 0,
            Currency = "PKR",
            Status = AccountStatuses.Active,
            CreatedAtUtc = DateTime.UtcNow
        };

        dbContext.Accounts.Add(account);
        OutboxWriter.AddEvent(dbContext, new AccountCreatedEvent
        {
            AccountId = account.Id,
            CustomerId = customerId,
            UserId = userId,
            AccountNumber = account.AccountNumber,
            AccountType = account.AccountType
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheService.SetAsync(BalanceCacheKey(account.Id), account.Balance, TimeSpan.FromMinutes(5), cancellationToken);
        return Map(account);
    }

    public async Task<IReadOnlyList<AccountResponse>> ListForUserAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var query = dbContext.Accounts.AsQueryable();
        if (!isAdmin)
        {
            query = query.Where(account => account.UserId == userId);
        }

        var accounts = await query.OrderByDescending(account => account.CreatedAtUtc).ToListAsync(cancellationToken);
        return accounts.Select(Map).ToList();
    }

    public async Task<AccountResponse> GetAsync(Guid accountId, Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var account = await FindAccountAsync(accountId, cancellationToken);
        EnsureOwnerOrAdmin(account, userId, isAdmin);
        return Map(account);
    }

    public async Task<AccountResponse> GetInternalAsync(Guid accountId, CancellationToken cancellationToken)
    {
        return Map(await FindAccountAsync(accountId, cancellationToken));
    }

    public async Task<AccountResponse> GetByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken)
    {
        var account = await dbContext.Accounts.FirstOrDefaultAsync(item => item.AccountNumber == accountNumber, cancellationToken)
            ?? throw new NotFoundException("Account was not found.");
        return Map(account);
    }

    public async Task<AccountResponse> ChangeStatusAsync(Guid accountId, string status, CancellationToken cancellationToken)
    {
        if (status is not (AccountStatuses.Frozen or AccountStatuses.Active or AccountStatuses.Closed))
        {
            throw new ValidationException("Account status is invalid.");
        }

        var account = await FindAccountAsync(accountId, cancellationToken);
        if (account.Status == AccountStatuses.Closed)
        {
            throw new BusinessRuleException("A closed account cannot change status.");
        }

        account.Status = status;
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheService.RemoveAsync(BalanceCacheKey(account.Id), cancellationToken);
        return Map(account);
    }

    public async Task<AccountApplicationResponse> SubmitApplicationAsync(
        Guid userId,
        Guid customerId,
        string accountType,
        string purpose,
        IFormFile identityDocument,
        IFormFile addressDocument,
        IFormFile signature,
        bool termsAccepted,
        AccountFileStore fileStore,
        CancellationToken cancellationToken)
    {
        if (identityDocument is null || addressDocument is null || signature is null)
        {
            throw new ValidationException("CNIC, address proof and signature are required.");
        }

        if (!termsAccepted)
        {
            throw new ValidationException("You must accept the account opening declaration.");
        }

        var pendingExists = await dbContext.Applications.AnyAsync(
            application => application.UserId == userId && application.Status == ApplicationStatuses.Pending,
            cancellationToken);
        if (pendingExists)
        {
            throw new BusinessRuleException("You already have an account request waiting for bank approval.");
        }

        var type = string.Equals(accountType, AccountTypes.Current, StringComparison.OrdinalIgnoreCase)
            ? AccountTypes.Current
            : AccountTypes.Savings;

        var application = new AccountApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CustomerId = customerId,
            AccountType = type,
            Purpose = string.IsNullOrWhiteSpace(purpose) ? "Personal banking" : purpose.Trim(),
            IdentityDocumentUrl = await fileStore.SaveAsync(identityDocument, "identity", cancellationToken),
            AddressDocumentUrl = await fileStore.SaveAsync(addressDocument, "address", cancellationToken),
            SignatureUrl = await fileStore.SaveAsync(signature, "signatures", cancellationToken),
            TermsAccepted = true,
            Status = ApplicationStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };

        dbContext.Applications.Add(application);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapApplication(application);
    }

    public async Task<IReadOnlyList<AccountApplicationResponse>> ListApplicationsAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var query = dbContext.Applications.AsQueryable();
        if (!isAdmin)
        {
            query = query.Where(application => application.UserId == userId);
        }

        var applications = await query.OrderByDescending(application => application.CreatedAtUtc).ToListAsync(cancellationToken);
        return applications.Select(MapApplication).ToList();
    }

    public async Task<AccountApplicationResponse> ApproveApplicationAsync(Guid applicationId, string note, CancellationToken cancellationToken)
    {
        var application = await FindApplicationAsync(applicationId, cancellationToken);
        if (application.Status != ApplicationStatuses.Pending)
        {
            throw new BusinessRuleException("Only pending requests can be approved.");
        }

        var account = await OpenAccountAsync(application.UserId, application.CustomerId, new OpenAccountRequest { AccountType = application.AccountType }, cancellationToken);
        application.Status = ApplicationStatuses.Approved;
        application.ReviewNote = string.IsNullOrWhiteSpace(note) ? "Approved by bank admin." : note.Trim();
        application.CreatedAccountId = account.Id;
        application.ReviewedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapApplication(application);
    }

    public async Task<AccountApplicationResponse> RejectApplicationAsync(Guid applicationId, string note, CancellationToken cancellationToken)
    {
        var application = await FindApplicationAsync(applicationId, cancellationToken);
        if (application.Status != ApplicationStatuses.Pending)
        {
            throw new BusinessRuleException("Only pending requests can be rejected.");
        }

        application.Status = ApplicationStatuses.Rejected;
        application.ReviewNote = string.IsNullOrWhiteSpace(note) ? "Rejected by bank admin." : note.Trim();
        application.ReviewedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapApplication(application);
    }

    public async Task DebitAsync(Guid accountId, MoneyMovementRequest request, CancellationToken cancellationToken)
    {
        var account = await FindAccountAsync(accountId, cancellationToken);
        EnsureActive(account);
        if (request.Amount <= 0)
        {
            throw new ValidationException("Amount must be greater than zero.");
        }

        if (account.Balance < request.Amount)
        {
            throw new BusinessRuleException("Insufficient account balance.");
        }

        account.Balance -= request.Amount;
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheService.SetAsync(BalanceCacheKey(account.Id), account.Balance, TimeSpan.FromMinutes(5), cancellationToken);
    }

    public async Task CreditAsync(Guid accountId, MoneyMovementRequest request, CancellationToken cancellationToken)
    {
        var account = await FindAccountAsync(accountId, cancellationToken);
        EnsureActive(account);
        if (request.Amount <= 0)
        {
            throw new ValidationException("Amount must be greater than zero.");
        }

        account.Balance += request.Amount;
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheService.SetAsync(BalanceCacheKey(account.Id), account.Balance, TimeSpan.FromMinutes(5), cancellationToken);
    }

    private async Task<AccountApplication> FindApplicationAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        return await dbContext.Applications.FirstOrDefaultAsync(application => application.Id == applicationId, cancellationToken)
            ?? throw new NotFoundException("Account request was not found.");
    }

    private async Task<BankAccount> FindAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        return await dbContext.Accounts.FirstOrDefaultAsync(account => account.Id == accountId, cancellationToken)
            ?? throw new NotFoundException("Account was not found.");
    }

    private async Task<string> CreateUniqueAccountNumberAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var accountNumber = $"10{Random.Shared.NextInt64(1000000000, 9999999999)}";
            var exists = await dbContext.Accounts.AnyAsync(account => account.AccountNumber == accountNumber, cancellationToken);
            if (!exists)
            {
                return accountNumber;
            }
        }
    }

    private static void EnsureOwnerOrAdmin(BankAccount account, Guid userId, bool isAdmin)
    {
        if (!isAdmin && account.UserId != userId)
        {
            throw new ForbiddenException("You cannot access this account.");
        }
    }

    private static void EnsureActive(BankAccount account)
    {
        if (account.Status == AccountStatuses.Frozen)
        {
            throw new BusinessRuleException("This account is frozen.");
        }

        if (account.Status == AccountStatuses.Closed)
        {
            throw new BusinessRuleException("This account is closed.");
        }
    }

    private static string BalanceCacheKey(Guid accountId) => $"account:balance:{accountId}";

    private static AccountApplicationResponse MapApplication(AccountApplication application)
    {
        return new AccountApplicationResponse
        {
            Id = application.Id,
            UserId = application.UserId,
            AccountType = application.AccountType,
            Purpose = application.Purpose,
            Status = application.Status,
            ReviewNote = application.ReviewNote,
            HasIdentityDocument = !string.IsNullOrWhiteSpace(application.IdentityDocumentUrl),
            HasAddressDocument = !string.IsNullOrWhiteSpace(application.AddressDocumentUrl),
            HasSignature = !string.IsNullOrWhiteSpace(application.SignatureUrl),
            CreatedAccountId = application.CreatedAccountId,
            CreatedAtUtc = application.CreatedAtUtc
        };
    }

    private static AccountResponse Map(BankAccount account)
    {
        return new AccountResponse
        {
            Id = account.Id,
            CustomerId = account.CustomerId,
            UserId = account.UserId,
            AccountNumber = account.AccountNumber,
            AccountType = account.AccountType,
            Balance = account.Balance,
            Currency = account.Currency,
            Status = account.Status,
            CreatedAtUtc = account.CreatedAtUtc
        };
    }
}
