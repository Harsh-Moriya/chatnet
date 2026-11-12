# Uploads go straight to storage and are checked when the message is sent

Status: Accepted, in part superseded by [0039](0039-files-encrypted-in-the-browser.md)

## Context

Bytes should not pass through the API (memory, bandwidth, the request size limit). But a presigned PUT cannot cap or inspect what is sent, so a ticket proves nothing about the object.

## Decision

`POST .../uploads` checks membership, the account and the declared type and size, and returns a presigned PUT under `pending/{conversation}/{user}/{guid}`; the key itself says whose upload it is, so the server stores no ticket. Sending a message looks at each named upload (the key is the caller's, the object exists, 1 byte to the limit, an allowed type), copies it inside the bucket to `attachments/{conversation}/{attachmentId}` on condition that its ETag is the one that was looked at, saves the message and attachment rows in one transaction, and deletes the uploads. Anything that fails after copies exist removes them.

Files are now encrypted in the browser before they are uploaded (see [0039](0039-files-encrypted-in-the-browser.md)), so the server can no longer see a file's type or name. The ticket carries only the size of the encrypted file, the checks at send are the key, the existence of the object and its size, and the list of allowed types, the type check and the download name are gone from the server. The rest of this decision holds.

## Alternatives considered

- Uploading through the API.
- A POST policy.
- A table of issued tickets.
- Trusting the ticket's size and type.
- Deleting the uploads before the commit (lost if the save fails) or copying without the ETag condition (someone holding the link could swap the file after the check).

## Consequences

- The limits hold even though a presigned PUT cannot enforce them.
- A ticket holder can park a file under `pending/` until the send looks at it or the lifecycle rule expires it (a day).

## Related

- [Attachments](../features/attachments.md)
- [Security model](../architecture/security-model.md)
