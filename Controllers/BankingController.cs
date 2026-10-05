using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using System.Security.Claims;

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
    public async Task<ActionResult<ApiResponse<object>>> AddBankAccount([FromBody] AddBankAccountRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var bankGroupId = request.Code.Trim();
        var bankName = request.BankName.Trim();
        var accountNumber = request.AccountNumber.Trim();
        var accountHolderName = request.AccountHolderName.Trim();
        var branchCode = request.BranchCode.Trim();
        var accountType = request.AccountType.Trim();

        if (string.IsNullOrWhiteSpace(bankGroupId))
        {
            return Ok(new ApiResponse<object>
            {
                Success = false,
                Message = "A valid bank selection is required."
            });
        }

        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == userId.Value);

        if (user == null)
            return Unauthorized();

        var availableBanks =
            await _bankProvider.GetSupportedBanksAsync(
                HttpContext.RequestAborted);

        var selectedBank =
            availableBanks.FirstOrDefault(bank =>
                string.Equals(
                    bank.Code,
                    bankGroupId,
                    StringComparison.OrdinalIgnoreCase));

        if (selectedBank == null)
        {
            return Ok(new ApiResponse<object>
            {
                Success = false,
                Message = "The selected bank is not currently available for payouts."
            });
        }

        // The provider's bank code is authoritative. Never trust display values supplied by the client.
        bankName = selectedBank.Name;
        branchCode = selectedBank.UniversalBranchCode;

        var isValid = await _bankingService.ValidateBankAccountAsync(
            bankName,
            accountNumber,
            branchCode);

        if (!isValid)
        {
            return Ok(new ApiResponse<object>
            {
                Success = false,
                Message = "Invalid bank account details."
            });
        }

        var exists = await _context.BankAccounts
            .AnyAsync(ba =>
                ba.UserId == userId &&
                ba.AccountNumber == accountNumber &&
                ba.IsActive);

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
            Code = selectedBank.Code,
            AccountNumber = accountNumber,
            AccountHolderName = accountHolderName,
            BranchCode = branchCode,
            AccountType = accountType,
            IsVerified = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.BankAccounts.Add(bankAccount);
        await _context.SaveChangesAsync();

        var verification =
            await _bankingService.VerifyBankAccountAsync(
                user,
                bankAccount,
                HttpContext.RequestAborted);

        if (!verification.Success)
        {
            bankAccount.IsVerified = false;
            bankAccount.VerifiedAt = null;

            await _context.SaveChangesAsync();

            return Ok(new ApiResponse<object>
            {
                Success = false,
                Data = new
                {
                    bankAccountId = bankAccount.Id,
                    isVerified = false
                },
                Message = verification.Message
            });
        }

        bankAccount.IsVerified = true;
        bankAccount.VerifiedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

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
    public async Task<ActionResult<ApiResponse<List<object>>>> GetBankAccounts()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var accounts = await _context.BankAccounts
            .Where(ba => ba.UserId == userId && ba.IsActive)
            .Select(ba => new
            {
                id = ba.Id,
                bankName = ba.BankName,
                accountNumber = MaskAccountNumber(ba.AccountNumber),
                accountHolderName = ba.AccountHolderName,
                accountType = ba.AccountType,
                isVerified = ba.IsVerified,
                createdAt = ba.CreatedAt
            })
            .Cast<object>()
            .ToListAsync();

        return Ok(new ApiResponse<List<object>>
        {
            Success = true,
            Data = accounts
        });
    }


    [HttpPost("withdraw")]
    public ActionResult<ApiResponse<object>> Retiredwithdraw()
    {
        return StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawals have been retired. Completed task payouts are sent directly to the verified runner bank account."
        });
    }



    [HttpPost("verify-withdrawal")]
    public ActionResult<ApiResponse<object>> Retiredverifywithdrawal()
    {
        return StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawal OTP verification has been retired."
        });
    }



    [HttpGet("withdrawals")]
    public ActionResult<ApiResponse<object>> Retiredwithdrawals()
    {
        return StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawal history has been retired."
        });
    }



    [HttpGet("withdrawal-fee")]
    public ActionResult<ApiResponse<object>> Retiredwithdrawalfee()
    {
        return StatusCode(410, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawal fees are no longer applicable."
        });
    }


    [HttpPost("bank-accounts/{id:int}/verify")]
    public async Task<ActionResult<ApiResponse<object>>> VerifyBankAccount(
        int id,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var bankAccount = await _context.BankAccounts
            .FirstOrDefaultAsync(
                ba => ba.Id == id && ba.UserId == userId.Value,
                cancellationToken);

        if (bankAccount == null)
        {
            return NotFound(new ApiResponse<object>
            {
                Success = false,
                Message = "Bank account not found."
            });
        }

        if (!bankAccount.IsActive)
        {
            return Ok(new ApiResponse<object>
            {
                Success = false,
                Message = "This bank account is inactive."
            });
        }

        var user = await _context.Users
            .FirstOrDefaultAsync(
                u => u.Id == userId.Value,
                cancellationToken);

        if (user == null)
            return Unauthorized();

        var verification =
            await _bankingService.VerifyBankAccountAsync(
                user,
                bankAccount,
                cancellationToken);

        if (!verification.Success)
        {
            bankAccount.IsVerified = false;
            bankAccount.VerifiedAt = null;
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(new ApiResponse<object>
            {
                Success = false,
                Data = new
                {
                    bankAccountId = bankAccount.Id,
                    isVerified = false
                },
                Message = verification.Message
            });
        }

        bankAccount.IsVerified = true;
        bankAccount.VerifiedAt = DateTime.UtcNow;

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

    [HttpGet("banks")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetSupportedBanks(
        CancellationToken cancellationToken)
    {
        var banks =
            await _bankProvider.GetAvailableBanksAsync(
                cancellationToken);

        if (banks.Count == 0)
        {
            return StatusCode(503, new ApiResponse<List<object>>
            {
                Success = false,
                Message = "Bank list is temporarily unavailable."
            });
        }

        var data =
            banks.Select(bank => new
            {
                bankGroupId = bank.Code,
                name = bank.Name,
                branchCode = bank.UniversalBranchCode
            }).ToList<object>();

        return Ok(new ApiResponse<List<object>>
        {
            Success = true,
            Data = data
        });
    }

    private int? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    private string MaskAccountNumber(string accountNumber)
    {
        if (accountNumber.Length <= 4) return accountNumber;
        return "****" + accountNumber.Substring(accountNumber.Length - 4);
    }
}

public class AddBankAccountRequest
{
    public string Code { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountHolderName { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string AccountType { get; set; } = "Savings";
}
