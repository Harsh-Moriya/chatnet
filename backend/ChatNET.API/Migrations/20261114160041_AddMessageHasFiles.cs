using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatNET.API.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageHasFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasFiles",
                table: "Messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // The messages that already have files are marked before the index is made, so it is built once over the final rows.
            migrationBuilder.Sql("""
                UPDATE "Messages" m
                SET "HasFiles" = true
                WHERE EXISTS (SELECT 1 FROM "MessageAttachments" a WHERE a."MessageId" = m."Id");
                """);

            // The default only served to add the column to rows that existed; a message stored from here on says whether it has files.
            migrationBuilder.Sql("""ALTER TABLE "Messages" ALTER COLUMN "HasFiles" DROP DEFAULT;""");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_CreatedAt_Files",
                table: "Messages",
                columns: new[] { "ConversationId", "CreatedAt", "Id" },
                descending: new[] { false, true, true },
                filter: "\"HasFiles\" AND NOT \"IsDeleted\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_ConversationId_CreatedAt_Files",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "HasFiles",
                table: "Messages");
        }
    }
}
