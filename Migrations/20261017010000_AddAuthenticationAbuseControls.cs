using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261017010000_AddAuthenticationAbuseControls")]
    public partial class AddAuthenticationAbuseControls : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PasswordResetSendWindowStartAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PasswordResetSendCount",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PendingAuthenticatorSecretExpiry",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailedLoginAttempts",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "FailedLoginWindowStartAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LoginLockoutUntil",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "PasswordResetSendWindowStartAt", table: "Users");
            migrationBuilder.DropColumn(name: "PasswordResetSendCount", table: "Users");
            migrationBuilder.DropColumn(name: "PendingAuthenticatorSecretExpiry", table: "Users");
            migrationBuilder.DropColumn(name: "FailedLoginAttempts", table: "Users");
            migrationBuilder.DropColumn(name: "FailedLoginWindowStartAt", table: "Users");
            migrationBuilder.DropColumn(name: "LoginLockoutUntil", table: "Users");
        }
    }
}
