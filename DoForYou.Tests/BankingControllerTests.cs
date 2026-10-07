using System.Security.Claims;
using System.Text.Json;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public class BankingControllerTests
{
    [Fact]
    public async System.Threading.Tasks.Task AddBankAccount_UsesPaystackCanonicalBankDetails()
    {
        using var context = CreateContext();

        var user = CreateUser(10);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var banking = new FakeBankingService
        {
            VerificationResult =
                new BankVerificationResult(
                    true,
                    "Bank account verified.",
                    "RCP_TEST")
        };

        var banks = new FakePaystackBankProvider(
            new[]
            {
                new SupportedBank(
                    "632005",
                    "Canonical Bank",
                    "123456")
            });

        var controller =
            CreateController(
                context,
                user.Id,
                banking,
                banks);

        var result =
            await controller.AddBankAccount(
                new AddBankAccountRequest
                {
                    BankCode = "632005",
                    BankGroupId = "632005",

                    // Deliberately incorrect client display values.
                    BankName = "Fake Client Bank",
                    BranchCode = "999999",

                    AccountNumber = "1234567890",
                    AccountHolderName = "John Runner",
                    AccountType = "Savings"
                },
                CancellationToken.None);

        var ok =
            Assert.IsType<OkObjectResult>(result.Result);

        var response =
            Assert.IsType<ApiResponse<object>>(ok.Value);

        var account =
            await context.BankAccounts.SingleAsync();

        Assert.True(response.Success);

        // Server must use Paystack's canonical values.
        Assert.Equal("Canonical Bank", account.BankName);
        Assert.Equal("632005", account.BankCode);
        Assert.Equal("123456", account.BranchCode);

        Assert.True(account.IsVerified);
        Assert.NotNull(account.VerifiedAt);

        Assert.Equal(1, banking.VerifyCallCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task AddBankAccount_RejectsUnknownPaystackBank()
    {
        using var context = CreateContext();

        var user = CreateUser(11);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var banking = new FakeBankingService();

        var banks = new FakePaystackBankProvider(
            new[]
            {
                new SupportedBank(
                    "632005",
                    "Canonical Bank",
                    "123456")
            });

        var controller =
            CreateController(
                context,
                user.Id,
                banking,
                banks);

        var result =
            await controller.AddBankAccount(
                new AddBankAccountRequest
                {
                    BankCode = "NOT-A-REAL-BANK",
                    BankGroupId = "NOT-A-REAL-BANK",
                    BankName = "Canonical Bank",
                    AccountNumber = "1234567890",
                    AccountHolderName = "Runner User",
                    BranchCode = "123456",
                    AccountType = "Savings"
                },
                CancellationToken.None);

        var ok =
            Assert.IsType<OkObjectResult>(result.Result);

        var response =
            Assert.IsType<ApiResponse<object>>(ok.Value);

        Assert.False(response.Success);

        Assert.Equal(
            "The selected bank is not currently available for Paystack verification or payouts.",
            response.Message);

        Assert.Empty(
            await context.BankAccounts.ToListAsync());

        // Verification must not run for an invalid bank selection.
        Assert.Equal(0, banking.VerifyCallCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task AddBankAccount_FailedVerification_RemainsUnverified()
    {
        using var context = CreateContext();

        var user = CreateUser(12);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var banking = new FakeBankingService
        {
            VerificationResult =
                new BankVerificationResult(
                    false,
                    "Bank verification is temporarily unavailable.")
        };

        var banks = new FakePaystackBankProvider(
            new[]
            {
                new SupportedBank(
                    "632005",
                    "Canonical Bank",
                    "123456")
            });

        var controller =
            CreateController(
                context,
                user.Id,
                banking,
                banks);

        var result =
            await controller.AddBankAccount(
                new AddBankAccountRequest
                {
                    BankCode = "632005",
                    BankGroupId = "632005",
                    BankName = "Fake Bank",
                    AccountNumber = "1234567890",
                    AccountHolderName = "Jane Runner",
                    BranchCode = "999999",
                    AccountType = "Savings"
                },
                CancellationToken.None);

        var ok =
            Assert.IsType<OkObjectResult>(result.Result);

        var response =
            Assert.IsType<ApiResponse<object>>(ok.Value);

        var account =
            await context.BankAccounts.SingleAsync();

        Assert.False(response.Success);
        Assert.False(account.IsVerified);
        Assert.Null(account.VerifiedAt);
        Assert.Equal("Bank verification is temporarily unavailable.", account.VerificationMessage);

        Assert.Equal(1, banking.VerifyCallCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task AddBankAccount_RequiresRecipientReferenceToMarkAccountVerified()
    {
        using var context = CreateContext();
        var user = CreateUser(18);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var banking = new FakeBankingService
        {
            VerificationResult = new BankVerificationResult(true, "Verified without recipient.")
        };
        var controller = CreateController(
            context,
            user.Id,
            banking,
            new FakePaystackBankProvider(
                new[] { new SupportedBank("632005", "Canonical Bank", "123456") }));

        var result = await controller.AddBankAccount(
            new AddBankAccountRequest
            {
                BankCode = "632005",
                AccountNumber = "1234567890",
                AccountHolderName = "Runner User",
                AccountType = "Savings"
            },
            CancellationToken.None);

        var response = Assert.IsType<ApiResponse<object>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        var account = await context.BankAccounts.SingleAsync();
        Assert.False(response.Success);
        Assert.False(account.IsVerified);
        Assert.Null(account.ProviderReference);
        Assert.Equal("Paystack did not return a payout recipient reference.", account.VerificationMessage);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetBankAccounts_OnlyMarksAccountsWithRecipientReferencesAsVerified()
    {
        using var context = CreateContext();
        var user = CreateUser(19);
        context.Users.Add(user);
        context.BankAccounts.AddRange(
            new BankAccount
            {
                UserId = user.Id,
                BankName = "Account without recipient",
                AccountNumber = "1234567890",
                AccountHolderName = "Runner User",
                AccountType = "Savings",
                IsActive = true,
                IsVerified = true
            },
            new BankAccount
            {
                UserId = user.Id,
                BankName = "Payout-ready account",
                AccountNumber = "0987654321",
                AccountHolderName = "Runner User",
                AccountType = "Savings",
                IsActive = true,
                IsVerified = true,
                ProviderReference = "RCP_TEST"
            });
        await context.SaveChangesAsync();

        var controller = CreateController(
            context,
            user.Id,
            new FakeBankingService(),
            new FakePaystackBankProvider(Array.Empty<SupportedBank>()));

        var result = await controller.GetBankAccounts(CancellationToken.None);
        var response = Assert.IsType<ApiResponse<List<object>>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(response.Data));
        var accounts = data.RootElement.EnumerateArray().ToArray();

        Assert.Equal(2, accounts.Length);
        Assert.False(accounts[0].GetProperty("isVerified").GetBoolean());
        Assert.True(accounts[1].GetProperty("isVerified").GetBoolean());
    }

    [Fact]
    public async System.Threading.Tasks.Task SetDefaultPayoutAccount_RequiresOwnedPayoutReadyAccountAndReplacesPriorDefault()
    {
        using var context = CreateContext();
        var user = CreateUser(20);
        var otherUser = CreateUser(21);
        var priorDefault = CreatePayoutReadyAccount(user.Id, "RCP_PRIOR", isDefault: true);
        var selectedAccount = CreatePayoutReadyAccount(user.Id, "RCP_SELECTED");
        var otherUsersAccount = CreatePayoutReadyAccount(otherUser.Id, "RCP_OTHER");
        var unverifiedAccount = new BankAccount
        {
            UserId = user.Id,
            BankName = "Unverified",
            AccountNumber = "1234567890",
            AccountHolderName = "Runner User",
            AccountType = "Savings",
            IsActive = true,
            IsVerified = true
        };
        context.Users.AddRange(user, otherUser);
        context.BankAccounts.AddRange(priorDefault, selectedAccount, otherUsersAccount, unverifiedAccount);
        await context.SaveChangesAsync();

        var controller = CreateController(
            context,
            user.Id,
            new FakeBankingService(),
            new FakePaystackBankProvider(Array.Empty<SupportedBank>()));

        var result = await controller.SetDefaultPayoutAccount(selectedAccount.Id, CancellationToken.None);

        var response = Assert.IsType<ApiResponse<bool>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(response.Success);
        Assert.False(priorDefault.IsDefaultPayout);
        Assert.True(selectedAccount.IsDefaultPayout);

        var accountsResult = await controller.GetBankAccounts(CancellationToken.None);
        var accountsResponse = Assert.IsType<ApiResponse<List<object>>>(
            Assert.IsType<OkObjectResult>(accountsResult.Result).Value);
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(accountsResponse.Data));
        var selectedJson = data.RootElement.EnumerateArray()
            .Single(account => account.GetProperty("id").GetInt32() == selectedAccount.Id);
        Assert.True(selectedJson.GetProperty("isDefaultPayout").GetBoolean());

        var unverifiedResult = await controller.SetDefaultPayoutAccount(unverifiedAccount.Id, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(unverifiedResult.Result);

        var otherUsersResult = await controller.SetDefaultPayoutAccount(otherUsersAccount.Id, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(otherUsersResult.Result);
    }

    [Fact]
    public async System.Threading.Tasks.Task RemoveBankAccount_DeactivatesItWithoutDeletingHistory()
    {
        using var context = CreateContext();
        var user = CreateUser(15);
        context.Users.Add(user);
        var bankAccount = new BankAccount
        {
            UserId = user.Id,
            BankName = "Canonical Bank",
            BankCode = "632005",
            AccountNumber = "1234567890",
            AccountHolderName = "Runner User",
            BranchCode = "123456",
            AccountType = "Savings"
        };
        context.BankAccounts.Add(bankAccount);
        await context.SaveChangesAsync();

        var controller = CreateController(
            context,
            user.Id,
            new FakeBankingService(),
            new FakePaystackBankProvider(Array.Empty<SupportedBank>()));

        var result = await controller.RemoveBankAccount(bankAccount.Id, CancellationToken.None);

        var response = Assert.IsType<ApiResponse<bool>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(response.Success);
        Assert.False(bankAccount.IsActive);
        Assert.Single(await context.BankAccounts.ToListAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task RemoveBankAccount_RejectsAccountWithPayoutInProgress()
    {
        using var context = CreateContext();
        var runner = CreateUser(16);
        var creator = CreateUser(17);
        context.Users.AddRange(runner, creator);
        var bankAccount = new BankAccount
        {
            UserId = runner.Id,
            BankName = "Canonical Bank",
            BankCode = "632005",
            AccountNumber = "1234567890",
            AccountHolderName = "Runner User",
            BranchCode = "123456",
            AccountType = "Savings"
        };
        var task = new DoForYou.API.Models.Task
        {
            TaskId = "DFY-ACCOUNT-REMOVE",
            TaskName = "Account removal test",
            TaskDescription = "Prevent removing an account with an active payout",
            Category = "Other",
            Area = "Pretoria",
            DateNeeded = DateTime.UtcNow.AddDays(1),
            Budget = 200m,
            PayoutAmount = 170m,
            PaymentStatus = "EscrowReleased",
            TaskStatus = "PayoutPending",
            PayoutStatus = "Processing",
            EscrowStatus = "released",
            CreatedByUserId = creator.Id,
            AcceptedByUserId = runner.Id
        };
        context.BankAccounts.Add(bankAccount);
        context.Tasks.Add(task);
        await context.SaveChangesAsync();
        context.Payouts.Add(new Payout
        {
            TaskId = task.Id,
            Task = task,
            RunnerId = runner.Id,
            BankAccountId = bankAccount.Id,
            BankAccount = bankAccount,
            Amount = 170m,
            Status = "Processing",
            Provider = "Paystack",
            MerchantReference = "DFY-PAYOUT-ACCOUNT-REMOVE"
        });
        await context.SaveChangesAsync();

        var controller = CreateController(
            context,
            runner.Id,
            new FakeBankingService(),
            new FakePaystackBankProvider(Array.Empty<SupportedBank>()));

        var result = await controller.RemoveBankAccount(bankAccount.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<bool>>(conflict.Value);
        Assert.False(response.Success);
        Assert.True(bankAccount.IsActive);
    }

    [Fact]
    public async System.Threading.Tasks.Task VerifyBankAccount_CreatesRecipientAndRetriesPriorMissingRecipientPayout()
    {
        using var context = CreateContext();

        var creator = CreateUser(13);
        var runner = CreateUser(14);
        context.Users.AddRange(creator, runner);

        var bankAccount = new BankAccount
        {
            UserId = runner.Id,
            BankName = "Canonical Bank",
            BankCode = "632005",
            AccountNumber = "1234567890",
            AccountHolderName = "Runner User",
            BranchCode = "123456",
            AccountType = "Savings",
            IsVerified = true,
            IsActive = true
        };
        context.BankAccounts.Add(bankAccount);

        var task = new DoForYou.API.Models.Task
        {
            TaskId = "DFY-RECOVER-PAYOUT",
            TaskName = "Payout retry test",
            TaskDescription = "Recover payout when recipient is added",
            Category = "Other",
            Area = "Pretoria",
            DateNeeded = DateTime.UtcNow.AddDays(1),
            Budget = 200m,
            PayoutAmount = 170m,
            PaymentStatus = "EscrowReleased",
            TaskStatus = "PayoutFailed",
            PayoutStatus = "Failed",
            EscrowStatus = "released",
            CreatedByUserId = creator.Id,
            AcceptedByUserId = runner.Id
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var payout = new Payout
        {
            TaskId = task.Id,
            Task = task,
            RunnerId = runner.Id,
            BankAccountId = bankAccount.Id,
            BankAccount = bankAccount,
            Amount = 170m,
            Status = "Failed",
            Provider = "Paystack",
            MerchantReference = "DFY-PAYOUT-DFY-RECOVER-PAYOUT",
            FailureReason = "Runner bank account is missing its Paystack recipient reference."
        };
        context.Payouts.Add(payout);
        await context.SaveChangesAsync();

        var banking = new BankingService(
            new FakePaystackBankProvider(
                new[] { new SupportedBank("632005", "Canonical Bank", "123456") }),
            context);
        var controller = CreateController(
            context,
            runner.Id,
            banking,
            new FakePaystackBankProvider(
                new[] { new SupportedBank("632005", "Canonical Bank", "123456") }));

        var result = await controller.VerifyBankAccount(bankAccount.Id, CancellationToken.None);

        var response = Assert.IsType<ApiResponse<object>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(response.Success);
        Assert.Equal("RCP_TEST", bankAccount.ProviderReference);
        Assert.Equal("Pending", payout.Status);
        Assert.Null(payout.FailureReason);
        Assert.Equal("DFY-PAYOUT-DFY-RECOVER-PAYOUT", payout.MerchantReference);
        Assert.Equal("Pending", task.PayoutStatus);
        Assert.Equal("PayoutPending", task.TaskStatus);
    }

    private static AppDbContext CreateContext()
    {
        var options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(
                    Guid.NewGuid().ToString())
                .Options;

        return new AppDbContext(options);
    }

    private static BankAccount CreatePayoutReadyAccount(int userId, string recipientReference, bool isDefault = false) =>
        new()
        {
            UserId = userId,
            BankName = "Verified Bank",
            AccountNumber = Guid.NewGuid().ToString("N")[..10],
            AccountHolderName = "Runner User",
            AccountType = "Savings",
            ProviderReference = recipientReference,
            IsActive = true,
            IsVerified = true,
            IsDefaultPayout = isDefault
        };

    private static User CreateUser(int id)
    {
        return new User
        {
            Id = id,
            FirstName = "Test",
            LastName = "Runner",
            Email = $"runner{id}@example.com",
            PhoneNumber = "0712345678",
            IdNumber = "8001015009087",
            UserType = "runner",
            ProfileCompleted = true,
            Address = "123 Test Street",
            Roles = "User",
            PasswordHash = "test-hash",
            IsVerified = true
        };
    }

    private static BankingController CreateController(
        AppDbContext context,
        int userId,
        IBankingService bankingService,
        IBankProvider bankProvider)
    {
        var controller =
            new BankingController(
                context,
                bankingService,
                bankProvider);

        controller.ControllerContext =
            new ControllerContext
            {
                HttpContext =
                    new DefaultHttpContext
                    {
                        User =
                            new ClaimsPrincipal(
                                new ClaimsIdentity(
                                    new[]
                                    {
                                        new Claim(
                                            ClaimTypes.NameIdentifier,
                                            userId.ToString())
                                    },
                                    "TestAuth"))
                    }
            };

        return controller;
    }

    private sealed class FakeBankingService : IBankingService
    {
        public BankVerificationResult VerificationResult { get; set; } =
            new BankVerificationResult(
                false,
                "Verification failed.");

        public int VerifyCallCount { get; private set; }

        public System.Threading.Tasks.Task<bool>
            ValidateBankAccountAsync(
                string bankName,
                string accountNumber,
                string branchCode)
        {
            return System.Threading.Tasks.Task.FromResult(
                !string.IsNullOrWhiteSpace(bankName) &&
                accountNumber.Length == 10 &&
                branchCode.Length == 6);
        }

        public System.Threading.Tasks.Task<BankVerificationResult>
            VerifyBankAccountAsync(
                User user,
                BankAccount bankAccount,
                CancellationToken cancellationToken = default)
        {
            VerifyCallCount++;

            return System.Threading.Tasks.Task.FromResult(
                VerificationResult);
        }
    }

    private sealed class FakePaystackBankProvider : IBankProvider
    {
        private readonly IReadOnlyList<SupportedBank> _banks;

        public FakePaystackBankProvider(
            IReadOnlyList<SupportedBank> banks)
        {
            _banks = banks;
        }

        public System.Threading.Tasks.Task<IReadOnlyList<SupportedBank>>
            GetSupportedBanksAsync(
                CancellationToken cancellationToken = default)
        {
            return System.Threading.Tasks.Task.FromResult(_banks);
        }

        public System.Threading.Tasks.Task<BankVerificationResultV2>
            VerifyBankAccountAsync(
                User user,
                BankAccount bankAccount,
                CancellationToken cancellationToken = default)
        {
            return System.Threading.Tasks.Task.FromResult(
                new BankVerificationResultV2(true, "Bank account verified.", "RCP_TEST"));
        }
    }
}
