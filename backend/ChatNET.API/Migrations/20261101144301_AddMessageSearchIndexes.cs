using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatNET.API.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageSearchIndexes : Migration
    {
        // Full text search over what was said. One index, on the conversation and the words together, so that a search of one
        // conversation looks up only that conversation's words: with the words alone, a word that is common in conversations the
        // person is not in would make Postgres read the matches of all of them. An expression index, which the EF model cannot
        // describe, so it is SQL; the expression must be the one SearchRepository writes (its Words), or Postgres will not use
        // it. btree_gin lets a GIN index hold the conversation id, an ordinary column type. The 'simple' configuration does no
        // stemming and drops no words: chats mix languages and handles, and a word should be found as it was typed.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gin;");

            migrationBuilder.Sql("""
                CREATE INDEX "IX_Messages_Search"
                ON "Messages" USING gin ("ConversationId", to_tsvector('simple', COALESCE("Content", '')));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX "IX_Messages_Search";""");
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS btree_gin;");
        }
    }
}
