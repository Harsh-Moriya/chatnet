using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatNET.API.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSearchIndexes : Migration
    {
        // The people search in the new conversation dialog matches the start of a handle or a display name with ILIKE, which an
        // ordinary index cannot serve (it is not case sensitive), so every search read every user. A trigram index can: it
        // finds the rows that share the letters of the text typed and Postgres checks only those. pg_trgm ships with
        // Postgres and may be created by the owner of the database. Written as SQL because the EF model cannot describe an
        // operator class.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.Sql("""CREATE INDEX "IX_AspNetUsers_UserName_Trigram" ON "AspNetUsers" USING gin ("UserName" gin_trgm_ops);""");
            migrationBuilder.Sql("""CREATE INDEX "IX_AspNetUsers_DisplayName_Trigram" ON "AspNetUsers" USING gin ("DisplayName" gin_trgm_ops);""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX "IX_AspNetUsers_DisplayName_Trigram";""");
            migrationBuilder.Sql("""DROP INDEX "IX_AspNetUsers_UserName_Trigram";""");
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS pg_trgm;");
        }
    }
}
