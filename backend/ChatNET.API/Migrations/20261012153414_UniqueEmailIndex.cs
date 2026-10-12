using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatNET.API.Migrations
{
    /// <inheritdoc />
    public partial class UniqueEmailIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A unique index cannot be built over duplicates, and the database's own error does not say
            // what to do. Checking first gives a message that does, and since a migration runs in one
            // transaction, nothing has changed when it stops.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "AspNetUsers"
                        WHERE "NormalizedEmail" IS NOT NULL
                        GROUP BY "NormalizedEmail"
                        HAVING COUNT(*) > 1) THEN
                        RAISE EXCEPTION 'Cannot make emails unique: some accounts share an email address. Find them with SELECT "NormalizedEmail", COUNT(*) FROM "AspNetUsers" GROUP BY "NormalizedEmail" HAVING COUNT(*) > 1, resolve them, then apply this migration again.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");
        }
    }
}
