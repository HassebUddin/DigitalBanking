using DigitalBanking.Account.Api.Contracts;
using Microsoft.AspNetCore.Http;
using DigitalBanking.Account.Api.Domain;
using DigitalBanking.Account.Api.Repository;
using DigitalBanking.BuildingBlocks.Caching;
using DigitalBanking.BuildingBlocks.Distributed;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Web;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts.Events;

namespace DigitalBanking.Account.Api.Application;

public sealed class AccountService(IAccountRepository accountRepository, ICacheService cacheService, IEventPublisher eventPublisher, ProvinceShardMap shards, ILogger<AccountService> logger)
{
    public async Task<AccountResponse> AddAccountAsync(Guid userId, Guid customerId, OpenAccountRequest request, CancellationToken cancellationToken)
    {
        var accountType = string.Equals(request.AccountType, AccountTypes.Current, StringComparison.OrdinalIgnoreCase) ? AccountTypes.Current : AccountTypes.Savings;
        var accountId = Guid.NewGuid();
        var region = await accountRepository.RegionOfUserAsync(userId, cancellationToken);
        var account = new BankAccount { Id = accountId, CustomerId = customerId, UserId = userId, AccountNumber = await CreateAccountNumberAsync(accountId, region, cancellationToken), AccountType = accountType, Balance = 0, Currency = "PKR", Status = AccountStatuses.Active, CreatedAtUtc = DateTime.UtcNow };

        await accountRepository.AddAccountAsync(account, cancellationToken);
        // Saved in the same transaction as the account (outbox): the admin's projector puts it in the Redis global index.
        accountRepository.AddAccountEvent(new AccountCreatedEvent
        {
            AccountId = account.Id,
            CustomerId = customerId,
            UserId = userId,
            AccountNumber = account.AccountNumber,
            AccountType = account.AccountType,
            Region = region,
            Shard = shards.ShardKey(region, account.Id)
        });

        await accountRepository.SaveAccountChangesAsync(cancellationToken);
        await cacheService.SetAsync(BalanceCacheKey(account.Id), account.Balance, TimeSpan.FromMinutes(5), cancellationToken);
        return new AccountResponse { Id = account.Id, CustomerId = account.CustomerId, UserId = account.UserId, AccountNumber = account.AccountNumber, AccountType = account.AccountType, Balance = account.Balance, Currency = account.Currency, Status = account.Status, CreatedAtUtc = account.CreatedAtUtc };
    }

    public async Task<PagedResult<AccountResponse>> SearchAccountsAsync(string? search, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = PagedResult<AccountResponse>.Normalize(page, pageSize);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (accounts, total, strategy) = await accountRepository.SearchAccountsAsync(search, status, page, pageSize, cancellationToken);
        var items = accounts.Select(account => new AccountResponse { Id = account.Id, CustomerId = account.CustomerId, UserId = account.UserId, AccountNumber = account.AccountNumber, AccountType = account.AccountType, Balance = account.Balance, Currency = account.Currency, Status = account.Status, CreatedAtUtc = account.CreatedAtUtc }).ToList();
        return new PagedResult<AccountResponse>(items, total, page, pageSize) { Strategy = strategy, TookMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1) };
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
        // A prepared two-phase commit promised the coordinator it can still be applied here.
        if (status != AccountStatuses.Active && await accountRepository.HasPreparedMovementAsync(accountId, cancellationToken))
            throw new BusinessRuleException("This account is reserved by a distributed transaction awaiting its decision. Try again shortly.");

        account.Status = status;
        await accountRepository.SaveAccountChangesAsync(cancellationToken);
        await cacheService.RemoveAsync(BalanceCacheKey(account.Id), cancellationToken);
        return new AccountResponse { Id = account.Id, CustomerId = account.CustomerId, UserId = account.UserId, AccountNumber = account.AccountNumber, AccountType = account.AccountType, Balance = account.Balance, Currency = account.Currency, Status = account.Status, CreatedAtUtc = account.CreatedAtUtc };
    }

    public async Task<AccountApplicationResponse> RequestAccountApplicationAsync(Guid userId, Guid customerId, string accountType, string purpose, IFormFile identityDocument, IFormFile identityBackDocument, IFormFile? addressDocument, IFormFile signature, AccountFileStore fileStore, CancellationToken cancellationToken)
    {
        // When the customer's province is down (master and slave), the request is queued instead of failing.
        var provinceDown = false;
        try
        {
            await EnsureCanRequestAccountAsync(userId, cancellationToken);
        }
        catch (Exception exception) when (RegionErrors.IsRegionDown(exception))
        {
            logger.LogWarning(exception, "Account request of {UserId} queued: the province could not be read.", userId);
            provinceDown = true;
        }

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

        if (!provinceDown)
        {
            try
            {
                await accountRepository.AddAccountApplicationAsync(application, cancellationToken);
                await accountRepository.SaveAccountChangesAsync(cancellationToken);
                return ToResponse(application);
            }
            catch (Exception exception) when (RegionErrors.IsRegionDown(exception))
            {
                logger.LogWarning(exception, "Account request of {UserId} queued: the province could not be written.", userId);
            }
        }

        // The documents are saved; only the database row is missing. RabbitMQ keeps the request until the
        // province is back (see QueuedAccountWorkHandlers), then the customer is notified.
        await eventPublisher.PublishAsync(new AccountApplicationQueuedEvent
        {
            ApplicationId = application.Id,
            UserId = application.UserId,
            CustomerId = application.CustomerId,
            AccountType = application.AccountType,
            Purpose = application.Purpose ?? string.Empty,
            IdentityDocumentUrl = application.IdentityDocumentUrl ?? string.Empty,
            IdentityBackDocumentUrl = application.IdentityBackDocumentUrl ?? string.Empty,
            AddressDocumentUrl = application.AddressDocumentUrl ?? string.Empty,
            SignatureUrl = application.SignatureUrl ?? string.Empty,
            CreatedAtUtc = application.CreatedAtUtc
        }, cancellationToken);
        application.Status = ApplicationStatuses.Queued;
        return ToResponse(application);
    }

    private async Task EnsureCanRequestAccountAsync(Guid userId, CancellationToken cancellationToken)
    {
        // One person, one account.
        if ((await accountRepository.GetAccountsAsync(userId, false, cancellationToken)).Any(account => account.Status != AccountStatuses.Closed))
            throw new BusinessRuleException("You already have an account. Only one account is allowed per customer.");

        if (await accountRepository.AccountApplicationPendingExistsAsync(userId, cancellationToken))
            throw new BusinessRuleException("You already have an account request waiting for bank approval.");
    }

    // Replays a request that was queued while the customer's province was down.
    public async Task ApplyQueuedApplicationAsync(AccountApplicationQueuedEvent queued, CancellationToken cancellationToken)
    {
        if (await accountRepository.AccountApplicationExistsAsync(queued.UserId, queued.ApplicationId, cancellationToken))
        {
            return;
        }

        try
        {
            await EnsureCanRequestAccountAsync(queued.UserId, cancellationToken);
        }
        catch (BusinessRuleException exception)
        {
            accountRepository.AddAccountEvent(new CustomerNotificationRequestedEvent { UserId = queued.UserId, Title = "Account request not submitted", Body = exception.Message });
            await accountRepository.SaveAccountChangesAsync(cancellationToken);
            return;
        }

        var customerId = queued.CustomerId != Guid.Empty ? queued.CustomerId : await accountRepository.FindCustomerIdAsync(queued.UserId, cancellationToken);
        await accountRepository.AddAccountApplicationAsync(new AccountApplication
        {
            Id = queued.ApplicationId,
            UserId = queued.UserId,
            CustomerId = customerId,
            AccountType = queued.AccountType,
            Purpose = queued.Purpose,
            IdentityDocumentUrl = queued.IdentityDocumentUrl,
            IdentityBackDocumentUrl = queued.IdentityBackDocumentUrl,
            AddressDocumentUrl = queued.AddressDocumentUrl,
            SignatureUrl = queued.SignatureUrl,
            TermsAccepted = true,
            Status = ApplicationStatuses.Pending,
            CreatedAtUtc = queued.CreatedAtUtc
        }, cancellationToken);
        accountRepository.AddAccountEvent(new CustomerNotificationRequestedEvent
        {
            UserId = queued.UserId,
            Title = "Account request submitted",
            Body = $"Our systems are back. Your {queued.AccountType} account request has been submitted and is now waiting for bank approval."
        });
        await accountRepository.SaveAccountChangesAsync(cancellationToken);
    }

    private static AccountApplicationResponse ToResponse(AccountApplication application)
    {
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
            if ((await accountRepository.GetAccountsAsync(application.UserId, false, cancellationToken)).Any(account => account.Status != AccountStatuses.Closed))
                throw new BusinessRuleException("This customer already has an account. Only one account is allowed per customer.");

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

    public Task DebitAsync(Guid accountId, MoneyMovementRequest request, CancellationToken cancellationToken) =>
        PostAsync(accountId, request, isDebit: true, cancellationToken);

    public Task CreditAsync(Guid accountId, MoneyMovementRequest request, CancellationToken cancellationToken) =>
        PostAsync(accountId, request, isDebit: false, cancellationToken);

    // Lets a resumed transfer saga find out whether a step it lost track of actually happened.
    public Task<bool> MovementAppliedAsync(Guid accountId, string referenceNumber, bool isDebit, CancellationToken cancellationToken) =>
        accountRepository.PostingAppliedAsync(accountId, $"{referenceNumber.Trim()}:{(isDebit ? "D" : "C")}:{accountId}", cancellationToken);

    // A new account funded with an opening deposit (staff tooling, drills and benchmarks).
    public async Task<AccountResponse> OpenFundedAccountAsync(OpenFundedAccountRequest request, CancellationToken cancellationToken)
    {
        if (request.UserId == Guid.Empty) throw new ValidationException("A user id is required.");
        if (request.OpeningBalance < 0) throw new ValidationException("The opening balance cannot be negative.");

        var account = await AddAccountAsync(request.UserId, Guid.Empty, new OpenAccountRequest { AccountType = request.AccountType }, cancellationToken);
        if (request.OpeningBalance > 0)
        {
            await CreditAsync(account.Id, new MoneyMovementRequest { Amount = request.OpeningBalance, ReferenceNumber = $"OPEN-{account.AccountNumber}", Purpose = MovementPurposes.Opening }, cancellationToken);
            account.Balance = request.OpeningBalance;
        }

        return account;
    }

    // ACID money movement: inside one database transaction in the account's province, the account row is
    // locked, the balance changes and a balanced double-entry posting (customer leg + general-ledger leg) is
    // written; all of it commits or none of it does. The idempotency key makes a retried call (e.g. a
    // transfer saga resuming after a crash or a failover) a no-op instead of a second debit.
    private async Task PostAsync(Guid accountId, MoneyMovementRequest request, bool isDebit, CancellationToken cancellationToken)
    {
        var side = isDebit ? "D" : "C";
        var purpose = MovementPurposes.Normalize(request.Purpose, isDebit ? MovementPurposes.TransferOut : MovementPurposes.TransferIn);
        var reference = string.IsNullOrWhiteSpace(request.ReferenceNumber) ? $"ADHOC-{Guid.NewGuid():N}" : request.ReferenceNumber.Trim();
        var idempotencyKey = $"{reference}:{side}:{accountId}";

        await using var locked = await accountRepository.LockAccountAsync(accountId, cancellationToken) ?? throw new NotFoundException("Account was not found.");
        var account = locked.Account;
        if (await accountRepository.LedgerPostingExistsAsync(idempotencyKey, cancellationToken))
        {
            // Already applied by an earlier attempt (which may not have been acknowledged yet).
            await accountRepository.WaitForStandbyAsync(cancellationToken);
            return;
        }

        if (account.Status == AccountStatuses.Frozen)
            throw new BusinessRuleException("This account is frozen.");
        if (account.Status == AccountStatuses.Closed)
            throw new BusinessRuleException("This account is closed.");
        if (isDebit && account.Balance < request.Amount)
            throw new BusinessRuleException("Insufficient account balance.");

        account.Balance += isDebit ? -request.Amount : request.Amount;
        var now = DateTime.UtcNow;
        var journalId = reference.EndsWith("-REFUND", StringComparison.Ordinal) ? reference[..^"-REFUND".Length] : reference;
        accountRepository.AddLedgerEntries(
            new LedgerEntry
            {
                Id = Guid.NewGuid(), JournalId = journalId, IdempotencyKey = idempotencyKey, Leg = 1,
                LedgerAccount = LedgerAccounts.Customer(account.AccountNumber), AccountId = account.Id,
                Debit = isDebit ? request.Amount : 0, Credit = isDebit ? 0 : request.Amount,
                BalanceAfter = account.Balance, Purpose = purpose, CreatedAtUtc = now
            },
            new LedgerEntry
            {
                Id = Guid.NewGuid(), JournalId = journalId, IdempotencyKey = idempotencyKey, Leg = 2,
                LedgerAccount = MovementPurposes.CounterAccount(purpose), AccountId = null,
                Debit = isDebit ? 0 : request.Amount, Credit = isDebit ? request.Amount : 0,
                Purpose = purpose, CreatedAtUtc = now
            });

        await accountRepository.SaveAccountChangesAsync(cancellationToken);
        await locked.CommitAsync(cancellationToken);
        await locked.DisposeAsync();
        // Acknowledge only once the standby holds the posting, so a failover cannot lose a confirmed movement.
        await accountRepository.WaitForStandbyAsync(cancellationToken);
        await cacheService.SetAsync(BalanceCacheKey(account.Id), account.Balance, TimeSpan.FromMinutes(5), cancellationToken);
    }

    // The number is reserved in the bank-wide account directory, whose primary key keeps it unique.
    private async Task<string> CreateAccountNumberAsync(Guid accountId, string region, CancellationToken cancellationToken)
    {
        while (true)
        {
            var accountNumber = $"10{Random.Shared.NextInt64(1000000000, 9999999999)}";
            if (await accountRepository.TryReserveAccountNumberAsync(accountNumber, accountId, region, cancellationToken))
            {
                return accountNumber;
            }
        }
    }

    private static string BalanceCacheKey(Guid accountId) => $"account:balance:{accountId}";
}
