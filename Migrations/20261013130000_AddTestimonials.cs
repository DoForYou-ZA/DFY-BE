using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261013130000_AddTestimonials")]
public partial class AddTestimonials : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Testimonials",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Role = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Text = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: false),
                IsPublished = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Testimonials", x => x.Id));
        migrationBuilder.CreateIndex(name: "IX_Testimonials_IsPublished_SortOrder", table: "Testimonials", columns: new[] { "IsPublished", "SortOrder" });
        migrationBuilder.InsertData(
            table: "Testimonials",
            columns: new[] { "Name", "Role", "Text", "IsPublished", "SortOrder", "CreatedAt", "UpdatedAt" },
            values: new object[,]
            {
                { "Thabo M.", "Task Creator", "DoForYou helped me find someone to handle my grocery shopping within hours. Amazing service!", true, 1, new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc) },
                { "Lerato K.", "Task Runner", "I've been earning extra income on weekends by completing tasks in my area. Highly recommend!", true, 2, new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc) },
                { "Sipho N.", "Task Creator", "The platform is so easy to use. I posted a task and had it completed the same day.", true, 3, new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc) }
            });
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "Testimonials");
}
