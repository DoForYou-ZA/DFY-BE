using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations;

public partial class RepairProductionTaskDeletedAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Production's EF model expects this nullable soft-delete timestamp.
        // IF NOT EXISTS makes this safe if the column was repaired manually.
        migrationBuilder.Sql("""
            ALTER TABLE "Tasks"
                ADD COLUMN IF NOT EXISTS "DeletedAt" timestamp with time zone NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally non-destructive: preserve any archival metadata.
    }
}
