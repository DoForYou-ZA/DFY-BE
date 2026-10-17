using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261017060000_AddSumsubIdentityVerification")]
    public partial class AddSumsubIdentityVerification : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdentityVerificationProvider",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityVerificationExternalUserId",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityVerificationApplicantId",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IdentityVerificationStartedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IdentityVerificationUpdatedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IdentityVerificationLastEventAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityVerificationHandoffTokenHash",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IdentityVerificationHandoffExpiry",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IdentityVerificationAttemptWindowStartAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdentityVerificationAttemptCount",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "IdentityVerificationEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    EventHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderApplicantId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    EventType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ResultStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityVerificationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityVerificationEvents_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityVerificationEvents_Provider_EventHash",
                table: "IdentityVerificationEvents",
                columns: new[] { "Provider", "EventHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityVerificationEvents_UserId_ReceivedAt",
                table: "IdentityVerificationEvents",
                columns: new[] { "UserId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_IdentityVerificationExternalUserId",
                table: "Users",
                column: "IdentityVerificationExternalUserId",
                unique: true,
                filter: "\"IdentityVerificationExternalUserId\" IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_IdentityVerificationExternalUserId",
                table: "Users");

            migrationBuilder.DropTable(name: "IdentityVerificationEvents");

            migrationBuilder.DropColumn(name: "IdentityVerificationProvider", table: "Users");
            migrationBuilder.DropColumn(name: "IdentityVerificationExternalUserId", table: "Users");
            migrationBuilder.DropColumn(name: "IdentityVerificationApplicantId", table: "Users");
            migrationBuilder.DropColumn(name: "IdentityVerificationStartedAt", table: "Users");
            migrationBuilder.DropColumn(name: "IdentityVerificationUpdatedAt", table: "Users");
            migrationBuilder.DropColumn(name: "IdentityVerificationLastEventAt", table: "Users");
            migrationBuilder.DropColumn(name: "IdentityVerificationHandoffTokenHash", table: "Users");
            migrationBuilder.DropColumn(name: "IdentityVerificationHandoffExpiry", table: "Users");
            migrationBuilder.DropColumn(name: "IdentityVerificationAttemptWindowStartAt", table: "Users");
            migrationBuilder.DropColumn(name: "IdentityVerificationAttemptCount", table: "Users");
        }
    }
}
