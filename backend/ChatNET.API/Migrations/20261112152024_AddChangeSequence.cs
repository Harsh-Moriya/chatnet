using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatNET.API.Migrations
{
    /// <inheritdoc />
    public partial class AddChangeSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ChangeSeq",
                table: "Messages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ChangeSeq",
                table: "Conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // What exists is numbered in the order it was sent: the messages of each conversation 1, 2, 3 and so on by time (the id
            // settles messages stamped alike), and the conversation's counter is its number of messages. Edits and deletions that
            // happened before this have no history to number, so a message carries the number of its creation.
            migrationBuilder.Sql("""
                UPDATE "Messages" m
                SET "ChangeSeq" = numbered."Seq"
                FROM (SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "ConversationId" ORDER BY "CreatedAt", "Id") AS "Seq" FROM "Messages") AS numbered
                WHERE m."Id" = numbered."Id";
                """);

            migrationBuilder.Sql("""
                UPDATE "Conversations" c
                SET "ChangeSeq" = counted."Total"
                FROM (SELECT "ConversationId", COUNT(*) AS "Total" FROM "Messages" GROUP BY "ConversationId") AS counted
                WHERE c."Id" = counted."ConversationId";
                """);

            // The default only served to add the column to rows that existed; from here on a message that is stored without a number
            // is refused at once, not stored as 0 and found out when a second one clashes with it.
            migrationBuilder.Sql("""ALTER TABLE "Messages" ALTER COLUMN "ChangeSeq" DROP DEFAULT;""");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_ChangeSeq",
                table: "Messages",
                columns: new[] { "ConversationId", "ChangeSeq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_ConversationId_ChangeSeq",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ChangeSeq",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ChangeSeq",
                table: "Conversations");
        }
    }
}
