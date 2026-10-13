using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261013120000_AddUserArchival")]
public partial class AddUserArchival : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsArchived",
            table: "Users",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime>(
            name: "ArchivedAt",
            table: "Users",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ArchivedReason",
            table: "Users",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ArchivedAt", table: "Users");
        migrationBuilder.DropColumn(name: "ArchivedReason", table: "Users");
        migrationBuilder.DropColumn(name: "IsArchived", table: "Users");
    }
}
