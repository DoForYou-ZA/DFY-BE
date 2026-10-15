using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoForYou.API.Migrations;

public partial class RepairProductionTaskName : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Production's EF Task model requires a non-null TaskName, but the live
        // table predates that field. Preserve every row and derive a readable
        // title from the existing description where one is available.
        migrationBuilder.Sql("""
            ALTER TABLE "Tasks"
                ADD COLUMN IF NOT EXISTS "TaskName" text NOT NULL DEFAULT '';

            UPDATE "Tasks"
            SET "TaskName" = COALESCE(
                NULLIF(left(btrim("TaskDescription"), 120), ''),
                "TaskId")
            WHERE "TaskName" IS NULL OR btrim("TaskName") = '';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally non-destructive: retain task names and production data.
    }
}
