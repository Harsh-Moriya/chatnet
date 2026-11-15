using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatNET.API.Migrations
{
    /// <inheritdoc />
    public partial class AddCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_Ciphertext",
                table: "Messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_KeyEpoch",
                table: "Messages");

            migrationBuilder.AddColumn<Guid>(
                name: "CallId",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Messages",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "Text");

            migrationBuilder.CreateTable(
                name: "Calls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CallerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CalleeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Media = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AnsweredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Calls", x => x.Id);
                    table.CheckConstraint("CK_Calls_Answer", "(\"EndReason\" = 'Completed' AND \"AnsweredAt\" IS NOT NULL) OR (\"EndReason\" IN ('Missed', 'Declined', 'Cancelled', 'Busy') AND \"AnsweredAt\" IS NULL) OR \"EndReason\" = 'Failed'");
                    table.CheckConstraint("CK_Calls_People", "\"CallerId\" <> \"CalleeId\"");
                    table.CheckConstraint("CK_Calls_Times", "\"StartedAt\" <= \"EndedAt\" AND (\"AnsweredAt\" IS NULL OR \"AnsweredAt\" BETWEEN \"StartedAt\" AND \"EndedAt\")");
                    table.ForeignKey(
                        name: "FK_Calls_AspNetUsers_CalleeId",
                        column: x => x.CalleeId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Calls_AspNetUsers_CallerId",
                        column: x => x.CallerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Calls_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_CallId",
                table: "Messages",
                column: "CallId",
                unique: true,
                filter: "\"CallId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_Content",
                table: "Messages",
                sql: "(\"Kind\" = 'Text' AND \"CallId\" IS NULL AND ((\"IsDeleted\" AND \"Ciphertext\" IS NULL) OR (NOT \"IsDeleted\" AND \"Ciphertext\" IS NOT NULL AND length(\"Ciphertext\") BETWEEN 98 AND 32768))) OR (\"Kind\" = 'Call' AND \"CallId\" IS NOT NULL AND \"Ciphertext\" IS NULL AND NOT \"IsDeleted\" AND \"EditCount\" = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_KeyEpoch",
                table: "Messages",
                sql: "\"Kind\" = 'Call' OR \"KeyEpoch\" >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_Calls_CalleeId",
                table: "Calls",
                column: "CalleeId");

            migrationBuilder.CreateIndex(
                name: "IX_Calls_CallerId",
                table: "Calls",
                column: "CallerId");

            migrationBuilder.CreateIndex(
                name: "IX_Calls_ConversationId",
                table: "Calls",
                column: "ConversationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Calls_CallId",
                table: "Messages",
                column: "CallId",
                principalTable: "Calls",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The rows of calls cannot exist without the columns that make them valid, so they go with them: going back loses the call
            // log, and nothing else.
            migrationBuilder.Sql("""DELETE FROM "Messages" WHERE "Kind" = 'Call';""");

            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Calls_CallId",
                table: "Messages");

            migrationBuilder.DropTable(
                name: "Calls");

            migrationBuilder.DropIndex(
                name: "IX_Messages_CallId",
                table: "Messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_Content",
                table: "Messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_KeyEpoch",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "CallId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Messages");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_Ciphertext",
                table: "Messages",
                sql: "(\"IsDeleted\" AND \"Ciphertext\" IS NULL) OR (NOT \"IsDeleted\" AND \"Ciphertext\" IS NOT NULL AND length(\"Ciphertext\") BETWEEN 98 AND 32768)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_KeyEpoch",
                table: "Messages",
                sql: "\"KeyEpoch\" >= 1");
        }
    }
}
