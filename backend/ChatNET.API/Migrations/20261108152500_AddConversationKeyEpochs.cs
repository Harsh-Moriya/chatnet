using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatNET.API.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationKeyEpochs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "KeyEpoch",
                table: "Conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "KeyRotationRequired",
                table: "Conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ConversationKeyEpochs",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Epoch = table.Column<int>(type: "integer", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    KeyCommitment = table.Column<byte[]>(type: "bytea", nullable: false),
                    Signature = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationKeyEpochs", x => new { x.ConversationId, x.Epoch });
                    table.CheckConstraint("CK_ConversationKeyEpochs_Epoch", "\"Epoch\" >= 1");
                    table.CheckConstraint("CK_ConversationKeyEpochs_KeyCommitment", "length(\"KeyCommitment\") = 32");
                    table.CheckConstraint("CK_ConversationKeyEpochs_Signature", "length(\"Signature\") = 64");
                    table.ForeignKey(
                        name: "FK_ConversationKeyEpochs_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationKeyEpochs_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConversationKeyEnvelopes",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Epoch = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountKeyGeneration = table.Column<int>(type: "integer", nullable: false),
                    Envelope = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationKeyEnvelopes", x => new { x.ConversationId, x.Epoch, x.UserId });
                    table.CheckConstraint("CK_ConversationKeyEnvelopes_AccountKeyGeneration", "\"AccountKeyGeneration\" >= 1");
                    table.CheckConstraint("CK_ConversationKeyEnvelopes_Envelope", "length(\"Envelope\") = 94");
                    table.ForeignKey(
                        name: "FK_ConversationKeyEnvelopes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationKeyEnvelopes_ConversationKeyEpochs_Conversation~",
                        columns: x => new { x.ConversationId, x.Epoch },
                        principalTable: "ConversationKeyEpochs",
                        principalColumns: new[] { "ConversationId", "Epoch" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Conversations_KeyEpoch",
                table: "Conversations",
                sql: "\"KeyEpoch\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationKeyEnvelopes_ConversationId_UserId_Epoch",
                table: "ConversationKeyEnvelopes",
                columns: new[] { "ConversationId", "UserId", "Epoch" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationKeyEnvelopes_UserId",
                table: "ConversationKeyEnvelopes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationKeyEpochs_CreatedById",
                table: "ConversationKeyEpochs",
                column: "CreatedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationKeyEnvelopes");

            migrationBuilder.DropTable(
                name: "ConversationKeyEpochs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Conversations_KeyEpoch",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "KeyEpoch",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "KeyRotationRequired",
                table: "Conversations");
        }
    }
}
