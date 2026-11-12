using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatNET.API.Migrations
{
    /// <inheritdoc />
    public partial class RemoveMessageSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The text of a message is ciphertext the server cannot read, so there is nothing for a full text index to hold.
            // btree_gin was only there to let that index hold the conversation id.
            migrationBuilder.Sql("""DROP INDEX "IX_Messages_Search";""");
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS btree_gin;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gin;");

            migrationBuilder.Sql("""
                CREATE INDEX "IX_Messages_Search"
                ON "Messages" USING gin ("ConversationId", to_tsvector('simple', COALESCE("Content", '')));
                """);
        }
    }
}
