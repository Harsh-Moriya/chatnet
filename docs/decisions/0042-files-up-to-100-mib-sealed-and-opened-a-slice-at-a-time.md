# Files of up to 100 MiB are sealed and opened a slice at a time

Status: Accepted. Changes the size limit and the "held whole in memory" consequence of [0039](0039-files-encrypted-in-the-browser.md).

## Context

A video from a phone is often larger than 10 MiB, and a limit that low leaves little to play. The first version of encrypted files read a file whole into memory to seal it and to open it, which is fine at 10 MiB and not at 100: sealing and opening a file of that size held several copies of it at once, and a phone's browser can end a tab for less. The format already cuts a file into independent pieces of 64 KiB, so nothing in it needs to change.

## Decision

**The largest file is 100 MiB, one limit for every file.** The server cannot tell a video from any other file, so it cannot have a larger limit for videos. `Storage:MaxFileBytes` is 104,857,600, and the largest stored object follows from it (104,883,213 bytes at 64 KiB pieces).

**A file is sealed and opened a slice at a time, as a `Blob`.** The browser reads about 4 MiB of a file, seals its pieces, and keeps the sealed slice as a `Blob`; the slices are joined as `Blob`s and the result is what is uploaded. Opening a downloaded file does the same in the other direction. Only the slice being worked on is in the page's memory, and the browser may keep the rest outside it. The stored bytes are the same as before for the same key and nonce prefix, so the published test vectors still hold.

**The browser keeps opened files that nobody is showing up to a budget of 64 MiB**, newest first, instead of a number of files.

**A picture over 10 MiB is not drawn in the message.** Pictures are opened as they come near the screen without anyone asking, so a large one is shown as a file to save until a viewer opens it on request.

## Alternatives considered

- A separate, larger limit for video: the server cannot know which files are videos.
- 50 MiB or 250 MiB: 50 MiB is a short clip; at 250 MiB an upload on a phone takes long enough to fail often, and storage grows faster.
- A multipart (resumable) upload: a single request to the bucket takes far more than 100 MiB, and a resumable upload can be added later without changing the format.

## Consequences

- A message with four files can now hold 400 MiB, and a person can store four times as much per file; storage is the cost.
- The upload is one request, so a connection that drops during it starts again from the beginning.
- The format, and every client that already follows it, is unchanged. A client that reads a file whole still works for a file that fits in its memory.
