using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatNET.API.Migrations
{
    /// <inheritdoc />
    public partial class EncryptMessages : Migration
    {
        // From here on the server holds only what the browser of the sender sealed, so the messages that exist now, which it holds in
        // the clear, are deleted and nothing replaces them: that is what makes it true that the server cannot read a message. The
        // files they carried go with them. A migration cannot reach the bucket, so one outbox row is made for each file, and the
        // relay takes the objects out once the application runs. What this removes cannot be restored: Down puts the columns back
        // and no more.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "OutboxMessages" ("Id", "Kind", "Payload", "CreatedAt", "NextAttemptAt", "Attempts")
                SELECT gen_random_uuid(), 'StoredFileRemoved', jsonb_build_object('key', a."StorageKey"), now(), now(), 0
                FROM "MessageAttachments" a;
                """);

            // The pushes that were about a message or a mention, which are about nothing now. The ones about an invitation to a group stay.
            migrationBuilder.Sql("""
                DELETE FROM "OutboxMessages"
                WHERE "Kind" IN ('MessageCreated', 'MessageUpdated', 'MessageFilesRemoved', 'MessageAlertRequested')
                   OR ("Kind" IN ('NotificationCreated', 'NotificationAlertRequested')
                       AND ("Payload" ->> 'notificationId')::uuid IN (SELECT "Id" FROM "Notifications" WHERE "Type" = 'Mention'));
                """);

            migrationBuilder.Sql("""DELETE FROM "Notifications" WHERE "Type" = 'Mention';""");

            // Their attachments go with them (the foreign key cascades).
            migrationBuilder.Sql("""DELETE FROM "Messages";""");

            migrationBuilder.DropIndex(
                name: "IX_Messages_ConversationId_SenderId_ClientMessageId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "Content",
                table: "Messages");

            migrationBuilder.AlterColumn<Guid>(
                name: "ClientMessageId",
                table: "Messages",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Ciphertext",
                table: "Messages",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "KeyEpoch",
                table: "Messages",
                type: "integer",
                nullable: false);

            migrationBuilder.AddColumn<Guid[]>(
                name: "MentionedUserIds",
                table: "Messages",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_SenderId_ClientMessageId",
                table: "Messages",
                columns: new[] { "ConversationId", "SenderId", "ClientMessageId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_Ciphertext",
                table: "Messages",
                sql: "(\"IsDeleted\" AND \"Ciphertext\" IS NULL) OR (NOT \"IsDeleted\" AND \"Ciphertext\" IS NOT NULL AND length(\"Ciphertext\") BETWEEN 98 AND 32768)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_KeyEpoch",
                table: "Messages",
                sql: "\"KeyEpoch\" >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_ConversationId_SenderId_ClientMessageId",
                table: "Messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_Ciphertext",
                table: "Messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_KeyEpoch",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "Ciphertext",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "KeyEpoch",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MentionedUserIds",
                table: "Messages");

            migrationBuilder.AlterColumn<Guid>(
                name: "ClientMessageId",
                table: "Messages",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "Content",
                table: "Messages",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_SenderId_ClientMessageId",
                table: "Messages",
                columns: new[] { "ConversationId", "SenderId", "ClientMessageId" },
                unique: true,
                filter: "\"ClientMessageId\" IS NOT NULL");
        }
    }
}
