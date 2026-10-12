using System.Net;
using System.Text;
using Task = System.Threading.Tasks.Task;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DoForYou.API.Models;
using DoForYou.API.Services;

namespace DoForYou.Tests;

public class PaystackProviderBankVerificationTests
{
    [Fact]
    public async Task VerifyBankAccount_IncludesPaystackDiagnosticFlagsWhenRefuted()
    {
        var handler = new QueueHttpMessageHandler(
            JsonResponse("""
            {
              "status": true,
              "message": "Personal Account Verification attempted",
              "data": {
                "accountAcceptsDebits": true,
                "accountAcceptsCredits": true,
                "accountHolderMatch": false,
                "accountOpenForMoreThanThreeMonths": true,
                "accountOpen": true,
                "verified": false,
                "verificationMessage": "Account holder details do not match"
              }
            }
            """));

        var provider = CreateProvider(handler);
        var result = await provider.VerifyBankAccountAsync(
            CreateUser(),
            CreateBankAccount(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Account holder details do not match", result.Message);
        Assert.Contains("account holder match: no", result.Message);
        Assert.Contains("receives credits: yes", result.Message);
        Assert.Contains("account open: yes", result.Message);
        Assert.Contains("open > 3 months: yes", result.Message);
    }

    [Fact]
    public async Task VerifyBankAccount_CreatesBasaRecipientAfterSuccessfulValidation()
    {
        var handler = new QueueHttpMessageHandler(
            JsonResponse("""
            {
              "status": true,
              "message": "Personal Account Verification attempted",
              "data": {
                "accountAcceptsDebits": true,
                "accountAcceptsCredits": true,
                "accountHolderMatch": true,
                "accountOpenForMoreThanThreeMonths": true,
                "accountOpen": true,
                "verified": true,
                "verificationMessage": "Account is verified successfully"
              }
            }
            """),
            JsonResponse("""
            {
              "status": true,
              "message": "Transfer recipient created successfully",
              "data": {
                "active": true,
                "recipient_code": "RCP_TEST_PERSONAL_ZA_123456"
              }
            }
            """));

        var provider = CreateProvider(handler);
        var result = await provider.VerifyBankAccountAsync(
            CreateUser(),
            CreateBankAccount(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("RCP_TEST_PERSONAL_ZA_123456", result.ProviderReference);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("/bank/validate", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Contains("/transferrecipient", handler.Requests[1].Request.RequestUri!.AbsolutePath);

        var recipientBody = handler.Requests[1].Body;
        Assert.Contains("\"type\":\"basa\"", recipientBody);
        Assert.Contains("\"currency\":\"ZAR\"", recipientBody);
    }

    [Fact]
    public async Task GetSupportedBanks_OnlyReturnsBanksSupportingPersonalValidation()
    {
        var handler = new QueueHttpMessageHandler(
            JsonResponse("""
            {
              "status": true,
              "message": "Banks retrieved",
              "data": [
                {
                  "code": "679000",
                  "name": "Discovery Bank Limited",
                  "branch_code": "679000",
                  "supported_types": ["business", "personal"]
                },
                {
                  "code": "999999",
                  "name": "Business Only Bank",
                  "branch_code": "999999",
                  "supported_types": ["business"]
                }
              ]
            }
            """));

        var provider = CreateProvider(handler);
        var banks = await provider.GetSupportedBanksAsync(CancellationToken.None);

        var bank = Assert.Single(banks);
        Assert.Equal("679000", bank.Code);
        Assert.Equal("Discovery Bank Limited", bank.Name);
    }

    private static PaystackProvider CreateProvider(QueueHttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        var factory = new TestHttpClientFactory(client);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Paystack:SecretKey"] = "sk_test_unit",
                ["Paystack:BaseUrl"] = "https://api.paystack.co"
            })
            .Build();
        var logger = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.None))
            .CreateLogger<PaystackProvider>();

        return new PaystackProvider(factory, configuration, logger);
    }

    private static User CreateUser() => new()
    {
        Id = 7,
        FirstName = "Co",
        LastName = "Mokolare",
        IdNumber = "9001015800082",
        Email = "runner@example.com"
    };

    private static BankAccount CreateBankAccount() => new()
    {
        Id = 8,
        UserId = 7,
        BankCode = "679000",
        BankGroupId = "679000",
        BankName = "Discovery Bank Limited",
        AccountNumber = "1234567890",
        AccountHolderName = "Co Mokolare",
        AccountType = "personal",
        IsActive = true
    };

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class QueueHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));

            if (_responses.Count == 0)
                throw new InvalidOperationException("No queued HTTP response remains.");

            return _responses.Dequeue();
        }
    }
}
