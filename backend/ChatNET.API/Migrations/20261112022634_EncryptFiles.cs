using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatNET.API.Migrations
{
    /// <inheritdoc />
    public partial class EncryptFiles : Migration
    {
        // From here on a file is stored encrypted and its name and type are inside the sealed message, so the files that exist now,
        // which the bucket holds as they were uploaded and the table names and types in the clear, are removed, and so are the messages
        // that carry them: a message whose files are gone is not what its sender sent. Messages with text only are not touched. A
        // migration cannot reach the bucket, so one outbox row is made for each file and the relay takes the objects out once the
        // application runs. What this removes cannot be restored: Down puts the columns back and no more.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "OutboxMessages" ("Id", "Kind", "Payload", "CreatedAt", "NextAttemptAt", "Attempts")
                SELECT gen_random_uuid(), 'StoredFileRemoved', jsonb_build_object('key', a."StorageKey"), now(), now(), 0
                FROM "MessageAttachments" a;
                """);

            // The pushes about those messages and the mentions in them, which are about nothing once the messages are gone.
            migrationBuilder.Sql("""
                DELETE FROM "OutboxMessages"
                WHERE ("Kind" IN ('MessageCreated', 'MessageUpdated', 'MessageFilesRemoved', 'MessageAlertRequested')
                       AND ("Payload" ->> 'messageId')::uuid IN (SELECT "MessageId" FROM "MessageAttachments"))
                   OR ("Kind" IN ('NotificationCreated', 'NotificationAlertRequested')
                       AND ("Payload" ->> 'notificationId')::uuid IN (
                           SELECT "Id" FROM "Notifications" WHERE "MessageId" IN (SELECT "MessageId" FROM "MessageAttachments")));
                """);

            migrationBuilder.Sql("""
                DELETE FROM "Notifications" WHERE "MessageId" IN (SELECT "MessageId" FROM "MessageAttachments");
                """);

            // Their attachment rows go with them (the foreign key cascades).
            migrationBuilder.Sql("""
                DELETE FROM "Messages" WHERE "Id" IN (SELECT "MessageId" FROM "MessageAttachments");
                """);

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "MessageAttachments");

            migrationBuilder.DropColumn(
                name: "FileName",
                table: "MessageAttachments");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "MessageAttachments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "MessageAttachments",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FileName",
                table: "MessageAttachments",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "MessageAttachments",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
