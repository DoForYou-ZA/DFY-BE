using System;
using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261016190000_AddRegistrationConsent")]
    public partial class AddRegistrationConsent : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(name: "TermsAcceptedAt", table: "Users", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<string>(name: "TermsVersion", table: "Users", type: "text", nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "PrivacyAcceptedAt", table: "Users", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<string>(name: "PrivacyVersion", table: "Users", type: "text", nullable: true);
            migrationBuilder.AddColumn<bool>(name: "MarketingConsent", table: "Users", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<DateTime>(name: "MarketingConsentUpdatedAt", table: "Users", type: "timestamp with time zone", nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "TermsAcceptedAt", table: "Users");
            migrationBuilder.DropColumn(name: "TermsVersion", table: "Users");
            migrationBuilder.DropColumn(name: "PrivacyAcceptedAt", table: "Users");
            migrationBuilder.DropColumn(name: "PrivacyVersion", table: "Users");
            migrationBuilder.DropColumn(name: "MarketingConsent", table: "Users");
            migrationBuilder.DropColumn(name: "MarketingConsentUpdatedAt", table: "Users");
        }
    }
}