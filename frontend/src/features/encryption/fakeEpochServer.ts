import { EpochRefused } from './epochs'
import { ServerRefused } from './fakeKeysServer'
import type {
  ConversationKeyState,
  EpochApi,
  EpochBody,
  EpochKeyStore,
  EpochPageWire,
  EpochReason,
  EpochRecordWire,
  Pin,
  PinStore,
  UserBundleWire,
} from './types'

// For the tests of the conversation keys: a server that applies the same rules as the real one (the next number, an envelope for
// exactly the people in the conversation at the keys they have now, no replacement of a young epoch that nothing asks to replace, an
// envelope shown only to its owner) and opens nothing, as the real one cannot. Not used by the app.

// How long an epoch stands before a member may replace it for no other reason, as on the server.
export const REPLACEMENT_INTERVAL_MS = 24 * 60 * 60 * 1000

interface StoredEpoch {
  epoch: number
  creatorId: string
  reason: EpochReason
  commitment: string
  signature: string
  madeAt: number
  envelopes: Map<string, { generation: number; envelope: string }>
}

interface FakeConversation {
  memberIds: Set<string>
  formerMemberIds: Set<string>
  keyEpoch: number
  keyRotationRequired: boolean
  epochs: StoredEpoch[]
}

export class FakeEpochServer {
  // The current bundle of each person who has keys.
  bundles = new Map<string, UserBundleWire>()
  conversations = new Map<string, FakeConversation>()
  calls: string[] = []

  // The time the server goes by, in milliseconds. A test moves it, or ages the epochs of a conversation, instead of waiting.
  now = Date.parse('2026-11-09T12:00:00Z')

  // Run just before a post is looked at: lets a test change the conversation after a browser has looked at it and before it posts.
  beforePost: ((conversationId: string) => void) | null = null

  // ##### Arranging #####

  registerBundle(userId: string, generation: number, bundle: string): void {
    this.bundles.set(userId, { userId, generation, bundle })
  }

  // A conversation as they were before keys: people, and no epoch.
  createConversation(id: string, memberIds: string[]): void {
    this.conversations.set(id, { memberIds: new Set(memberIds), formerMemberIds: new Set(), keyEpoch: 0, keyRotationRequired: false, epochs: [] })
  }

  addMember(id: string, userId: string): void {
    this.conversation(id).memberIds.add(userId)
    this.conversation(id).formerMemberIds.delete(userId)
  }

  // As the real one does: the person leaves, and the epoch in force must be replaced.
  removeMember(id: string, userId: string): void {
    const conversation = this.conversation(id)
    conversation.memberIds.delete(userId)
    conversation.formerMemberIds.add(userId)
    conversation.keyRotationRequired = true
  }

  // As after a removal, without the removal: the epoch in force must be replaced.
  requireRotation(id: string): void {
    this.conversation(id).keyRotationRequired = true
  }

  advance(ms: number): void {
    this.now += ms
  }

  // Dates the epochs of a conversation two days back, as if they had been made then.
  age(id: string): void {
    for (const stored of this.conversation(id).epochs) stored.madeAt -= 2 * REPLACEMENT_INTERVAL_MS
  }

  // Changes an epoch as stored, to see whether the people reading it notice.
  tamper(id: string, epoch: number, change: (stored: StoredEpoch) => void): void {
    change(this.conversation(id).epochs.find((stored) => stored.epoch === epoch)!)
  }

  // A conversation made with its first epoch, as the real server does when a browser starts one.
  start(callerId: string, id: string, memberIds: string[], body: EpochBody): void {
    this.createConversation(id, memberIds)
    try {
      this.store(callerId, id, body)
    } catch (failure) {
      this.conversations.delete(id)
      throw failure
    }
  }

  // People added to a group together with the epoch made for the group they join.
  addWithEpoch(callerId: string, id: string, userIds: string[], body: EpochBody): void {
    const conversation = this.conversation(id)
    const before = new Set(conversation.memberIds)
    for (const userId of userIds) this.addMember(id, userId)

    try {
      this.store(callerId, id, body, 'MemberAdded')
    } catch (failure) {
      conversation.memberIds = before
      throw failure
    }
  }

  // ##### The rules #####

  private conversation(id: string): FakeConversation {
    const found = this.conversations.get(id)
    if (!found) throw new ServerRefused(403)
    return found
  }

  // `alone` is an epoch stored on its own, with no change of members: the only kind that is held back when it comes too soon.
  private store(callerId: string, id: string, body: EpochBody, reason?: EpochReason, alone = false): void {
    const conversation = this.conversation(id)
    if (!conversation.memberIds.has(callerId)) throw new ServerRefused(403)

    if (body.epoch !== conversation.keyEpoch + 1) throw new EpochRefused('epoch-stale')

    const members = [...conversation.memberIds]
    if (members.some((userId) => !this.bundles.has(userId))) throw new EpochRefused('member-has-no-keys')

    const sealedFor = body.envelopes.map((envelope) => envelope.userId)
    const sameSet = sealedFor.length === members.length && members.every((userId) => sealedFor.includes(userId))
    if (!sameSet || body.envelopes.some((envelope) => envelope.generation !== this.bundles.get(envelope.userId)!.generation)) {
      throw new EpochRefused('epoch-members-changed')
    }

    const newest = conversation.epochs[conversation.epochs.length - 1]
    if (alone && newest !== undefined && !conversation.keyRotationRequired && this.now - newest.madeAt < REPLACEMENT_INTERVAL_MS) {
      throw new EpochRefused('epoch-too-soon')
    }

    conversation.epochs.push({
      epoch: body.epoch,
      creatorId: callerId,
      reason: reason ?? (body.epoch === 1 ? 'Created' : 'Rotated'),
      commitment: body.commitment,
      signature: body.signature,
      madeAt: this.now,
      envelopes: new Map(body.envelopes.map((envelope) => [envelope.userId, { generation: envelope.generation, envelope: envelope.envelope }])),
    })
    conversation.keyEpoch = body.epoch
    conversation.keyRotationRequired = false
  }

  // ##### What a browser asks #####

  as(callerId: string): EpochApi {
    return {
      lookupKeys: async (userIds): Promise<UserBundleWire[]> => {
        this.calls.push('lookupKeys')
        return userIds.flatMap((userId) => {
          const bundle = this.bundles.get(userId)
          return bundle ? [bundle] : []
        })
      },

      getKeyState: async (id): Promise<ConversationKeyState> => {
        this.calls.push('getKeyState')
        const conversation = this.conversation(id)
        if (!conversation.memberIds.has(callerId)) throw new ServerRefused(403)
        const newest = conversation.epochs[conversation.epochs.length - 1]
        return {
          keyEpoch: conversation.keyEpoch,
          keyRotationRequired: conversation.keyRotationRequired,
          keyEpochAt: newest === undefined ? null : new Date(newest.madeAt).toISOString(),
        }
      },

      getMemberIds: async (id): Promise<string[]> => {
        this.calls.push('getMemberIds')
        const conversation = this.conversation(id)
        if (!conversation.memberIds.has(callerId)) throw new ServerRefused(403)
        return [...conversation.memberIds]
      },

      getEpochs: async (id, from, limit = 20): Promise<EpochPageWire> => {
        this.calls.push('getEpochs')
        const conversation = this.conversation(id)
        if (!conversation.memberIds.has(callerId) && !conversation.formerMemberIds.has(callerId)) throw new ServerRefused(403)

        const mine = conversation.epochs.filter((stored) => stored.epoch >= from && stored.envelopes.has(callerId))
        const page: EpochRecordWire[] = mine.slice(0, limit).map((stored) => ({
          epoch: stored.epoch,
          creatorId: stored.creatorId,
          reason: stored.reason,
          createdAt: new Date(stored.madeAt).toISOString(),
          commitment: stored.commitment,
          signature: stored.signature,
          members: [...stored.envelopes.entries()]
            .map(([userId, held]) => ({ userId, generation: held.generation }))
            .sort((a, b) => (a.userId < b.userId ? -1 : 1)),
          generation: stored.envelopes.get(callerId)!.generation,
          envelope: stored.envelopes.get(callerId)!.envelope,
        }))

        return { epochs: page, hasMore: mine.length > limit }
      },

      postEpoch: async (id, body): Promise<void> => {
        this.calls.push('postEpoch')
        this.beforePost?.(id)
        this.store(callerId, id, body, undefined, true)
      },
    }
  }
}

export class MemoryEpochKeyStore implements EpochKeyStore {
  readonly records = new Map<string, Uint8Array>()
  failNextSave: Error | null = null

  private static id(userId: string, conversationId: string, epoch: number) {
    return `${userId}|${conversationId}|${epoch}`
  }

  async load(userId: string, conversationId: string, epoch: number): Promise<Uint8Array | null> {
    return this.records.get(MemoryEpochKeyStore.id(userId, conversationId, epoch)) ?? null
  }

  async save(userId: string, conversationId: string, epoch: number, key: Uint8Array): Promise<void> {
    if (this.failNextSave) {
      const failure = this.failNextSave
      this.failNextSave = null
      throw failure
    }
    this.records.set(MemoryEpochKeyStore.id(userId, conversationId, epoch), key)
  }

  async highest(userId: string, conversationId: string): Promise<number> {
    const prefix = `${userId}|${conversationId}|`
    return Math.max(0, ...[...this.records.keys()].filter((id) => id.startsWith(prefix)).map((id) => Number(id.slice(prefix.length))))
  }

  async removeAll(userId: string): Promise<void> {
    for (const id of [...this.records.keys()]) if (id.startsWith(`${userId}|`)) this.records.delete(id)
  }
}

export class MemoryPinStore implements PinStore {
  readonly pins = new Map<string, Pin>()

  async load(ownerId: string, contactId: string): Promise<Pin | null> {
    return this.pins.get(`${ownerId}|${contactId}`) ?? null
  }

  async save(pin: Pin): Promise<void> {
    this.pins.set(`${pin.ownerId}|${pin.contactId}`, pin)
  }

  async removeAll(ownerId: string): Promise<void> {
    for (const id of [...this.pins.keys()]) if (id.startsWith(`${ownerId}|`)) this.pins.delete(id)
  }
}
