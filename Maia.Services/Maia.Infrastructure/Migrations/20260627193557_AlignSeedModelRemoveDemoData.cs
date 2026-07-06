using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AlignSeedModelRemoveDemoData : Migration
    {
        // Snapshot-only migration. EF scaffolded DeleteData for the demo seed rows
        // (TrapInterfaces job + rule/lease, dev ClassificationRules, dev FixPolicyRule)
        // and an InsertData for the new "Exe" JobType — but the *database* mutation for
        // all of that already happens in 20260627182335_CleanupDevSeedsAddExeJobType
        // (raw SQL, already applied). Re-running those ops here would be redundant for
        // the deletes and would COLLIDE on the Exe insert (JobTypeId 5 already exists).
        //
        // So Up/Down are intentionally empty: this migration exists solely to carry the
        // updated model snapshot (HasData now declares metadata only, incl. Exe), keeping
        // the snapshot honest so future `migrations add` diffs are correct.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
