# Encrypted video is played by a service worker, a range at a time

Status: Accepted. Builds on [0039](0039-files-encrypted-in-the-browser.md) (the chunked format) and [0042](0042-files-up-to-100-mib-sealed-and-opened-a-slice-at-a-time.md) (files of up to 100 MiB).

## Context

A video of up to 100 MiB, decrypted whole before the first frame, takes long to start and holds as much memory as the file. A `<video>` element plays well from a URL that answers byte ranges, but the file in the bucket is ciphertext, and the key must not leave the person's devices. The format already cuts a file into independent, authenticated chunks, so any part of it can be opened without the rest.

## Decision

**The service worker answers the video element's range requests.** The page gives the worker a stream (the key, how the file is cut, its sizes, its link and its type) by message and plays `/file-stream/<id>`. For each request the worker fetches only the stored bytes of the chunks that hold the range, opens each chunk with its index and the final flag the file's size says, and answers 206. A chunk that does not authenticate, a bucket that does not honour the range, or a size that does not match is a 502 and no body. An answer is at most 2 MiB and is never kept.

**The key is in the worker's memory only.** It is not in an address, a header or storage. The browser stops an idle worker, so a request for a stream the worker has lost asks the page for it again; an expired link is asked of the page, once.

**Where there is no worker for the page, or the stream fails before the video loads, the whole file is decrypted and played**, as before.

**The worker is TypeScript, built apart from the page into one classic script at `/sw.js`**, so it shares the page's crypto instead of copying it, and is registered when the application starts, not only when alerts are turned on.

## Alternatives considered

- Media Source Extensions: they need a video cut into fragments, and a file a person sends is not.
- Decrypting the whole video first: it is what this replaces; the wait and the memory grow with the file.
- Keeping the worker as a hand-written script with its own copy of the chunk code: no build step, but the encryption format would live in two places and the worker would not be type-checked.
- A module service worker: not supported by every browser the application is meant for.

## Consequences

- The worker is now part of playing video, and it is registered for every person. A browser that does not send media requests through a service worker plays the whole file instead; only Chrome has been tried.
- A video of 100 MiB starts after the first range, and moving about in it fetches only the chunks it needs (a measured case read about 2.4 MB of 60 MiB).
- An answer decrypts a few chunks per request, one chunk at a time per request, and no thread of the page is used.
- A script of the application can open a stream as the page can; the key is as exposed as it was in the page.
