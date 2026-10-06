using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations;

public partial class AddPaystackProviderFields : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Provider",
            table: "Payments",
            type: "text",
            nullable: false,
            defaultValue: "Paystack");

        migrationBuilder.AddColumn<string>(
            name: "BankCode",
            table: "BankAccounts",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProviderReference",
            table: "BankAccounts",
            type: "text",
            nullable: true);

        migrationBuilder.Sql(
            @"UPDATE ""Payments""
              SET ""Provider"" = 'Paystack'
              WHERE ""Provider"" IS NULL OR ""Provider"" = '';");

        // Existing Ozow payout records are deliberately left unchanged.
        // Only newly created payouts use Paystack, avoiding accidental resubmission
        // of historical provider transactions.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Provider",
            table: "Payments");

        migrationBuilder.DropColumn(
            name: "BankCode",
            table: "BankAccounts");

        migrationBuilder.DropColumn(
            name: "ProviderReference",
            table: "BankAccounts");
    }
}
