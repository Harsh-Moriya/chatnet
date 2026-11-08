# Encryption formats

The byte formats used to encrypt message content, files and keys, written so that a client on any platform can read and write them. This page is the specification of version 1. The reference implementation is `frontend/src/lib/crypto`, and [encryption-test-vectors.json](encryption-test-vectors.json) holds fixed inputs and the exact bytes every format must produce.

The formats cover what is stored and relayed as opaque bytes: the public key bundle of a person, the encrypted key backup, the key of a conversation and how it reaches each member, the message blob, the encrypted file, device linking and the safety number.

## Conventions

- **Integers** are unsigned and big-endian.
- **Text** is UTF-8.
- **Ids** (users, conversations, messages, link requests) enter the formats as the 16 bytes of the UUID in the order its hex digits are written, `3f2a9c1e-7b4d-...` becoming `3f 2a 9c 1e 7b 4d ...`. This is not the mixed-endian layout some platforms use in memory for a GUID.
- **`||`** is concatenation. **`label(L, parts...)`** is the UTF-8 bytes of the string `L`, a zero byte, then the parts.
- **Versioning.** A stored blob starts with a version byte (`01`) and a suite byte (`01`). Suite 1 is the set of algorithms below. A reader rejects a version or suite it does not know instead of guessing. Every label carries the version too (`chatnet/v1/...`).
- **Domain separation.** Every HKDF info, every associated-data string and every signature input starts with a label (listed under each format), so a value made for one purpose is never accepted for another.

## Algorithms

| Purpose | Algorithm |
|---|---|
| Key agreement | X25519 (RFC 7748) |
| Signatures | Ed25519 (RFC 8032) |
| Key derivation | HKDF with SHA-256 (RFC 5869) |
| Passphrase stretching | PBKDF2 with HMAC-SHA-256 (RFC 8018) |
| Authenticated encryption | AES-256-GCM with a 12-byte nonce and a 16-byte tag |
| Hash | SHA-256 |

A private key is 32 raw bytes: the X25519 scalar, or the Ed25519 seed. An API that cannot import a raw private key can wrap the 32 bytes in the fixed PKCS8 prefix of its algorithm: `302e020100300506032b656e04220420` for X25519 and `302e020100300506032b657004220420` for Ed25519. A public key is 32 raw bytes. A signature is 64 bytes.

An X25519 agreement that gives 32 zero bytes (a public key of small order) is rejected.

## Keys of a person

Each person has two kinds of key.

- **Identity key** (Ed25519): signs. It does not change unless the person resets their encryption, so other people can remember it and notice a change.
- **Account key** (X25519): receives conversation keys. It is replaced by a new **generation** (1, 2, 3, ...) when a device is removed. Older generations are kept by the person so history stays readable.

### Public bundle

What the server stores and hands to other people: the public keys of one generation, signed by the identity key. 134 bytes.

| Offset | Length | Field |
|---|---|---|
| 0 | 1 | version `01` |
| 1 | 1 | suite `01` |
| 2 | 4 | generation |
| 6 | 32 | identity public key |
| 38 | 32 | account public key |
| 70 | 64 | signature |

The signature is Ed25519 by the identity key over `label("chatnet/v1/account-bundle", userId(16) || generation(4) || identityPublic(32) || accountPublic(32))`. Including the user id stops a bundle being presented as another person's. A reader checks the signature with the identity key the bundle carries; whether that identity key is the right one for the person is decided separately, by remembering it the first time and comparing safety numbers.

### Secrets encoding

The private keys of a person, as the plaintext of the backup and of a device link.

| Offset | Length | Field |
|---|---|---|
| 0 | 1 | version `01` |
| 1 | 32 | identity private key |
| 33 | 32 | identity public key |
| 65 | 2 | number of account keys, at least 1 |
| 67 | 68 each | per account key, in ascending generation: generation (4), private key (32), public key (32) |

Generations must strictly ascend, and the total length must match exactly.

## Backup

The secrets of a person encrypted under a key stretched from a passphrase only that person knows. The server stores the blob and never the passphrase.

| Offset | Length | Field |
|---|---|---|
| 0 | 1 | version `01` |
| 1 | 1 | suite `01` |
| 2 | 1 | key derivation `01` (PBKDF2-HMAC-SHA256) |
| 3 | 4 | iterations |
| 7 | 16 | salt |
| 23 | 12 | nonce |
| 35 | n + 16 | AES-256-GCM ciphertext of the secrets encoding, then the tag |

- **Key:** PBKDF2-HMAC-SHA256 over the UTF-8 bytes of the passphrase after Unicode NFKC normalisation, with the salt and the iteration count from the header, producing 32 bytes.
- **Associated data:** `label("chatnet/v1/backup", userId(16) || header(35))`.
- **Iterations:** 600000 for a new backup. A reader accepts 1 to 10000000, so a stored blob can be opened after the default is raised.
- **Passphrase length:** at least 12 characters, counted as Unicode code points after normalisation.
- A wrong passphrase and a damaged blob both fail authentication and cannot be told apart.

## Sealed box

A small value encrypted to the X25519 public key of a recipient. It carries conversation keys to members and the secrets to a device being linked; only the label and the context differ.

```
box = 01 || 01 || E(32) || nonce(12) || ciphertext || tag(16)
```

To seal `plaintext` to recipient public key `R` with label `L` and context bytes `C`:

1. Make a fresh X25519 key pair `(e, E)`.
2. `secret = X25519(e, R)`.
3. `info = label(L, C)`.
4. `key = HKDF-SHA256(ikm = secret, salt = E || R, info = info, length = 32)`.
5. Pick a random 12-byte `nonce`.
6. `ciphertext || tag = AES-256-GCM(key, nonce, plaintext, aad = info || 01 || 01 || E)`.

To open, the recipient reads `E` from the box and repeats steps 2 to 4 with their private key. The overhead is 62 bytes.

## Conversation keys

Messages of a conversation are encrypted under a 32-byte AES key that is valid for one **epoch**. A new epoch starts when the membership changes, so a person who left cannot read what comes after, and a person added cannot read what came before.

### Envelope

The key of an epoch, sealed for one member. 94 bytes.

- Label `chatnet/v1/envelope`.
- Context `conversationId(16) || epoch(4) || userId(16) || generation(4)`, where `userId` is the member and `generation` is the generation of their account key the box is sealed to.
- Plaintext: the 32-byte epoch key.

There is one envelope per member per epoch. The generation in the context tells the member which of their account keys opens it.

### Commitment

`HKDF-SHA256(ikm = epochKey, salt = empty, info = label("chatnet/v1/epoch-commitment", conversationId(16) || epoch(4)), length = 32)`.

The commitment is published with the epoch. It reveals nothing about the key, and a member who opens their envelope checks that the key they got matches it. A creator who tried to give different members different keys could not satisfy one signed commitment.

### Signed record

The creator of an epoch signs a record of it with their identity key: Ed25519 over

```
label("chatnet/v1/epoch-signature",
      conversationId(16) || epoch(4) || creatorId(16) || commitment(32) ||
      memberCount(4) || (userId(16) || generation(4)) for each member, ascending by userId bytes)
```

Each member is listed once. A reader checks the signature against the identity key of the creator, that the creator is a member, that the member list is the one they expect, and that their own envelope opens and matches the commitment.

## Message

A message is one opaque blob.

| Offset | Length | Field |
|---|---|---|
| 0 | 1 | version `01` |
| 1 | 1 | suite `01` |
| 2 | 4 | epoch |
| 6 | 12 | nonce |
| 18 | n + 16 | AES-256-GCM ciphertext of the padded payload, then the tag |
| end - 64 | 64 | signature |

The smallest blob is 98 bytes, and a blob is at most 32768 bytes when written.

**Context** (52 bytes): `conversationId(16) || senderId(16) || clientMessageId(16) || editCount(4)`. The sender is the person who signs; `clientMessageId` is the id the client chose for the message; `editCount` is 0 for the first version and rises by one with each edit.

- **Associated data:** `label("chatnet/v1/message", context || header(18))`.
- **Signature:** Ed25519 by the identity key of the sender over `label("chatnet/v1/message-signature", context || header(18) || ciphertext || tag)`.

The signature is made after encrypting and checked before decrypting. Because the context is covered twice, a message cannot be moved to another conversation, shown as sent by another person, or replaced by an older version of itself, and a member who holds the epoch key cannot forge a message from another member.

### Payload

The plaintext is a JSON object in UTF-8, written compactly with the keys in this order, and padded.

```json
{"text":"Hello","files":[{"name":"notes.txt","type":"text/plain","size":150,"key":"<base64url, 32 bytes>","chunkSize":65536}]}
```

- `text`: the message text. Empty when the message is only files.
- `files`: the files of the message, in the order the attachments are stored. Each holds the original name, the content type, the size in bytes of the unencrypted file, the key of that file as unpadded base64url, and the chunk size used to encrypt it.

Readers accept any JSON of this shape, with fields they do not know ignored.

**Padding:** the JSON bytes, then `80`, then zero bytes up to the next multiple of 256. There is always at least one padding byte. A reader removes trailing zeros and then the `80`, and rejects the payload if the `80` is missing. All messages whose JSON is shorter than 256 bytes therefore have the same length.

## File

An attachment is stored encrypted in fixed-size chunks, so a client can fetch and decrypt any chunk by its byte range.

| Offset | Length | Field |
|---|---|---|
| 0 | 1 | version `01` |
| 1 | 1 | suite `01` |
| 2 | 4 | chunk size, 1 to 16777216 |
| 6 | 7 | nonce prefix, random per file |
| 13 | | chunk 0, chunk 1, ... |

The file is cut into pieces of the chunk size; the last piece may be shorter, and an empty file is one empty piece. Each piece is encrypted with AES-256-GCM and a 16-byte tag appended, so every stored chunk but the last is `chunkSize + 16` bytes. The key is random per file and is carried only inside the message payload.

- **Nonce of chunk `i`:** `prefix(7) || i(4) || final(1)`, where `final` is `01` for the last chunk and `00` for the others.
- **Associated data:** `label("chatnet/v1/file-chunk", header(13))`.

Putting the index and the final flag in the nonce means chunks cannot be reordered, the end cannot be cut off without detection, and a chunk cannot be moved to another file. To read one chunk, fetch bytes `13 + i * (chunkSize + 16)` for `chunkSize + 16` bytes (fewer for the last chunk), and decrypt knowing whether it is the last. Chunk `i` is the last when the stored file ends within it.

## Device linking

A new device gets the secrets of a person from a device that is already signed in. The server relays one sealed box and cannot open it.

1. The new device makes a temporary X25519 key pair and registers the public key as a link request.
2. It shows a **link code** derived from that key and, as an alternative, a QR code.
3. The signed-in device obtains the request (by the code, or from the QR), checks that the code matches the temporary public key it was given, and then seals the secrets encoding to the temporary public key.
4. The new device opens the box and has the keys.

**Link code.** `digest = SHA-256(label("chatnet/v1/link-code", temporaryPublic))`. Take the first 50 bits of `digest`, most significant bit first, in groups of 5, and write each group as a character of the alphabet `0123456789ABCDEFGHJKMNPQRSTVWXYZ`. That gives 10 characters, shown as `XXXXX-XXXXX`. When reading a code, ignore case, spaces and hyphens, and read `O` as `0` and `I` or `L` as `1`.

The code is derived from the key, so a server that handed the signed-in device a different key would also have to find one whose hash starts with the same 50 bits, which cannot be done within the few minutes a link request is meant to stay open.

**QR content.** `chatnet-link:1:<requestId>:<temporaryPublic as unpadded base64url>`, which carries the whole request with no server lookup.

**Sealed secrets.** A sealed box with label `chatnet/v1/link`, context `requestId(16) || userId(16)`, and the secrets encoding as plaintext.

## Safety number

Sixty digits two people can compare to confirm that each has the identity key the other thinks they have.

For each person, `digest = SHA-256(label("chatnet/v1/safety", userId(16) || identityPublic(32)))`. Read the first 30 bytes as six groups of 5 bytes, each a big-endian number, reduce each modulo 100000 and write it with five digits. That gives 30 digits. The number is the 30 digits of the person whose 16 user id bytes sort first, followed by the 30 digits of the other, so both people see the same number. It is shown as twelve groups of five.

## What readers must reject

A reader treats each of these as an error and does not try to repair the input:

- an unknown version or suite, a wrong length, or an unknown key derivation in a backup
- a chunk size or iteration count outside the range given above
- a payload that is not valid UTF-8 or JSON of the shape above, a file key that is not 32 bytes, or missing padding
- a signature that does not verify, a ciphertext that does not authenticate, or an association (conversation, sender, client message id, edit count, epoch, member, generation) that differs from the one the blob was made for
- an epoch key that does not match its commitment
- an account key generation list that does not ascend

## Limits of the formats

- **No forward secrecy inside an epoch.** The key of an epoch opens every message of that epoch. Epochs bound the exposure when membership changes, and a new epoch can be started at any time.
- **Padding is coarse.** Messages are padded to a multiple of 256 bytes, so a long message still shows roughly how long it is, and the number and sizes of files are visible.
- **Trust in an identity key is not in the format.** A client remembers the identity key of a person the first time it sees it, and a change is something to show the user. The safety number is how two people confirm the key out of band.
- **Who a message is between, and when, is not hidden.** Only content is encrypted.

## Test vectors

[encryption-test-vectors.json](encryption-test-vectors.json) holds, for version 1: the keys used (all derived from SHA-256 of a fixed text, so no key is chosen by hand), and for each format the inputs and the exact output bytes in hex. Random values (ephemeral keys, nonces, salts) are fixed in the vectors so that output is reproducible; real use draws them at random. Sections: `bundle`, `secrets`, `backup`, `epoch` (commitment, envelope, signed record), `messages`, `files`, `link`, `safetyNumber`.

The vectors were produced by an implementation written separately from the one in the web client, on a different cryptography library, and the web client's tests check that it reproduces every value and opens every blob. A new client implementation should do the same before it handles real data.
