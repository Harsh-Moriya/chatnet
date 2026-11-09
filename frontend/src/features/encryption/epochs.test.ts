import { describe, expect, it } from 'vitest'
import {
  type AccountSecrets,
  CryptoError,
  createAccountSecrets,
  currentAccountKey,
  epochCommitment,
  fromBase64Url,
  newEpochKey,
  sealEnvelope,
  signBundle,
  signEpoch,
  toBase64Url,
} from '../../lib/crypto'
import {
  EpochContestedError,
  type EpochDeps,
  EpochRefused,
  MAX_ATTEMPTS,
  MissingKeysError,
  buildEpoch,
  ensureEpoch,
  keepNewConversationKey,
  loadEpochKeys,
  readyConversationWith,
  prepareAddingMembers,
  prepareNewConversation,
  resolvePeers,
  saveKey,
} from './epochs'
import { FakeEpochServer, MemoryEpochKeyStore, MemoryPinStore } from './fakeEpochServer'
import { ServerRefused } from './fakeKeysServer'
import { KeysMismatchError } from './flows'
import { IdentityChangedError, trustNewIdentity } from './pins'
import type { EpochApi } from './types'

// The conversation keys against a fake server that applies the real server's rules, with the real cryptography: a key made by one
// person is opened by another, and every way a server or a member could hand out a key that is not what it claims is noticed.

interface Person {
  id: string
  secrets: AccountSecrets
  keys: MemoryEpochKeyStore
  pins: MemoryPinStore
  deps: EpochDeps
}

async function bundleOf(userId: string, secrets: AccountSecrets): Promise<string> {
  return toBase64Url(await signBundle(userId, secrets.identity, currentAccountKey(secrets)))
}

async function newPerson(server: FakeEpochServer, options: { register?: boolean } = {}): Promise<Person> {
  const id = crypto.randomUUID()
  const secrets = await createAccountSecrets()
  if (options.register !== false) server.registerBundle(id, 1, await bundleOf(id, secrets))
  return personFrom(server.as(id), id, secrets)
}

function personFrom(api: EpochApi, id: string, secrets: AccountSecrets): Person {
  const keys = new MemoryEpochKeyStore()
  const pins = new MemoryPinStore()
  return { id, secrets, keys, pins, deps: { api, keys, pins, self: { userId: id, secrets } } }
}

// A conversation started by `creator` for these people, as a browser starts one.
async function start(server: FakeEpochServer, creator: Person, others: Person[]): Promise<string> {
  const prepared = await prepareNewConversation(creator.deps, others.map((person) => person.id))
  server.start(creator.id, prepared.conversationId, [creator.id, ...others.map((person) => person.id)], prepared.body)
  await keepNewConversationKey(creator.deps, prepared, prepared.conversationId)
  return prepared.conversationId
}

// A conversation that existed before it had keys.
const LEGACY = 'e1e1e1e1-0000-4000-8000-000000000001'

const keyOf = (person: Person, conversationId: string, epoch: number) => person.keys.load(person.id, conversationId, epoch)

// The page the server gives a person, changed on its way.
function serving(server: FakeEpochServer, person: Person, change: (page: Awaited<ReturnType<EpochApi['getEpochs']>>) => void): Person {
  const api = server.as(person.id)
  const altered: EpochApi = {
    ...api,
    getEpochs: async (...args) => {
      const page = await api.getEpochs(...args)
      change(page)
      return page
    },
  }
  return { ...person, deps: { ...person.deps, api: altered } }
}

describe('a key made by one person and opened by another', () => {
  it('reaches the other person as the same key, and is checked on the way', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    const conversationId = await start(server, ana, [ben])

    const loaded = await loadEpochKeys(ben.deps, conversationId)

    expect(loaded).toEqual({ held: [1], rejected: [] })
    const anasKey = await keyOf(ana, conversationId, 1)
    expect(anasKey).not.toBeNull()
    expect(await keyOf(ben, conversationId, 1)).toEqual(anasKey)
    expect(anasKey).toHaveLength(32)
  })

  it('is sealed once for each person, and each can open only their own', async () => {
    const server = new FakeEpochServer()
    const [ana, ben, cy] = [await newPerson(server), await newPerson(server), await newPerson(server)]
    const conversationId = await start(server, ana, [ben, cy])

    await loadEpochKeys(ben.deps, conversationId)
    await loadEpochKeys(cy.deps, conversationId)

    const key = await keyOf(ana, conversationId, 1)
    expect(await keyOf(ben, conversationId, 1)).toEqual(key)
    expect(await keyOf(cy, conversationId, 1)).toEqual(key)
    expect(server.conversations.get(conversationId)!.epochs[0].envelopes.size).toBe(3)
  })

  it('is kept by the person who made it only when the conversation is the one prepared', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    const prepared = await prepareNewConversation(ana.deps, [ben.id])

    await keepNewConversationKey(ana.deps, prepared, crypto.randomUUID())

    expect(await keyOf(ana, prepared.conversationId, 1)).toBeNull()
  })

  it('is not lost when it cannot be kept, because the envelope for its maker is on the server', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    const prepared = await prepareNewConversation(ana.deps, [ben.id])
    server.start(ana.id, prepared.conversationId, [ana.id, ben.id], prepared.body)
    ana.keys.failNextSave = new Error('the disk is full')

    await expect(keepNewConversationKey(ana.deps, prepared, prepared.conversationId)).resolves.toBeUndefined()
    const recovered = await loadEpochKeys(ana.deps, prepared.conversationId)

    expect(recovered.held).toEqual([1])
    expect(await keyOf(ana, prepared.conversationId, 1)).toEqual(prepared.key)
  })

  it('holds only the epochs the person has an envelope for', async () => {
    const server = new FakeEpochServer()
    const [ana, ben, cy] = [await newPerson(server), await newPerson(server), await newPerson(server)]
    const conversationId = await start(server, ana, [ben])
    const addition = await prepareAddingMembers(ana.deps, conversationId, [cy.id])
    server.addWithEpoch(ana.id, conversationId, [cy.id], addition.body)
    await saveKey(ana.deps, conversationId, 2, addition.key)

    const newcomer = await loadEpochKeys(cy.deps, conversationId)
    const existing = await loadEpochKeys(ben.deps, conversationId)

    expect(addition.body.epoch).toBe(2)
    expect(newcomer).toEqual({ held: [2], rejected: [] })
    expect(await keyOf(cy, conversationId, 1)).toBeNull()
    expect(existing.held).toEqual([1, 2])
    expect(await keyOf(ben, conversationId, 2)).toEqual(addition.key)
  })

  it('picks up where it left off', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    const conversationId = await start(server, ana, [ben])
    await loadEpochKeys(ben.deps, conversationId)
    server.requireRotation(conversationId)
    await ensureEpoch(ana.deps, conversationId)
    server.calls.length = 0

    const later = await loadEpochKeys(ben.deps, conversationId)

    expect(later.held).toEqual([2])
    expect(server.calls.filter((call) => call === 'getEpochs')).toHaveLength(1)
  })

  it('reads every page when there are more than one', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    const conversationId = await start(server, ana, [ben])
    for (let round = 0; round < 3; round++) {
      server.requireRotation(conversationId)
      await ensureEpoch(ana.deps, conversationId)
    }
    const small: EpochApi = { ...server.as(ben.id), getEpochs: (id, from) => server.as(ben.id).getEpochs(id, from, 2) }

    const loaded = await loadEpochKeys({ ...ben.deps, api: small }, conversationId)

    expect(loaded.held).toEqual([1, 2, 3, 4])
    expect(loaded.rejected).toEqual([])
  })
})

describe('an epoch that is not what it claims', () => {
  async function pair() {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    return { server, ana, ben, conversationId: await start(server, ana, [ben]) }
  }

  it('is refused when the envelope is not the reader\'s', async () => {
    const { server, ana, ben, conversationId } = await pair()
    server.tamper(conversationId, 1, (stored) => {
      stored.envelopes.get(ben.id)!.envelope = stored.envelopes.get(ana.id)!.envelope
    })

    const loaded = await loadEpochKeys(ben.deps, conversationId)

    expect(loaded).toEqual({ held: [], rejected: [{ epoch: 1, reason: 'badEnvelope' }] })
    expect(await keyOf(ben, conversationId, 1)).toBeNull()
  })

  it('is refused when the signature is not the creator\'s', async () => {
    const { server, ben, conversationId } = await pair()
    server.tamper(conversationId, 1, (stored) => {
      const bytes = fromBase64Url(stored.signature)
      bytes[0] ^= 1
      stored.signature = toBase64Url(bytes)
    })

    const loaded = await loadEpochKeys(ben.deps, conversationId)

    expect(loaded.rejected.map((r) => r.reason)).toEqual(['badSignature'])
  })

  it('is refused when it commits to a different key than the one sealed', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    const conversationId = crypto.randomUUID()
    const sealed = newEpochKey()
    const committedTo = await epochCommitment(conversationId, 1, newEpochKey())
    const members = [ana, ben]
    const envelopes = await Promise.all(
      members.map(async (person) => ({
        userId: person.id,
        generation: 1,
        envelope: toBase64Url(
          await sealEnvelope({ conversationId, epoch: 1, userId: person.id, generation: 1 }, currentAccountKey(person.secrets).publicKey, sealed),
        ),
      })),
    )
    const signature = await signEpoch(
      { conversationId, epoch: 1, creatorId: ana.id, commitment: committedTo, members: members.map((person) => ({ userId: person.id, generation: 1 })) },
      ana.secrets.identity,
    )
    server.start(ana.id, conversationId, [ana.id, ben.id], {
      epoch: 1,
      commitment: toBase64Url(committedTo),
      signature: toBase64Url(signature),
      envelopes,
    })

    const loaded = await loadEpochKeys(ben.deps, conversationId)

    expect(loaded.rejected.map((r) => r.reason)).toEqual(['badCommitment'])
  })

  it('is refused when the creator is not one of the people it was made for', async () => {
    const { server, ben, conversationId } = await pair()
    server.tamper(conversationId, 1, (stored) => {
      stored.creatorId = crypto.randomUUID()
    })

    expect((await loadEpochKeys(ben.deps, conversationId)).rejected.map((r) => r.reason)).toEqual(['creatorNotMember'])
  })

  it('is refused when the list of people is not the one that was signed', async () => {
    const { server, ben, conversationId } = await pair()
    const reader = serving(server, ben, (page) => {
      page.epochs[0].members.push({ userId: crypto.randomUUID(), generation: 1 })
    })

    expect((await loadEpochKeys(reader.deps, conversationId)).rejected.map((r) => r.reason)).toEqual(['badSignature'])
  })

  it('is refused, not thrown at the reader, when the list of people names someone twice', async () => {
    const { server, ben, conversationId } = await pair()
    const reader = serving(server, ben, (page) => {
      page.epochs[0].members.push({ ...page.epochs[0].members[0] })
    })

    expect((await loadEpochKeys(reader.deps, conversationId)).rejected.map((r) => r.reason)).toEqual(['badSignature'])
  })

  it.each(['commitment', 'signature', 'envelope'] as const)('is refused when the %s is not base64url', async (field) => {
    const { server, ben, conversationId } = await pair()
    const reader = serving(server, ben, (page) => {
      page.epochs[0][field] = 'not base64url!'
    })

    expect((await loadEpochKeys(reader.deps, conversationId)).rejected.map((r) => r.reason)).toEqual(['malformed'])
  })

  it('is refused when it does not list the reader at the key their envelope is for', async () => {
    const { server, ben, conversationId } = await pair()
    const reader = serving(server, ben, (page) => {
      page.epochs[0].generation = 7
    })

    expect((await loadEpochKeys(reader.deps, conversationId)).rejected.map((r) => r.reason)).toEqual(['notListed'])
  })

  it('is refused when it was sealed to a key generation the reader does not hold', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    // Ben's keys changed on another device: the server offers a newer bundle than any this browser has the secrets for.
    const newer = await createAccountSecrets()
    const newerBundle = toBase64Url(
      await signBundle(ben.id, ben.secrets.identity, { ...currentAccountKey(newer), generation: 2 }),
    )
    server.registerBundle(ben.id, 2, newerBundle)
    const conversationId = await start(server, ana, [ben])

    expect((await loadEpochKeys(ben.deps, conversationId)).rejected.map((r) => r.reason)).toEqual(['noKey'])
  })

  it('is refused when its creator has no keys any more', async () => {
    const { server, ana, ben, conversationId } = await pair()
    server.bundles.delete(ana.id)

    expect((await loadEpochKeys(ben.deps, conversationId)).rejected.map((r) => r.reason)).toEqual(['creatorUnknown'])
  })

  it('is refused when it was not made for the people who are in the conversation now', async () => {
    const { ben, ana, conversationId } = await pair()

    const loaded = await loadEpochKeys(ben.deps, conversationId, { newest: { epoch: 1, userIds: [ana.id, ben.id, crypto.randomUUID()] } })

    expect(loaded.rejected.map((r) => r.reason)).toEqual(['membersMismatch'])
  })

  it('is judged on the newest epoch only when the people are given', async () => {
    const { ben, conversationId } = await pair()

    const loaded = await loadEpochKeys(ben.deps, conversationId, { newest: { epoch: 2, userIds: ['nobody'] } })

    expect(loaded.held).toEqual([1])
  })

  it('names a creator whose identity changed since they were first seen, and is accepted once the reader trusts the new one', async () => {
    const { server, ana, ben, conversationId } = await pair()
    await loadEpochKeys(ben.deps, conversationId)
    // Ana resets her encryption: the same account, new keys.
    const reset = await createAccountSecrets()
    server.registerBundle(ana.id, 1, await bundleOf(ana.id, reset))
    const anaAfter = personFrom(server.as(ana.id), ana.id, reset)
    server.requireRotation(conversationId)
    await ensureEpoch(anaAfter.deps, conversationId)

    const blocked = await loadEpochKeys(ben.deps, conversationId)

    expect(blocked.held).toEqual([])
    expect(blocked.rejected).toHaveLength(1)
    expect(blocked.rejected[0].reason).toBe('identityChanged')
    const changed = blocked.rejected[0].identityChanged!
    expect(changed).toBeInstanceOf(IdentityChangedError)
    expect(changed.userId).toBe(ana.id)

    await trustNewIdentity(ben.pins, ben.id, ana.id, changed.seen)
    const accepted = await loadEpochKeys(ben.deps, conversationId, { from: 2 })

    expect(accepted).toEqual({ held: [2], rejected: [] })
    expect(await keyOf(ben, conversationId, 2)).toEqual(await keyOf(anaAfter, conversationId, 2))
  })

  it('does not stop at a bad epoch: the ones after it are still read', async () => {
    const { server, ana, ben, conversationId } = await pair()
    server.requireRotation(conversationId)
    await ensureEpoch(ana.deps, conversationId)
    server.tamper(conversationId, 1, (stored) => {
      stored.creatorId = crypto.randomUUID()
    })

    const loaded = await loadEpochKeys(ben.deps, conversationId)

    expect(loaded.held).toEqual([2])
    expect(loaded.rejected.map((r) => r.epoch)).toEqual([1])
  })
})

describe('making sure a conversation has a key', () => {
  it('makes the first epoch of a conversation that had none, and the others find it', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    server.createConversation(LEGACY, [ana.id, ben.id])

    const first = await ensureEpoch(ana.deps, LEGACY)
    const second = await ensureEpoch(ben.deps, LEGACY)

    expect(first).toEqual({ epoch: 1, created: true })
    expect(second).toEqual({ epoch: 1, created: false })
    expect(await keyOf(ben, LEGACY, 1)).toEqual(await keyOf(ana, LEGACY, 1))
    expect(server.conversations.get(LEGACY)!.epochs[0].reason).toBe('Created')
  })

  it('does nothing when the epoch is current and already held', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    const conversationId = await start(server, ana, [ben])
    server.calls.length = 0

    const result = await ensureEpoch(ana.deps, conversationId)

    expect(result).toEqual({ epoch: 1, created: false })
    expect(server.calls).not.toContain('postEpoch')
    expect(server.calls).not.toContain('lookupKeys')
  })

  it('replaces the epoch after someone left, for the people who remain, and the person who left is not given it', async () => {
    const server = new FakeEpochServer()
    const [ana, ben, cy] = [await newPerson(server), await newPerson(server), await newPerson(server)]
    const conversationId = await start(server, ana, [ben, cy])
    server.removeMember(conversationId, cy.id)

    const result = await ensureEpoch(ben.deps, conversationId)
    const forCy = await loadEpochKeys(cy.deps, conversationId)

    expect(result).toEqual({ epoch: 2, created: true })
    expect([...server.conversations.get(conversationId)!.epochs[1].envelopes.keys()].sort()).toEqual([ana.id, ben.id].sort())
    expect(server.conversations.get(conversationId)!.keyRotationRequired).toBe(false)
    expect(forCy.held).toEqual([1])
    expect(await keyOf(cy, conversationId, 2)).toBeNull()
    expect((await ensureEpoch(ana.deps, conversationId)).created).toBe(false)
    expect(await keyOf(ana, conversationId, 2)).toEqual(await keyOf(ben, conversationId, 2))
  })

  it('leaves one epoch when two members make it together, and both end up holding it', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    server.createConversation(LEGACY, [ana.id, ben.id])

    const results = await Promise.all([ensureEpoch(ana.deps, LEGACY), ensureEpoch(ben.deps, LEGACY)])

    expect(results.filter((r) => r.created)).toHaveLength(1)
    expect(results.every((r) => r.epoch === 1)).toBe(true)
    expect(server.conversations.get(LEGACY)!.epochs).toHaveLength(1)
    expect(await keyOf(ben, LEGACY, 1)).toEqual(await keyOf(ana, LEGACY, 1))
  })

  it('makes the epoch again, for the new person too, when someone joins while it is being made', async () => {
    const server = new FakeEpochServer()
    const [ana, ben, cy] = [await newPerson(server), await newPerson(server), await newPerson(server)]
    server.createConversation(LEGACY, [ana.id, ben.id])
    let joined = false
    server.beforePost = (id) => {
      if (joined) return
      joined = true
      server.addMember(id, cy.id)
    }

    const result = await ensureEpoch(ana.deps, LEGACY)

    expect(result).toEqual({ epoch: 1, created: true })
    expect(server.calls.filter((call) => call === 'postEpoch')).toHaveLength(2)
    expect([...server.conversations.get(LEGACY)!.epochs[0].envelopes.keys()].sort()).toEqual([ana.id, ben.id, cy.id].sort())
  })

  it('gives up, saying so, when the conversation keeps changing', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    server.createConversation(LEGACY, [ana.id, ben.id])
    server.beforePost = (id) => {
      server.conversations.get(id)!.keyEpoch += 1
    }

    await expect(ensureEpoch(ana.deps, LEGACY)).rejects.toBeInstanceOf(EpochContestedError)

    expect(server.calls.filter((call) => call === 'postEpoch')).toHaveLength(MAX_ATTEMPTS)
  })

  it('does not retry what retrying cannot fix', async () => {
    const server = new FakeEpochServer()
    const [ana, ben, outsider] = [await newPerson(server), await newPerson(server), await newPerson(server)]
    server.createConversation(LEGACY, [ana.id, ben.id])

    await expect(ensureEpoch(outsider.deps, LEGACY)).rejects.toBeInstanceOf(ServerRefused)

    expect(server.calls.filter((call) => call === 'getKeyState')).toHaveLength(1)
  })

  it('names the people who have not set up encryption and makes nothing', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server, { register: false })]
    server.createConversation(LEGACY, [ana.id, ben.id])

    const failure = await ensureEpoch(ana.deps, LEGACY).catch((error: unknown) => error)

    expect(failure).toBeInstanceOf(MissingKeysError)
    expect((failure as MissingKeysError).userIds).toEqual([ben.id])
    expect(server.calls).not.toContain('postEpoch')
  })

  it('replaces an epoch that does not check out, as any member may', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    const conversationId = await start(server, ana, [ben])
    // The server says the epoch is current but the people it lists are not the people it was signed for.
    const lying = serving(server, ben, (page) => {
      page.epochs[0].members = [page.epochs[0].members[0]]
    })

    const result = await ensureEpoch(lying.deps, conversationId)

    expect(result).toEqual({ epoch: 2, created: true })
  })

  it('refuses to seal a key to someone whose identity is not the one first seen', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    server.createConversation(LEGACY, [ana.id, ben.id])
    await ensureEpoch(ana.deps, LEGACY)
    const reset = await createAccountSecrets()
    server.registerBundle(ben.id, 1, await bundleOf(ben.id, reset))
    server.requireRotation(LEGACY)

    const failure = await ensureEpoch(ana.deps, LEGACY).catch((error: unknown) => error)

    expect(failure).toBeInstanceOf(IdentityChangedError)
    expect((failure as IdentityChangedError).userId).toBe(ben.id)
    expect(server.conversations.get(LEGACY)!.epochs).toHaveLength(1)
  })
})

describe('making a conversation ready as it is opened', () => {
  it('asks the server nothing when the newest epoch is current and its key is held', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    const conversationId = await start(server, ana, [ben])
    server.calls.length = 0

    await readyConversationWith(ana.deps, conversationId, { keyEpoch: 1, keyRotationRequired: false })

    expect(server.calls).toEqual([])
  })

  it('opens the envelope of an epoch this browser has not read yet', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    const conversationId = await start(server, ana, [ben])

    await readyConversationWith(ben.deps, conversationId, { keyEpoch: 1, keyRotationRequired: false })

    expect(await keyOf(ben, conversationId, 1)).toEqual(await keyOf(ana, conversationId, 1))
    expect(server.calls).not.toContain('postEpoch')
  })

  it('makes the first epoch of a conversation from before keys, and the next after someone left', async () => {
    const server = new FakeEpochServer()
    const [ana, ben, cy] = [await newPerson(server), await newPerson(server), await newPerson(server)]
    server.createConversation(LEGACY, [ana.id, ben.id, cy.id])

    await readyConversationWith(ana.deps, LEGACY, { keyEpoch: 0, keyRotationRequired: false })
    server.removeMember(LEGACY, cy.id)
    await readyConversationWith(ben.deps, LEGACY, { keyEpoch: 1, keyRotationRequired: true })

    expect(server.conversations.get(LEGACY)!.keyEpoch).toBe(2)
    expect(await keyOf(ben, LEGACY, 2)).not.toBeNull()
  })

  it('does not trust what it was told when the key it holds is for an older epoch', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    const conversationId = await start(server, ana, [ben])
    server.requireRotation(conversationId)
    await ensureEpoch(ben.deps, conversationId)

    await readyConversationWith(ana.deps, conversationId, { keyEpoch: 2, keyRotationRequired: false })

    expect(await keyOf(ana, conversationId, 2)).toEqual(await keyOf(ben, conversationId, 2))
  })
})

describe('adding people', () => {
  it('prepares the next epoch for the people there now and the ones named', async () => {
    const server = new FakeEpochServer()
    const [ana, ben, cy] = [await newPerson(server), await newPerson(server), await newPerson(server)]
    const conversationId = await start(server, ana, [ben])

    const prepared = await prepareAddingMembers(ana.deps, conversationId, [cy.id, ben.id])

    expect(prepared.body.epoch).toBe(2)
    expect(prepared.body.envelopes.map((e) => e.userId).sort()).toEqual([ana.id, ben.id, cy.id].sort())
  })

  it('is refused by the server, and changes nothing, if it was made for fewer people than will be there', async () => {
    const server = new FakeEpochServer()
    const [ana, ben, cy] = [await newPerson(server), await newPerson(server), await newPerson(server)]
    const conversationId = await start(server, ana, [ben])
    const withoutCy = await prepareAddingMembers(ana.deps, conversationId, [])

    expect(() => server.addWithEpoch(ana.id, conversationId, [cy.id], withoutCy.body)).toThrow(EpochRefused)

    expect([...server.conversations.get(conversationId)!.memberIds].sort()).toEqual([ana.id, ben.id].sort())
    expect(server.conversations.get(conversationId)!.keyEpoch).toBe(1)
  })

  it('is made again when it is retried after the group changed', async () => {
    const server = new FakeEpochServer()
    const [ana, ben, cy, dee] = [await newPerson(server), await newPerson(server), await newPerson(server), await newPerson(server)]
    const conversationId = await start(server, ana, [ben])
    const stale = await prepareAddingMembers(ana.deps, conversationId, [cy.id])
    server.addMember(conversationId, dee.id)

    expect(() => server.addWithEpoch(ana.id, conversationId, [cy.id], stale.body)).toThrow(EpochRefused)
    const fresh = await prepareAddingMembers(ana.deps, conversationId, [cy.id])
    server.addWithEpoch(ana.id, conversationId, [cy.id], fresh.body)

    expect(fresh.body.envelopes).toHaveLength(4)
    expect(server.conversations.get(conversationId)!.keyEpoch).toBe(2)
  })
})

describe('the keys of other people', () => {
  it('are taken from the server only for the people asked about, at the generation they were signed for', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    const stranger = await newPerson(server)
    const api = server.as(ana.id)
    const noisy: EpochApi = {
      ...api,
      lookupKeys: async (ids) => [...(await api.lookupKeys([...ids, stranger.id]))],
    }

    const peers = await resolvePeers({ ...ana.deps, api: noisy }, [ana.id, ben.id])

    expect([...peers.keys()].sort()).toEqual([ana.id, ben.id].sort())
  })

  it('are refused when the generation the server states is not the one that was signed', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    server.bundles.get(ben.id)!.generation = 3

    await expect(resolvePeers(ana.deps, [ana.id, ben.id])).rejects.toBeInstanceOf(CryptoError)
  })

  it('are refused when the bundle is not signed by the identity key it carries', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    const bytes = fromBase64Url(server.bundles.get(ben.id)!.bundle)
    bytes[10] ^= 1
    server.bundles.get(ben.id)!.bundle = toBase64Url(bytes)

    await expect(resolvePeers(ana.deps, [ben.id])).rejects.toMatchObject({ code: 'authentication' })
  })

  it('are refused when the server\'s bundle for the person asking is not the one they hold', async () => {
    const server = new FakeEpochServer()
    const ana = await newPerson(server)
    const impostor = await createAccountSecrets()
    server.registerBundle(ana.id, 1, await bundleOf(ana.id, impostor))

    await expect(resolvePeers(ana.deps, [ana.id])).rejects.toBeInstanceOf(KeysMismatchError)
  })

  it('are refused when the identity of someone is not the one first seen', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    await resolvePeers(ana.deps, [ben.id])
    server.registerBundle(ben.id, 1, await bundleOf(ben.id, await createAccountSecrets()))

    await expect(resolvePeers(ana.deps, [ben.id])).rejects.toBeInstanceOf(IdentityChangedError)
  })

  it('are kept apart per person, so one person\'s pins say nothing about another\'s', async () => {
    const server = new FakeEpochServer()
    const [ana, ben, cy] = [await newPerson(server), await newPerson(server), await newPerson(server)]
    await resolvePeers(ana.deps, [cy.id])
    server.registerBundle(cy.id, 1, await bundleOf(cy.id, await createAccountSecrets()))

    await expect(resolvePeers(ben.deps, [cy.id])).resolves.toBeInstanceOf(Map)
  })
})

describe('building an epoch', () => {
  it('seals a different envelope for each person to the same key and signs who it was made for', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    const peers = await resolvePeers(ana.deps, [ana.id, ben.id])

    const built = await buildEpoch(ana.deps, crypto.randomUUID(), 4, [...peers.values()])

    expect(built.body.epoch).toBe(4)
    expect(built.body.envelopes).toHaveLength(2)
    expect(new Set(built.body.envelopes.map((e) => e.envelope)).size).toBe(2)
    expect(fromBase64Url(built.body.commitment)).toHaveLength(32)
    expect(fromBase64Url(built.body.signature)).toHaveLength(64)
    expect(built.key).toHaveLength(32)
  })
})
