using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261017020000_AddUserConsentHistory")]
    public partial class AddUserConsentHistory : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MarketingConsentVersion",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserConsentEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    ConsentType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Accepted = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserConsentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserConsentEvents_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserConsentEvents_UserId_ConsentType_RecordedAt",
                table: "UserConsentEvents",
                columns: new[] { "UserId", "ConsentType", "RecordedAt" });

            // Preserve the consent evidence already captured in user columns. This is a
            // one-time backfill, not an inference that unrecorded historical consent exists.
            migrationBuilder.Sql(@"
                INSERT INTO ""UserConsentEvents"" (""UserId"", ""ConsentType"", ""Accepted"", ""Version"", ""RecordedAt"", ""Source"")
                SELECT ""Id"", 'terms', TRUE, ""TermsVersion"", ""TermsAcceptedAt"", 'migration-backfill'
                FROM ""Users""
                WHERE ""TermsAcceptedAt"" IS NOT NULL AND NULLIF(BTRIM(""TermsVersion""), '') IS NOT NULL;

                INSERT INTO ""UserConsentEvents"" (""UserId"", ""ConsentType"", ""Accepted"", ""Version"", ""RecordedAt"", ""Source"")
                SELECT ""Id"", 'privacy', TRUE, ""PrivacyVersion"", ""PrivacyAcceptedAt"", 'migration-backfill'
                FROM ""Users""
                WHERE ""PrivacyAcceptedAt"" IS NOT NULL AND NULLIF(BTRIM(""PrivacyVersion""), '') IS NOT NULL;

                UPDATE ""Users""
                SET ""MarketingConsentVersion"" = 'marketing-v1',
                    ""MarketingConsentUpdatedAt"" = COALESCE(""MarketingConsentUpdatedAt"", ""CreatedAt"");

                INSERT INTO ""UserConsentEvents"" (""UserId"", ""ConsentType"", ""Accepted"", ""Version"", ""RecordedAt"", ""Source"")
                SELECT ""Id"", 'marketing', ""MarketingConsent"", 'marketing-v1',
                       COALESCE(""MarketingConsentUpdatedAt"", ""CreatedAt""), 'migration-backfill'
                FROM ""Users"";
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "UserConsentEvents");
            migrationBuilder.DropColumn(name: "MarketingConsentVersion", table: "Users");
        }
    }
}
