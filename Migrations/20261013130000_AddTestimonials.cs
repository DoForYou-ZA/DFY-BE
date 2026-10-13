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

        migrationBuilder.CreateIndex(
            name: "IX_Testimonials_IsPublished_SortOrder",
            table: "Testimonials",
            columns: new[] { "IsPublished", "SortOrder" });

        // Keep the original homepage stories when the database is first migrated.
        // Raw SQL avoids requiring a generated migration target model for InsertData.
        migrationBuilder.Sql(@"
            INSERT INTO ""Testimonials""
                (""Name"", ""Role"", ""Text"", ""IsPublished"", ""SortOrder"", ""CreatedAt"", ""UpdatedAt"")
            VALUES
                ('Thabo M.', 'Task Creator', 'DoForYou helped me find someone to handle my grocery shopping within hours. Amazing service!', TRUE, 1, TIMESTAMPTZ '2026-10-13 00:00:00+00', TIMESTAMPTZ '2026-10-13 00:00:00+00'),
                ('Lerato K.', 'Task Runner', 'I''ve been earning extra income on weekends by completing tasks in my area. Highly recommend!', TRUE, 2, TIMESTAMPTZ '2026-10-13 00:00:00+00', TIMESTAMPTZ '2026-10-13 00:00:00+00'),
                ('Sipho N.', 'Task Creator', 'The platform is so easy to use. I posted a task and had it completed the same day.', TRUE, 3, TIMESTAMPTZ '2026-10-13 00:00:00+00', TIMESTAMPTZ '2026-10-13 00:00:00+00');
        ");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "Testimonials");
}
