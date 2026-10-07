using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultPayoutBankAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDefaultPayout",
                table: "BankAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDefaultPayout",
                table: "BankAccounts");
        }
    }
}
