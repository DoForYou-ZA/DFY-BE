using System.Net;
using System.Text;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DoForYou.Tests;

public class PaystackSettlementReconciliationTests
{
    [Fact]
    public async Task GetSettlementsAsync_ConvertsMinorUnitsAndReadsSettlementTransactions()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/settlement", StringComparison.Ordinal))
            {
                Assert.Contains("from=", request.RequestUri.Query);
                Assert.Contains("to=", request.RequestUri.Query);
                return JsonResponse("""
                    {
                      "status": true,
                      "message": "Settlements retrieved",
                      "data": [{
                        "id": 42,
                        "domain": "test",
                        "status": "success",
                        "currency": "ZAR",
                        "total_amount": 19300,
                        "effective_amount": 19300,
                        "total_fees": 700,
                        "total_processed": 20000,
                        "settlement_date": "2026-10-12T00:00:00.000Z"
                      }],
                      "meta": { "pageCount": 1 }
                    }
                    """);
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/settlement/42/transactions", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {
                      "status": true,
                      "message": "Transactions retrieved",
                      "data": [{
                        "id": 123456,
                        "reference": "dfy-pay-dfy-task-001",
                        "status": "success",
                        "currency": "ZAR",
                        "amount": 20000,
                        "fees": 680,
                        "paid_at": "2026-10-11T10:00:00.000Z"
                      }],
                      "meta": { "pageCount": 1 }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = CreateProvider(handler);
        var result = await provider.GetSettlementsAsync(
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-10-14T00:00:00Z"),
            25);

        Assert.True(result.Success);
        Assert.True(result.IsComplete);
        var settlement = Assert.Single(result.Settlements);
        Assert.Equal("42", settlement.Id);
        Assert.Equal("test", settlement.Domain);
        Assert.Equal(200m, settlement.TotalProcessed);
        Assert.Equal(193m, settlement.TotalAmount);
        Assert.Equal(193m, settlement.EffectiveAmount);
        Assert.Equal(7m, settlement.TotalFees);

        var transaction = Assert.Single(result.Transactions);
        Assert.Equal("dfy-pay-dfy-task-001", transaction.Reference);
        Assert.Equal(200m, transaction.Amount);
        Assert.Equal(6.8m, transaction.Fees);
        Assert.Equal("ZAR", transaction.Currency);
    }

    [Fact]
    public async Task GetSettlementsAsync_ReportsProviderFailureWithoutClaimingCompleteResults()
    {
        var provider = CreateProvider(new StubHandler(_ =>
            JsonResponse("""{"status":false,"message":"Invalid key","data":[]}""", HttpStatusCode.Unauthorized)));

        var result = await provider.GetSettlementsAsync(
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-10-14T00:00:00Z"));

        Assert.False(result.Success);
        Assert.False(result.IsComplete);
        Assert.Empty(result.Settlements);
        Assert.Contains("Invalid key", result.Error);
    }

    [Fact]
    public async Task GetSettlementsAsync_WithoutSecretDoesNotCallProvider()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Provider must not be called."));
        var provider = new PaystackProvider(
            new StubClientFactory(handler),
            new ConfigurationBuilder().Build(),
            NullLogger<PaystackProvider>.Instance,
            new HttpContextAccessor());

        var result = await provider.GetSettlementsAsync(
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-10-14T00:00:00Z"));

        Assert.False(result.Success);
        Assert.False(result.IsComplete);
        Assert.Contains("not configured", result.Error);
    }

    private static PaystackProvider CreateProvider(StubHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Paystack:SecretKey"] = "sk_test_unit_test_only",
                ["Paystack:BaseUrl"] = "https://api.paystack.co"
            })
            .Build();

        return new PaystackProvider(
            new StubClientFactory(handler),
            configuration,
            NullLogger<PaystackProvider>.Instance,
            new HttpContextAccessor());
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
