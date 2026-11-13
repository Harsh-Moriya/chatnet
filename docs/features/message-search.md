# Searching messages

Messages are sealed, so the server cannot search them. Search runs in the browser over a copy of the messages the browser has already opened, kept in IndexedDB on that device. The server's part is a feed of what changed in each conversation, which keeps the copy current.

## Using it

The search box sits above the list of conversations. While it holds text, the list is replaced by the results; clearing the box (the cross, or Escape) brings the list back.

- At least 2 and at most 100 characters, after trimming.
- Results are the newest messages first, 20 at a time, with "Show more results" for the rest.
- A result shows the conversation, the sender and the time, and the text around the first match with the matching words picked out. A message that matched by the name of one of its files shows the file name instead.
- A click opens the conversation at that message, the same way a notification does: it is found in what the tab holds, or the history around it is loaded.
- A message that was edited is searched by its latest text. A deleted message is never found. Messages sent while the person was away are found once the browser has read them.

### What a search can say

| Query | Finds messages that |
|---|---|
| `budget meeting` | hold every word, in any order |
| `meet` | hold a word that starts with `meet`; only the last word typed can be unfinished, so `meet budg` needs the word `meet` and a word starting `budg` |
| `"exact phrase"` | hold those words together, in that order |
| `-draft` | do not hold the word; needs at least one other term |
| `cat or dog` | hold either word |

Case and accents do not matter: `cafe` finds `Café`. A word is a run of letters, digits and underscores in any script; punctuation and spaces only separate words, so `e-mail` is the two words `e` and `mail` that must stand together. Scripts written without spaces are read as one long word each, so a fragment from the middle of a Chinese or Japanese sentence does not match.

A word after a minus, after `or` or inside quotes is always a whole word, and so is a word followed by a quote or a mark.

## The local store

Each message the browser opens is kept as its text, its sender, its time, its edit count, the names of its files and the number of its latest change. Messages that could not be opened yet (no key for their epoch on this device) are kept sealed and opened again when the keys of their conversation change. A deleted message is kept as a small marker, so a late copy of it cannot bring it back.

The store is filled in two ways:

- **As messages are opened.** Everything the app opens, from history, from a push, from its own sends and edits, is written to the store a moment later, in batches.
- **By reading each conversation.** The first time, the browser reads the conversation's history from the newest message back (50 at a time), so recent messages are searchable first, and then reads the changes feed from the number the conversation had when it began. After that it reads only the changes since the last number it stored. This runs when the list of conversations first loads, after a reconnect, and whenever the list says a conversation moved. It resumes where it stopped if the tab is closed halfway.

While older history is still being read, a search says it covers what the device has so far, and runs again as each conversation finishes.

One tab reads for a person at a time (Web Locks, where the browser has them). Reading is idempotent, so where it has not, tabs only repeat work.

## Where it is kept, and for how long

- The store is in the same IndexedDB database as the keys of the device, under the person's user id. Two people who use one browser never see each other's messages.
- Signing out deletes the person's messages and read positions from the device, as it deletes their keys. A session that only expires keeps them, as it keeps the keys.
- The store is a copy of what the server holds and can always be read again: a store that is missing, evicted by the browser or written in an older format is emptied and rebuilt from the server.
- A browser that cannot keep the store (some private windows) shows "Search is not available in this browser"; nothing else changes.

## Limits

- The copy on the device is plain text, like the keys next to it: anyone who can read the browser's profile on disk, or a script running in the page, can read it. This is the price of searching on the device. Signing out removes it.
- Search covers only what the device has read. A new device starts empty and fills as it reads.
- It does not look inside files, only at their names.
- Each search walks the store from the newest message and stops once it has a page, so a common word costs about a page of messages and a rare word reads the whole store. The cost is that of reading the store, not of the server.
- A message that cannot be opened on this device is not found.

## The changes feed

Every message carries `changeSeq`, the number of its latest change, and every conversation carries the number of the latest change to any of its messages (in the list as `changeSeq`). A send, an edit and a delete each take the next number of their conversation, inside the transaction that makes the change. Numbers start at 1, have no gaps and follow the order the changes became visible.

`GET /api/conversations/{id}/changes?since=N` returns the messages whose number is above `N`, in order, at most 100, as `{ changes, cursor, more }`. An example response is on the [endpoints](../api/endpoints.md#changes) page.

- A client asks from the number it last stored (0 for a new store) and, while `more` is true, asks again from `cursor`. When `more` is false, `cursor` is the conversation's number as it was read, and is where the next request starts.
- A message appears once, in its current state, at the number of its latest change: an edited message has moved to the end, and a deleted message comes as a marker (`ciphertext: null`, `isDeleted: true`).
- The same member window as the history applies: only messages created while the caller was a member, in their current state. A caller who is not a member is a 403.
- A point above the conversation's number is a 409 with code `changes-cursor-ahead`: the server holds an older state than the client last saw (a restored backup), so the client discards what it holds for that conversation and reads it again from the start. A negative or missing point is a 400.
- Numbers come from a counter on the conversation's row that is bumped by a statement taking the row's lock, so a change that commits later never has a lower number than one a reader has already seen. This is what makes it safe to continue from the cursor.

A client that is not a browser follows the same two steps: read the history back from the newest message, then follow the feed from the number noted before it began.
