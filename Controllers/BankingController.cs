using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/banking")]
[Authorize]
public class BankingController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IBankingService _bankingService;
    private readonly IBankProvider _bankProvider;

    public BankingController(
        AppDbContext context,
        IBankingService bankingService,
        IBankProvider bankProvider)
    {
        _context = context;
        _bankingService = bankingService;
        _bankProvider = bankProvider;
    }

    [HttpPost("bank-accounts")]
    [HttpPost("accounts")]
    public async Task<ActionResult<ApiResponse<object>>> AddBankAccount(
        [FromBody] AddBankAccountRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var user = await _context.Users.FirstOrDefaultAsync(
            u => u.Id == userId.Value,
            cancellationToken);

        if (user == null)
            return Unauthorized();

        var bankCode = !string.IsNullOrWhiteSpace(request.BankCode)
            ? request.BankCode.Trim()
            : request.BankGroupId.Trim(); // Backward-compatible frontend field.

        if (string.IsNullOrWhiteSpace(bankCode))
        {
            return Ok(new ApiResponse<object>
            {
                Success = false,
                Message = "A valid Paystack bank selection is required."
            });
        }

        var banks = await _bankProvider.GetSupportedBanksAsync(cancellationToken);
        var selectedBank = banks.FirstOrDefault(b =>
            string.Equals(b.Code, bankCode, StringComparison.OrdinalIgnoreCase));

        if (selectedBank == null)
        {
            return Ok(new ApiResponse<object>
            {
                Success = false,
                Message = "The selected bank is not currently available for Paystack verification or payouts."
            });
        }

        if (string.IsNullOrWhiteSpace(request.AccountNumber) ||
            request.AccountNumber.Length < 9 ||
            request.AccountNumber.Length > 11 ||
            !request.AccountNumber.All(char.IsDigit))
        {
            return Ok(new ApiResponse<object>
            {
                Success = false,
                Message = "Invalid bank account number."
            });
        }

        if (string.IsNullOrWhiteSpace(request.AccountHolderName))
        {
            return Ok(new ApiResponse<object>
            {
                Success = false,
                Message = "Account holder name is required."
            });
        }

        var normalizedAccountNumber = request.AccountNumber.Trim();
        var exists = await _context.BankAccounts.AnyAsync(
            ba => ba.UserId == userId.Value &&
                  ba.AccountNumber == normalizedAccountNumber &&
                  ba.IsActive,
            cancellationToken);

        if (exists)
        {
            return Ok(new ApiResponse<object>
            {
                Success = false,
                Message = "Bank account already exists."
            });
        }

        var bankAccount = new BankAccount
        {
            UserId = userId.Value,
            BankName = selectedBank.Name,
            BankCode = selectedBank.Code,
            BankGroupId = selectedBank.Code, // Legacy compatibility only.
            AccountNumber = normalizedAccountNumber,
            AccountHolderName = request.AccountHolderName.Trim(),
            BranchCode = selectedBank.BranchCode ?? request.BranchCode?.Trim() ?? string.Empty,
            AccountType = request.AccountType.Trim(),
            IsVerified = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.BankAccounts.Add(bankAccount);
        await _context.SaveChangesAsync(cancellationToken);

        var verification = await _bankingService.VerifyBankAccountAsync(
            user,
            bankAccount,
            cancellationToken);

        var verified = verification.Success &&
                       !string.IsNullOrWhiteSpace(verification.ProviderReference);
        if (!verified)
        {
            bankAccount.IsVerified = false;
            bankAccount.VerifiedAt = null;
            bankAccount.VerificationMessage = verification.Success
                ? "Paystack did not return a payout recipient reference."
                : verification.Message;
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(new ApiResponse<object>
            {
                Success = false,
                Data = new
                {
                    bankAccountId = bankAccount.Id,
                    isVerified = false,
                    verificationMessage = bankAccount.VerificationMessage
                },
                Message = bankAccount.VerificationMessage
            });
        }

        bankAccount.IsVerified = true;
        bankAccount.VerifiedAt = DateTime.UtcNow;
        bankAccount.ProviderReference = verification.ProviderReference;
        bankAccount.VerificationMessage = null;

        await _context.SaveChangesAsync(cancellationToken);

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                bankAccountId = bankAccount.Id,
                isVerified = true,
                verifiedAt = bankAccount.VerifiedAt
            },
            Message = verification.Message
        });
    }

    [HttpGet("bank-accounts")]
    [HttpGet("accounts")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetBankAccounts(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var accounts = await _context.BankAccounts
            .Where(ba => ba.UserId == userId && ba.IsActive)
            .Select(ba => new
            {
                id = ba.Id,
                bankName = ba.BankName,
                bankCode = ba.BankCode ?? ba.BankGroupId,
                accountNumber = MaskAccountNumber(ba.AccountNumber),
                accountHolderName = ba.AccountHolderName,
                accountType = ba.AccountType,
                isVerified = ba.IsVerified && !string.IsNullOrWhiteSpace(ba.ProviderReference),
                verificationMessage = ba.VerificationMessage,
                createdAt = ba.CreatedAt
            })
            .Cast<object>()
            .ToListAsync(cancellationToken);

        return Ok(new ApiResponse<List<object>>
        {
            Success = true,
            Data = accounts
        });
    }

    [HttpPost("withdraw")]
    public ActionResult<ApiResponse<object>> RetiredWithdraw() =>
        StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawals have been retired. Completed task payouts are sent directly to the verified runner bank account."
        });

    [HttpPost("verify-withdrawal")]
    public ActionResult<ApiResponse<object>> RetiredVerifyWithdrawal() =>
        StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawal OTP verification has been retired."
        });

    [HttpGet("withdrawals")]
    public ActionResult<ApiResponse<object>> RetiredWithdrawals() =>
        StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawal history has been retired."
        });

    [HttpGet("withdrawal-fee")]
    public ActionResult<ApiResponse<object>> RetiredWithdrawalFee() =>
        StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawal fees are no longer applicable."
        });

    [HttpPost("bank-accounts/{id:int}/verify")]
    public async Task<ActionResult<ApiResponse<object>>> VerifyBankAccount(
        int id,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var bankAccount = await _context.BankAccounts.FirstOrDefaultAsync(
            ba => ba.Id == id && ba.UserId == userId.Value,
            cancellationToken);

        if (bankAccount == null)
            return NotFound(new ApiResponse<object>
            {
                Success = false,
                Message = "Bank account not found."
            });

        var user = await _context.Users.FirstOrDefaultAsync(
            u => u.Id == userId.Value,
            cancellationToken);

        if (user == null)
            return Unauthorized();

        var verification = await _bankingService.VerifyBankAccountAsync(
            user,
            bankAccount,
            cancellationToken);

        var verified = verification.Success &&
                       !string.IsNullOrWhiteSpace(verification.ProviderReference);
        if (verified)
        {
            bankAccount.IsVerified = true;
            bankAccount.VerifiedAt = DateTime.UtcNow;
            bankAccount.ProviderReference = verification.ProviderReference;
            bankAccount.VerificationMessage = null;
        }
        else
        {
            bankAccount.VerificationMessage = verification.Success
                ? "Paystack did not return a payout recipient reference."
                : verification.Message;

            if (string.IsNullOrWhiteSpace(bankAccount.ProviderReference))
            {
                bankAccount.IsVerified = false;
                bankAccount.VerifiedAt = null;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return Ok(new ApiResponse<object>
        {
            Success = verified,
            Data = new
            {
                bankAccountId = bankAccount.Id,
                isVerified = bankAccount.IsVerified &&
                             !string.IsNullOrWhiteSpace(bankAccount.ProviderReference),
                verifiedAt = bankAccount.VerifiedAt,
                verificationMessage = bankAccount.VerificationMessage
            },
            Message = verified ? verification.Message : bankAccount.VerificationMessage
        });
    }

    [HttpDelete("bank-accounts/{id:int}")]
    public async Task<ActionResult<ApiResponse<bool>>> RemoveBankAccount(
        int id,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var bankAccount = await _context.BankAccounts.FirstOrDefaultAsync(
            ba => ba.Id == id && ba.UserId == userId.Value && ba.IsActive,
            cancellationToken);

        if (bankAccount == null)
            return NotFound(new ApiResponse<bool>
            {
                Success = false,
                Message = "Active bank account not found."
            });

        var inFlightStatuses = new[]
        {
            "Pending",
            "Processing",
            "AwaitingFunds",
            "AwaitingBankDetails"
        };
        var hasInFlightPayout = await _context.Payouts.AnyAsync(
            payout => payout.BankAccountId == id &&
                      inFlightStatuses.Contains(payout.Status),
            cancellationToken);

        if (hasInFlightPayout)
            return Conflict(new ApiResponse<bool>
            {
                Success = false,
                Message = "This account is linked to a payout in progress and cannot be removed yet."
            });

        bankAccount.IsActive = false;
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Bank account removed from your payout settings."
        });
    }

    [HttpGet("banks")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetSupportedBanks(
        CancellationToken cancellationToken)
    {
        var banks = await _bankProvider.GetSupportedBanksAsync(cancellationToken);

        if (banks.Count == 0)
        {
            return StatusCode(503, new ApiResponse<List<object>>
            {
                Success = false,
                Message = "Paystack bank list is temporarily unavailable."
            });
        }

        var data = banks.Select(bank => new
        {
            bankCode = bank.Code,
            // Keep bankGroupId as a compatibility alias while the frontend migrates.
            bankGroupId = bank.Code,
            name = bank.Name,
            branchCode = bank.BranchCode
        }).Cast<object>().ToList();

        return Ok(new ApiResponse<List<object>>
        {
            Success = true,
            Data = data
        });
    }

    private int? GetCurrentUserId()
    {
        var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(value, out var id) ? id : null;
    }

    private static string MaskAccountNumber(string accountNumber) =>
        accountNumber.Length <= 4
            ? accountNumber
            : "****" + accountNumber[^4..];
}

public class AddBankAccountRequest
{
    public string BankCode { get; set; } = string.Empty;
    public string BankGroupId { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountHolderName { get; set; } = string.Empty;
    public string? BranchCode { get; set; }
    public string AccountType { get; set; } = "Savings";
}
