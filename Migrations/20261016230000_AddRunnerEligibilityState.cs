using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261016230000_AddRunnerEligibilityState")]
    public partial class AddRunnerEligibilityState : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AuthenticatorTwoFactorEnabled",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "IdentityVerificationStatus",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "not_started");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthenticatorTwoFactorEnabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IdentityVerificationStatus",
                table: "Users");
        }
    }
}
