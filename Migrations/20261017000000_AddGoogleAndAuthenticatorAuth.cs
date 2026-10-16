using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261017000000_AddGoogleAndAuthenticatorAuth")]
    public partial class AddGoogleAndAuthenticatorAuth : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleSubject",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthenticatorSecretEncrypted",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingAuthenticatorSecretEncrypted",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthenticatorRecoveryCodeHashes",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AuthenticatorEnrolledAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastTwoFactorVerifiedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(@"UPDATE ""Users""
SET ""NormalizedEmail"" = LOWER(BTRIM(""Email""));");

            // Never delete or merge duplicate user identities automatically. Abort the
            // migration with a clear message so staging duplicates can be investigated.
            migrationBuilder.Sql(@"DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM ""Users""
                    GROUP BY ""NormalizedEmail""
                    HAVING COUNT(*) > 1
                ) THEN
                    RAISE EXCEPTION 'Duplicate normalized email addresses exist in Users. Resolve them before applying the authentication migration.';
                END IF;
            END $$;");

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedEmail",
                table: "Users",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedEmail",
                table: "Users",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_GoogleSubject",
                table: "Users",
                column: "GoogleSubject",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_GoogleSubject",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_NormalizedEmail",
                table: "Users");

            migrationBuilder.DropColumn(name: "NormalizedEmail", table: "Users");
            migrationBuilder.DropColumn(name: "GoogleSubject", table: "Users");
            migrationBuilder.DropColumn(name: "AuthenticatorSecretEncrypted", table: "Users");
            migrationBuilder.DropColumn(name: "PendingAuthenticatorSecretEncrypted", table: "Users");
            migrationBuilder.DropColumn(name: "AuthenticatorRecoveryCodeHashes", table: "Users");
            migrationBuilder.DropColumn(name: "AuthenticatorEnrolledAt", table: "Users");
            migrationBuilder.DropColumn(name: "LastTwoFactorVerifiedAt", table: "Users");
        }
    }
}
