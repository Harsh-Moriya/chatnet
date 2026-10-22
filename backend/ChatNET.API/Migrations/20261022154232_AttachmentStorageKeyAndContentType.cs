using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatNET.API.Migrations
{
    /// <inheritdoc />
    public partial class AttachmentStorageKeyAndContentType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Url",
                table: "MessageAttachments",
                newName: "StorageKey");

            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "MessageAttachments",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "MessageAttachments");

            migrationBuilder.RenameColumn(
                name: "StorageKey",
                table: "MessageAttachments",
                newName: "Url");
        }
    }
}
