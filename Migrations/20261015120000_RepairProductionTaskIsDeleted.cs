using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations;

public partial class RepairProductionTaskIsDeleted : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The EF model applies a global query filter using Tasks.IsDeleted.
        // Existing production tasks should remain visible, so default all existing rows to false.
        migrationBuilder.Sql("""
            ALTER TABLE "Tasks"
                ADD COLUMN IF NOT EXISTS "IsDeleted" boolean NOT NULL DEFAULT FALSE;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally non-destructive: do not remove soft-delete state from production data.
    }
}
