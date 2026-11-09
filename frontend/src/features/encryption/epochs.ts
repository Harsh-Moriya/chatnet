import {
  type AccountBundle,
  type AccountSecrets,
  type Bytes,
  CryptoError,
  type EpochRecord,
  epochCommitment,
  equal,
  findAccountKey,
  fromBase64Url,
  newEpochKey,
  openBundle,
  openEnvelope,
  sealEnvelope,
  signEpoch,
  toBase64Url,
  verifyEpoch,
} from '../../lib/crypto'
import { KeysMismatchError } from './flows'
import { IdentityChangedError, pinIdentity } from './pins'
import type { EpochApi, EpochBody, EpochKeyStore, EpochRecordWire, PinStore } from './types'

// The keys of a conversation. A conversation has a key for each period (an epoch), made by a member's browser, sealed once for each
// member and checked by every member who opens it. Nothing here touches the network, IndexedDB or the page directly, so a test drives
// the real cryptography against a fake server.

export interface EpochDeps {
  api: EpochApi
  keys: EpochKeyStore
  pins: PinStore
  self: { userId: string; secrets: AccountSecrets }
}

// How many times a key period is made again when the conversation changed under it, before the person is told to try again.
export const MAX_ATTEMPTS = 3

// ##### What the screens explain #####

// Some of the people have not set up encryption, so no key can be sealed for them.
export class MissingKeysError extends Error {
  readonly userIds: string[]

  constructor(userIds: string[]) {
    super('Some people have not set up encryption yet.')
    this.name = 'MissingKeysError'
    this.userIds = userIds
  }
}

// The server refused a key period because the conversation is not what it was made for. `code` is the server's name for the reason
// (see the problem details of the conflict).
export class EpochRefused extends Error {
  readonly code: string | null

  constructor(code: string | null) {
    super(`The server refused the epoch${code ? ` (${code})` : ''}.`)
    this.name = 'EpochRefused'
    this.code = code
  }
}

// The conversation kept changing while a key period was made for it.
export class EpochContestedError extends Error {
  constructor() {
    super('The conversation changed too many times to make its keys.')
    this.name = 'EpochContestedError'
  }
}

const RETRIABLE = new Set(['epoch-stale', 'epoch-members-changed'])

export function isRetriable(failure: unknown): boolean {
  return failure instanceof EpochRefused && failure.code !== null && RETRIABLE.has(failure.code)
}

// Runs the work, and runs it again when the server says the conversation changed under it.
export async function withEpochRetries<T>(work: () => Promise<T>): Promise<T> {
  for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt++) {
    try {
      return await work()
    } catch (failure) {
      if (!isRetriable(failure)) throw failure
    }
  }

  throw new EpochContestedError()
}

// ##### People #####

// The current keys of these people, checked: each bundle is signed by its own identity key and says what the server said, each
// identity is the one this browser first saw for the person, and the person's own bundle is the one they hold. Throws
// MissingKeysError for anyone without keys and IdentityChangedError for a changed identity.
export async function resolvePeers(deps: EpochDeps, userIds: string[]): Promise<Map<string, AccountBundle>> {
  const wanted = [...new Set(userIds)]
  const found = await deps.api.lookupKeys(wanted)
  const bundles = new Map<string, AccountBundle>()

  for (const entry of found) {
    if (!wanted.includes(entry.userId)) continue

    const bundle = await openBundle(entry.userId, fromBase64Url(entry.bundle))
    if (bundle.generation !== entry.generation) throw new CryptoError('malformed', 'The generation is not the one in the bundle')
    bundles.set(entry.userId, bundle)
  }

  const missing = wanted.filter((id) => !bundles.has(id))
  if (missing.length > 0) throw new MissingKeysError(missing)

  for (const [userId, bundle] of bundles) {
    await pinIdentity(deps.pins, deps.self.userId, userId, bundle.identityPublic)
  }

  const own = bundles.get(deps.self.userId)
  if (own) {
    const held = findAccountKey(deps.self.secrets, own.generation)
    if (!held || !equal(held.publicKey, own.accountPublic) || !equal(deps.self.secrets.identity.publicKey, own.identityPublic)) {
      throw new KeysMismatchError()
    }
  }

  return bundles
}

// ##### Making a key period #####

export interface BuiltEpoch {
  body: EpochBody
  // Kept by whoever made it once the server has accepted the epoch.
  key: Bytes
}

// A new key for the conversation, sealed to each of these people and signed by the person making it with a record of who it was
// made for. Whoever makes it is among the people.
export async function buildEpoch(
  deps: EpochDeps,
  conversationId: string,
  epoch: number,
  members: AccountBundle[],
): Promise<BuiltEpoch> {
  const key = newEpochKey()
  const commitment = await epochCommitment(conversationId, epoch, key)

  const envelopes = await Promise.all(
    members.map(async (member) => ({
      userId: member.userId,
      generation: member.generation,
      envelope: toBase64Url(
        await sealEnvelope({ conversationId, epoch, userId: member.userId, generation: member.generation }, member.accountPublic, key),
      ),
    })),
  )

  const signature = await signEpoch(
    {
      conversationId,
      epoch,
      creatorId: deps.self.userId,
      commitment,
      members: members.map((member) => ({ userId: member.userId, generation: member.generation })),
    },
    deps.self.secrets.identity,
  )

  return { body: { epoch, commitment: toBase64Url(commitment), signature: toBase64Url(signature), envelopes }, key }
}

// What a conversation that does not exist yet needs: its id, which the first key period is made for, and that period.
export interface PreparedConversation {
  conversationId: string
  body: EpochBody
  key: Bytes
}

export async function prepareNewConversation(deps: EpochDeps, otherUserIds: string[]): Promise<PreparedConversation> {
  const memberIds = [...new Set([deps.self.userId, ...otherUserIds])]
  const peers = await resolvePeers(deps, memberIds)
  const conversationId = crypto.randomUUID()
  const built = await buildEpoch(deps, conversationId, 1, memberIds.map((id) => peers.get(id)!))

  return { conversationId, body: built.body, key: built.key }
}

// Keeps the key of a conversation the server has made, if it is the one prepared. A request to start a direct conversation that
// already existed answers with that one, whose key is not the one prepared and is fetched from the server instead.
export async function keepNewConversationKey(deps: EpochDeps, prepared: PreparedConversation, madeConversationId: string): Promise<void> {
  if (madeConversationId !== prepared.conversationId) return
  await saveKey(deps, prepared.conversationId, prepared.body.epoch, prepared.key)
}

// A new key period for a group that people are about to join: for the people in it now and the ones named.
export interface PreparedAddition {
  body: EpochBody
  key: Bytes
}

export async function prepareAddingMembers(deps: EpochDeps, conversationId: string, userIds: string[]): Promise<PreparedAddition> {
  const [state, present] = await Promise.all([deps.api.getKeyState(conversationId), deps.api.getMemberIds(conversationId)])
  const memberIds = [...new Set([...present, ...userIds])]
  const peers = await resolvePeers(deps, memberIds)

  return buildEpoch(deps, conversationId, state.keyEpoch + 1, memberIds.map((id) => peers.get(id)!))
}

// A key this browser made and the server accepted. If it cannot be kept the conversation is not lost: the envelope for this person
// is on the server, and opening it gives the same key.
export async function saveKey(deps: EpochDeps, conversationId: string, epoch: number, key: Bytes): Promise<void> {
  await deps.keys.save(deps.self.userId, conversationId, epoch, key).catch(() => undefined)
}

export interface EnsureResult {
  epoch: number
  // This browser made the epoch, as opposed to finding it.
  created: boolean
}

// Makes sure the conversation has a key period that can be used: one that exists, was made for the people in it now and that this
// browser holds. If not, makes the next one. Several members do this together after a change, and the server stores one: the
// others find theirs refused as out of date, look again, and take the one that was stored.
export async function ensureEpoch(deps: EpochDeps, conversationId: string): Promise<EnsureResult> {
  return withEpochRetries(async () => {
    const [state, memberIds] = await Promise.all([deps.api.getKeyState(conversationId), deps.api.getMemberIds(conversationId)])

    if (state.keyEpoch >= 1 && !state.keyRotationRequired) {
      await loadEpochKeys(deps, conversationId, { newest: { epoch: state.keyEpoch, userIds: memberIds } })

      if ((await deps.keys.load(deps.self.userId, conversationId, state.keyEpoch)) !== null) {
        return { epoch: state.keyEpoch, created: false }
      }
      // The newest epoch could not be opened or did not check out; a member may replace it.
    }

    const peers = await resolvePeers(deps, memberIds)
    const epoch = state.keyEpoch + 1
    const built = await buildEpoch(deps, conversationId, epoch, memberIds.map((id) => peers.get(id)!))

    await deps.api.postEpoch(conversationId, built.body)
    await saveKey(deps, conversationId, epoch, built.key)

    return { epoch, created: true }
  })
}

// What the conversation looks like to this browser already (from the list it holds), so a conversation whose newest key is held here is
// made ready without asking the server anything.
export interface KnownKeyState {
  keyEpoch: number
  keyRotationRequired: boolean
}

export async function readyConversationWith(deps: EpochDeps, conversationId: string, known: KnownKeyState): Promise<void> {
  if (known.keyEpoch >= 1 && !known.keyRotationRequired && (await deps.keys.load(deps.self.userId, conversationId, known.keyEpoch)) !== null) return

  await ensureEpoch(deps, conversationId)
}

// ##### Reading a key period #####

export type RejectedReason =
  | 'malformed'
  | 'creatorNotMember'
  | 'notListed'
  | 'membersMismatch'
  | 'identityChanged'
  | 'creatorUnknown'
  | 'badSignature'
  | 'noKey'
  | 'badEnvelope'
  | 'badCommitment'

export interface Rejected {
  epoch: number
  reason: RejectedReason
  // The person whose identity changed, when that is the reason.
  identityChanged?: IdentityChangedError
}

export interface LoadResult {
  // The epochs opened and kept by this call.
  held: number[]
  rejected: Rejected[]
}

export interface LoadOptions {
  // Where to start. Default: after the newest epoch already held.
  from?: number
  // The people the newest epoch must have been made for (the people in the conversation now), when it is known.
  newest?: { epoch: number; userIds: string[] }
}

// Fetches the epochs this person has an envelope for that are not held yet, checks each, opens its envelope and keeps the key. An
// epoch that does not check out is not kept and is reported, with the reason. Everything an epoch claims is checked: that the
// creator is one of the people it was made for, that this person is too, that the creator signed exactly that, that the creator is the
// one this browser first saw, that the envelope opens, and that the key it holds is the one the signature commits to.
export async function loadEpochKeys(deps: EpochDeps, conversationId: string, options: LoadOptions = {}): Promise<LoadResult> {
  let from = options.from ?? (await deps.keys.highest(deps.self.userId, conversationId)) + 1
  const result: LoadResult = { held: [], rejected: [] }
  const creators = new Map<string, AccountBundle | Rejected['reason'] | IdentityChangedError>()

  for (;;) {
    const page = await deps.api.getEpochs(conversationId, from)

    for (const record of page.epochs) {
      const checked = await openRecord(deps, conversationId, record, options, creators)

      if ('key' in checked) {
        await deps.keys.save(deps.self.userId, conversationId, record.epoch, checked.key)
        result.held.push(record.epoch)
      } else {
        result.rejected.push({ epoch: record.epoch, ...checked })
      }
    }

    if (!page.hasMore || page.epochs.length === 0) return result
    from = page.epochs[page.epochs.length - 1].epoch + 1
  }
}

type Opened = { key: Bytes } | { reason: RejectedReason; identityChanged?: IdentityChangedError }

async function openRecord(
  deps: EpochDeps,
  conversationId: string,
  wire: EpochRecordWire,
  options: LoadOptions,
  creators: Map<string, AccountBundle | RejectedReason | IdentityChangedError>,
): Promise<Opened> {
  const self = deps.self.userId

  if (!wire.members.some((member) => member.userId === wire.creatorId)) return { reason: 'creatorNotMember' }

  const mine = wire.members.find((member) => member.userId === self)
  if (!mine || mine.generation !== wire.generation) return { reason: 'notListed' }

  if (options.newest?.epoch === wire.epoch) {
    const expected = new Set(options.newest.userIds)
    const same = wire.members.length === expected.size && wire.members.every((member) => expected.has(member.userId))
    if (!same) return { reason: 'membersMismatch' }
  }

  const creator = await creatorBundle(deps, wire.creatorId, creators)
  if (creator instanceof IdentityChangedError) return { reason: 'identityChanged', identityChanged: creator }
  if (typeof creator === 'string') return { reason: creator }

  let record: EpochRecord
  let signature: Bytes
  let envelope: Bytes
  try {
    record = { conversationId, epoch: wire.epoch, creatorId: wire.creatorId, commitment: fromBase64Url(wire.commitment), members: wire.members }
    signature = fromBase64Url(wire.signature)
    envelope = fromBase64Url(wire.envelope)
  } catch {
    return { reason: 'malformed' }
  }

  // A record that cannot even be put into the form that was signed (a person listed twice, a commitment of the wrong size) is no
  // more valid than one with a wrong signature.
  const signed = await Promise.resolve()
    .then(() => verifyEpoch(record, creator.identityPublic, signature))
    .catch(() => false)
  if (!signed) return { reason: 'badSignature' }

  const accountKey = findAccountKey(deps.self.secrets, wire.generation)
  if (!accountKey) return { reason: 'noKey' }

  let key: Bytes
  try {
    key = await openEnvelope({ conversationId, epoch: wire.epoch, userId: self, generation: wire.generation }, accountKey, envelope)
  } catch {
    return { reason: 'badEnvelope' }
  }

  if (!equal(await epochCommitment(conversationId, wire.epoch, key), record.commitment)) return { reason: 'badCommitment' }

  return { key }
}

// The keys of a creator, asked for once however many epochs they made.
async function creatorBundle(
  deps: EpochDeps,
  creatorId: string,
  cache: Map<string, AccountBundle | RejectedReason | IdentityChangedError>,
): Promise<AccountBundle | RejectedReason | IdentityChangedError> {
  const cached = cache.get(creatorId)
  if (cached !== undefined) return cached

  let found: AccountBundle | RejectedReason | IdentityChangedError
  try {
    found = (await resolvePeers(deps, [creatorId])).get(creatorId)!
  } catch (failure) {
    if (failure instanceof IdentityChangedError) found = failure
    else if (failure instanceof MissingKeysError || failure instanceof CryptoError) found = 'creatorUnknown'
    else throw failure
  }

  cache.set(creatorId, found)
  return found
}
