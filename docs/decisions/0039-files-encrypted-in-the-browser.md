# Files are encrypted in the browser, and the server enforces size and count only

Status: Accepted

## Context

A message's text is sealed, but a file stored as uploaded would still show its content, its name and its type to anyone who can read the bucket or the database. The server must still keep files in order, refuse an upload that is too large, and give the right readers a way to fetch them, without ever being able to open what it stores.

## Decision

The sender's browser encrypts each file in pieces of 64 KiB with AES-256-GCM, under a random key made for that file, and uploads the result. The key does not go to the server. The name, type, size, key and piece size of each file are entries in the sealed message next to the text, so only the members of the conversation can open a file.

- The upload ticket carries one number, the size of the encrypted file, and signs the upload for `application/octet-stream`. The server cannot see what a file is, so it enforces size and count only. The type list was removed from the configuration, because a list the server cannot check would look like a rule and not be one.
- `Storage:MaxFileBytes` stays the largest file as a person sees it, before encryption. The largest object the server accepts is what a file of that size becomes at 64 KiB pieces (the file, a 13-byte header and a 16-byte tag for each piece), and the smallest is 29 bytes. A client that chooses smaller pieces can store less.
- The files of a message are tied to its entries by position: the n-th attachment, in the order they were attached, is described by the n-th entry. A reader accepts the pairing only when the stored size is the one an encrypted file of that entry would have, and shows any other attachment as a file that can't be opened.
- What a sender says a file is called and is, is a claim. A reader cleans the name, draws only PNG, JPEG, GIF and WebP pictures and plays only MP4 and WebM video, and saves everything else as opaque bytes, so nothing a sender says can make the app open a file as a web page.
- An edit seals the message again, so it carries the entries of the files again. A message with a file that can't be opened cannot be edited.
- The browser fetches a file with the signed link, decrypts it in memory and shows it. A link that has expired is renewed once.
- The change was applied by a migration that deleted the messages that carried unencrypted files, with their notifications and the pending pushes about them, queued the removal of every stored object, and dropped the name, type and kind from the attachment table.

## Alternatives considered

- Leaving files as uploaded and relying on the storage provider's own encryption at rest: it protects a stolen disk, not a leaked bucket, a reader of the storage or a copy of the database.
- Keeping a list of allowed types on the server "for the sender": it cannot be checked against bytes it cannot read.
- Counting the encrypted size against the limit as written: a file of exactly 10 MiB would be refused for the overhead of its own encryption.
- Naming each attachment inside the sealed message by id: it would change the published format and let a client choose the ids of database rows. Position, checked by size, needs neither.
- Encrypting a whole file in one operation: it would have to be held and decrypted whole by every reader, and could never be read by range.
- Keeping the old files readable beside the new ones: a conversation would hold messages that are half sealed.

## Consequences

- The server and the bucket hold ciphertext. They still see how many files a message has, how large each is (a fixed overhead more than the file), who sent them and when, and the times files are fetched.
- A client that is not the web client can send any bytes. Readers decide what to do with what a sender says a file is, and a file that is not the one its entry describes is refused.
- A file is held whole in memory while it is shown, at most 10 MiB. The format cuts a file into pieces so a reader can later fetch and decrypt a part of it.
- There is no virus scan and no server-side thumbnail, because the server cannot read the bytes.
- The bucket must allow `GET` from the app's origin, since the browser fetches files itself.
- Files that were uploaded before this change were deleted with their messages and cannot be restored.
- Protection against a malicious server that serves changed JavaScript is out of scope: the code that encrypts and opens comes from the server.

## Related

- [Attachments](../features/attachments.md)
- [Encryption formats](../architecture/encryption-format.md)
- [Security model](../architecture/security-model.md)
- [Messages are sealed in the browser, and the server stores a blob it checks only for shape and epoch](0038-messages-stored-as-ciphertext.md)
- [Uploads go straight to storage and are checked when the message is sent](0024-direct-uploads-checked-at-send.md)
