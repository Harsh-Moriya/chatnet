using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatNET.API.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageClientMessageId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClientMessageId",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_SenderId_ClientMessageId",
                table: "Messages",
                columns: new[] { "ConversationId", "SenderId", "ClientMessageId" },
                unique: true,
                filter: "\"ClientMessageId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_ConversationId_SenderId_ClientMessageId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ClientMessageId",
                table: "Messages");
        }
    }
}
