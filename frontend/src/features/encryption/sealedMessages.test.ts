import { describe, expect, it } from 'vitest'
import { fromBase64Url, newEd25519, toBase64Url } from '../../lib/crypto'
import { EpochContestedError, EpochRefused, MAX_ATTEMPTS, ensureEpoch, loadEpochKeys, prepareAddingMembers, saveKey } from './epochs'
import { type Person, newPerson, start } from './epochTestKit'
import { FakeEpochServer } from './fakeEpochServer'
import {
  EpochKeyMissingError,
  type SealedMessage,
  createMessageOpener,
  sealText,
  underCurrentEpoch,
} from './sealedMessages'

// Messages against a fake server that applies the real server's rules, with the real cryptography: what one person seals the others
// open, and every way a blob can be something other than it claims is reported as a state of the message and never as a failure.

const LEGACY = 'e1e1e1e1-0000-4000-8000-000000000002'

async function conversationOf(people: number) {
  const server = new FakeEpochServer()
  const everyone: Person[] = []
  for (let i = 0; i < people; i++) everyone.push(await newPerson(server))
  const id = await start(server, everyone[0], everyone.slice(1))
  return { server, everyone, id }
}

let counter = 0

// What a person's browser would store and send for this text, as the server would hand it back to someone else.
async function messageFrom(
  sender: Person,
  conversationId: string,
  text: string,
  options: { epoch?: number; editCount?: number; clientMessageId?: string } = {},
): Promise<SealedMessage> {
  const clientMessageId = options.clientMessageId ?? `00000000-0000-4000-8000-${String(++counter).padStart(12, '0')}`
  const editCount = options.editCount ?? 0
  const epoch = options.epoch ?? 1
  const sealed = await sealText(sender.deps, { conversationId, clientMessageId, editCount, epoch, text })

  return { conversationId, senderId: sender.id, clientMessageId, editCount, keyEpoch: sealed.keyEpoch, ciphertext: sealed.ciphertext }
}

const callsTo = (server: FakeEpochServer, name: string) => server.calls.filter((call) => call === name).length

describe('a message sealed by one person and opened by another', () => {
  it('reaches the other person as the same text, and the sender reads their own', async () => {
    const { everyone: [ana, ben], id } = await conversationOf(2)
    const message = await messageFrom(ana, id, 'Hello, Ben')

    expect(await createMessageOpener(ben.deps).open(message)).toEqual({ readable: true, text: 'Hello, Ben' })
    expect(await createMessageOpener(ana.deps).open(message)).toEqual({ readable: true, text: 'Hello, Ben' })
  })

  it('keeps any text as it was written, whatever it holds', async () => {
    const { everyone: [ana, ben], id } = await conversationOf(2)
    const opener = createMessageOpener(ben.deps)

    for (const text of ['', 'a', 'こんにちは 👩🏽‍💻 é', 'x'.repeat(4000), '{"text":"nested"}', ' padded ']) {
      expect(await opener.open(await messageFrom(ana, id, text))).toEqual({ readable: true, text })
    }
  })

  it('is a blob that starts with the version, the suite and the epoch it was sealed in', async () => {
    const { everyone: [ana], id } = await conversationOf(1)

    const blob = fromBase64Url((await messageFrom(ana, id, 'hi')).ciphertext)

    expect([...blob.subarray(0, 2)]).toEqual([1, 1])
    expect(new DataView(blob.buffer, blob.byteOffset).getUint32(2)).toBe(1)
  })

  it('does not repeat the fetches for the next messages of a conversation', async () => {
    const { server, everyone: [ana, ben], id } = await conversationOf(2)
    const opener = createMessageOpener(ben.deps)
    await opener.open(await messageFrom(ana, id, 'one'))
    const before = { epochs: callsTo(server, 'getEpochs'), lookups: callsTo(server, 'lookupKeys') }

    for (let i = 0; i < 5; i++) {
      expect(await opener.open(await messageFrom(ana, id, `more ${i}`))).toMatchObject({ readable: true })
    }

    expect({ epochs: callsTo(server, 'getEpochs'), lookups: callsTo(server, 'lookupKeys') }).toEqual(before)
  })

  it('asks for the keys once when a page of messages is opened at once', async () => {
    const { server, everyone: [ana, ben], id } = await conversationOf(2)
    const opener = createMessageOpener(ben.deps)
    const page = await Promise.all(Array.from({ length: 6 }, (_, i) => messageFrom(ana, id, `message ${i}`)))
    server.calls.length = 0

    const opened = await Promise.all(page.map((message) => opener.open(message)))

    expect(opened.every((result) => result.readable)).toBe(true)
    expect(callsTo(server, 'getEpochs')).toBe(1)
  })

  it('opens a message of an older epoch when only a newer one is held', async () => {
    const { server, everyone: [ana, ben], id } = await conversationOf(2)
    const old = await messageFrom(ana, id, 'from the first epoch')
    server.requireRotation(id)
    expect(await ensureEpoch(ana.deps, id)).toEqual({ epoch: 2, created: true })
    await loadEpochKeys(ben.deps, id, { from: 2 })
    expect(await ben.keys.load(ben.id, id, 1)).toBeNull()

    expect(await createMessageOpener(ben.deps).open(old)).toEqual({ readable: true, text: 'from the first epoch' })
  })

  it('opens what was sealed under a later epoch after the keys are fetched', async () => {
    const { server, everyone: [ana, ben], id } = await conversationOf(2)
    server.requireRotation(id)
    await ensureEpoch(ana.deps, id)

    const message = await messageFrom(ana, id, 'second epoch', { epoch: 2 })

    expect(message.keyEpoch).toBe(2)
    expect(await createMessageOpener(ben.deps).open(message)).toEqual({ readable: true, text: 'second epoch' })
  })
})

describe('a message that cannot be shown', () => {
  it('is reported as having no key when the server has none to give this person', async () => {
    const { server, everyone: [ana, ben], id } = await conversationOf(2)
    const dee = await newPerson(server)
    const added = await prepareAddingMembers(ana.deps, id, [dee.id])
    server.addWithEpoch(ana.id, id, [dee.id], added.body)
    await saveKey(ana.deps, id, added.body.epoch, added.key)
    const before = await messageFrom(ana, id, 'said before dee joined')
    const opener = createMessageOpener(dee.deps)

    expect(await opener.open(before)).toEqual({ readable: false, reason: 'noKey' })
    expect(await createMessageOpener(ben.deps).open(before)).toMatchObject({ readable: true })
  })

  it('is not asked for again until the conversation is told to forget what was missing', async () => {
    const { server, everyone: [ana], id } = await conversationOf(1)
    const dee = await newPerson(server)
    const added = await prepareAddingMembers(ana.deps, id, [dee.id])
    server.addWithEpoch(ana.id, id, [dee.id], added.body)
    const before = await messageFrom(ana, id, 'before')
    const opener = createMessageOpener(dee.deps)
    await opener.open(before)
    server.calls.length = 0

    await opener.open(before)
    expect(callsTo(server, 'getEpochs')).toBe(0)

    opener.forget(id)
    await opener.open(before)
    expect(callsTo(server, 'getEpochs')).toBe(1)
  })

  it('is reported as having no key, and is tried again next time, when the server cannot be reached', async () => {
    const { server, everyone: [ana], id } = await conversationOf(1)
    const outsider = await newPerson(server)
    const opener = createMessageOpener(outsider.deps)
    const message = await messageFrom(ana, id, 'not for you')

    expect(await opener.open(message)).toEqual({ readable: false, reason: 'noKey' })
    server.calls.length = 0
    expect(await opener.open(message)).toEqual({ readable: false, reason: 'noKey' })

    expect(callsTo(server, 'getEpochs')).toBe(1)
  })

  it('is reported as having no key, not as a bad message, when this browser cannot read its key store', async () => {
    const { everyone: [ana, ben], id } = await conversationOf(2)
    const message = await messageFrom(ana, id, 'x')
    const unreadableStore = {
      ...ben.deps,
      keys: {
        load: async (): Promise<never> => {
          throw new Error('the store could not be read')
        },
        save: ben.keys.save.bind(ben.keys),
        highest: ben.keys.highest.bind(ben.keys),
        removeAll: ben.keys.removeAll.bind(ben.keys),
      },
    }

    expect(await createMessageOpener(unreadableStore).open(message)).toEqual({ readable: false, reason: 'noKey' })
  })

  it('forgets what was missing for the conversation it is told about and for no other', async () => {
    const server = new FakeEpochServer()
    const ana = await newPerson(server)
    const dee = await newPerson(server)
    const conversations: string[] = []
    for (let i = 0; i < 2; i++) {
      const id = await start(server, ana, [])
      const added = await prepareAddingMembers(ana.deps, id, [dee.id])
      server.addWithEpoch(ana.id, id, [dee.id], added.body)
      conversations.push(id)
    }
    const opener = createMessageOpener(dee.deps)
    const [first, second] = [await messageFrom(ana, conversations[0], 'one'), await messageFrom(ana, conversations[1], 'two')]
    await opener.open(first)
    await opener.open(second)

    opener.forget(conversations[0])
    server.calls.length = 0
    await opener.open(second)
    expect(callsTo(server, 'getEpochs')).toBe(0)

    await opener.open(first)
    expect(callsTo(server, 'getEpochs')).toBe(1)
  })

  it('is invalid when the blob was changed on its way', async () => {
    const { everyone: [ana, ben], id } = await conversationOf(2)
    const message = await messageFrom(ana, id, 'untouched')
    const blob = fromBase64Url(message.ciphertext)

    for (const index of [0, 1, 6, 20, blob.length - 1]) {
      const changed = new Uint8Array(blob)
      changed[index] ^= 1
      expect(await createMessageOpener(ben.deps).open({ ...message, ciphertext: toBase64Url(changed) })).toEqual({ readable: false, reason: 'invalid' })
    }
  })

  it('is invalid when it is not a blob at all', async () => {
    const { everyone: [ana, ben], id } = await conversationOf(2)
    const message = await messageFrom(ana, id, 'x')
    const opener = createMessageOpener(ben.deps)

    for (const ciphertext of ['', 'not base64url!', 'AQE', toBase64Url(new Uint8Array(200))]) {
      expect(await opener.open({ ...message, ciphertext })).toEqual({ readable: false, reason: 'invalid' })
    }
  })

  it('is invalid when the epoch it is said to be in is not the one it was sealed in, without asking for any key', async () => {
    const { server, everyone: [ana, ben], id } = await conversationOf(2)
    const message = await messageFrom(ana, id, 'x')
    server.calls.length = 0

    expect(await createMessageOpener(ben.deps).open({ ...message, keyEpoch: 2 })).toEqual({ readable: false, reason: 'invalid' })
    expect(server.calls).toEqual([])
  })

  it('is invalid when it is moved to another conversation, another sender or an older edit', async () => {
    const { everyone: [ana, ben], id } = await conversationOf(2)
    const message = await messageFrom(ana, id, 'as written', { editCount: 1 })
    const opener = createMessageOpener(ben.deps)

    expect(await opener.open({ ...message, conversationId: crypto.randomUUID() })).toMatchObject({ readable: false })
    expect(await opener.open({ ...message, senderId: ben.id })).toEqual({ readable: false, reason: 'invalid' })
    expect(await opener.open({ ...message, editCount: 0 })).toEqual({ readable: false, reason: 'invalid' })
    expect(await opener.open({ ...message, clientMessageId: crypto.randomUUID() })).toEqual({ readable: false, reason: 'invalid' })
    expect(await opener.open(message)).toEqual({ readable: true, text: 'as written' })
  })

  it('is invalid when the key held for its epoch is not the one it was sealed under', async () => {
    const { everyone: [ana, ben], id } = await conversationOf(2)
    const message = await messageFrom(ana, id, 'secret')
    await ben.keys.save(ben.id, id, 1, crypto.getRandomValues(new Uint8Array(32)))

    expect(await createMessageOpener(ben.deps).open(message)).toEqual({ readable: false, reason: 'invalid' })
  })

  it('is invalid when someone who holds the key signs as another member', async () => {
    const { everyone: [ana, ben, cy], id } = await conversationOf(3)
    const forged = await sealText(cy.deps, { conversationId: id, clientMessageId: crypto.randomUUID(), editCount: 0, epoch: 1, text: 'from Ana?' })

    const claimed: SealedMessage = {
      conversationId: id,
      senderId: ana.id,
      clientMessageId: 'x',
      editCount: 0,
      keyEpoch: 1,
      ciphertext: forged.ciphertext,
    }

    expect(await createMessageOpener(ben.deps).open(claimed)).toEqual({ readable: false, reason: 'invalid' })
  })

  it('has an unknown sender when the sender has no keys on the server', async () => {
    const { everyone: [ana, ben], id } = await conversationOf(2)
    const message = await messageFrom(ana, id, 'x')

    expect(await createMessageOpener(ben.deps).open({ ...message, senderId: crypto.randomUUID() })).toEqual({
      readable: false,
      reason: 'senderUnknown',
    })
  })

  it('looks the sender up again when the server could not be reached the first time', async () => {
    const { server, everyone: [, ben, cy], id } = await conversationOf(3)
    const message = await messageFrom(cy, id, 'from cy')
    let reachable = true
    const flaky = {
      ...ben.deps,
      api: {
        ...ben.deps.api,
        lookupKeys: async (userIds: string[]) => {
          if (!reachable) throw new Error('network down')
          return ben.deps.api.lookupKeys(userIds)
        },
      },
    }
    const opener = createMessageOpener(flaky)
    await loadEpochKeys(flaky, id)
    reachable = false

    expect(await opener.open(message)).toEqual({ readable: false, reason: 'senderUnknown' })
    reachable = true
    expect(await opener.open(message)).toEqual({ readable: true, text: 'from cy' })
    expect(server.calls.length).toBeGreaterThan(0)
  })

  it('checks a message of its own against its own key, without asking the server who it is', async () => {
    const { server, everyone: [ana], id } = await conversationOf(2)
    const message = await messageFrom(ana, id, 'mine')
    server.calls.length = 0

    expect(await createMessageOpener(ana.deps).open(message)).toEqual({ readable: true, text: 'mine' })
    expect(callsTo(server, 'lookupKeys')).toBe(0)
  })

  it('has a changed sender when their identity key is not the one first seen', async () => {
    const { everyone: [ana, ben], id } = await conversationOf(2)
    const message = await messageFrom(ana, id, 'x')
    await loadEpochKeys(ben.deps, id)
    const other = await newEd25519()
    await ben.pins.save({ ownerId: ben.id, contactId: ana.id, identityPublic: other.publicKey, firstSeenAt: new Date().toISOString() })

    expect(await createMessageOpener(ben.deps).open(message)).toEqual({ readable: false, reason: 'senderChanged' })
  })
})

describe('sealing a text', () => {
  it('seals under the key of the epoch it is given and keeps the epoch in the blob', async () => {
    const { everyone: [ana], id } = await conversationOf(1)

    const sealed = await sealText(ana.deps, { conversationId: id, clientMessageId: crypto.randomUUID(), editCount: 0, epoch: 1, text: 'x' })

    expect(sealed.keyEpoch).toBe(1)
    expect(new DataView(fromBase64Url(sealed.ciphertext).buffer).getUint32(2)).toBe(1)
  })

  it('makes a different blob each time, even for the same text', async () => {
    const { everyone: [ana], id } = await conversationOf(1)
    const input = { conversationId: id, clientMessageId: crypto.randomUUID(), editCount: 0, epoch: 1, text: 'same' }

    expect((await sealText(ana.deps, input)).ciphertext).not.toBe((await sealText(ana.deps, input)).ciphertext)
  })

  it('opens the key from the server when this browser does not hold it yet', async () => {
    const { everyone: [, ben], id } = await conversationOf(2)
    expect(await ben.keys.load(ben.id, id, 1)).toBeNull()

    const sealed = await sealText(ben.deps, { conversationId: id, clientMessageId: crypto.randomUUID(), editCount: 0, epoch: 1, text: 'x' })

    expect(sealed.keyEpoch).toBe(1)
    expect(await ben.keys.load(ben.id, id, 1)).not.toBeNull()
  })

  it('refuses when there is no key for the epoch, here or on the server', async () => {
    const { everyone: [ana], id } = await conversationOf(1)

    await expect(sealText(ana.deps, { conversationId: id, clientMessageId: crypto.randomUUID(), editCount: 0, epoch: 3, text: 'x' }))
      .rejects.toBeInstanceOf(EpochKeyMissingError)
  })
})

describe('sending under the conversation\'s current epoch', () => {
  const work = (server: FakeEpochServer, id: string) => async (epoch: number) => {
    const conversation = server.conversations.get(id)!
    if (epoch !== conversation.keyEpoch) throw new EpochRefused('epoch-stale')
    if (conversation.keyRotationRequired) throw new EpochRefused('epoch-due')
    return epoch
  }

  it('asks the server nothing when the epoch is known and its key is held', async () => {
    const { server, everyone: [ana], id } = await conversationOf(1)
    server.calls.length = 0

    expect(await underCurrentEpoch(ana.deps, id, { keyEpoch: 1, keyRotationRequired: false }, work(server, id))).toBe(1)
    expect(server.calls).toEqual([])
  })

  it('opens the key first when this browser does not hold it', async () => {
    const { server, everyone: [, ben], id } = await conversationOf(2)

    expect(await underCurrentEpoch(ben.deps, id, { keyEpoch: 1, keyRotationRequired: false }, work(server, id))).toBe(1)
    expect(await ben.keys.load(ben.id, id, 1)).not.toBeNull()
  })

  it('makes the first epoch of a conversation that has none', async () => {
    const server = new FakeEpochServer()
    const [ana, ben] = [await newPerson(server), await newPerson(server)]
    server.createConversation(LEGACY, [ana.id, ben.id])

    expect(await underCurrentEpoch(ana.deps, LEGACY, { keyEpoch: 0, keyRotationRequired: false }, work(server, LEGACY))).toBe(1)
  })

  it('replaces an epoch that a removal marked as due before sealing under it', async () => {
    const { server, everyone: [ana], id } = await conversationOf(2)
    server.requireRotation(id)

    expect(await underCurrentEpoch(ana.deps, id, { keyEpoch: 1, keyRotationRequired: true }, work(server, id))).toBe(2)
  })

  it('does not even try an epoch it knows is due, so the one made first is the one used', async () => {
    const { server, everyone: [ana], id } = await conversationOf(2)
    server.requireRotation(id)
    const tried: number[] = []

    await underCurrentEpoch(ana.deps, id, { keyEpoch: 1, keyRotationRequired: true }, async (epoch) => {
      tried.push(epoch)
      return work(server, id)(epoch)
    })

    expect(tried).toEqual([2])
  })

  it('looks again and adopts the epoch another member stored, when the one it went by was out of date', async () => {
    const { server, everyone: [ana, ben], id } = await conversationOf(2)
    server.requireRotation(id)
    await ensureEpoch(ben.deps, id)
    let attempts = 0

    const epoch = await underCurrentEpoch(ana.deps, id, { keyEpoch: 1, keyRotationRequired: false }, async (e) => {
      attempts++
      return work(server, id)(e)
    })

    expect(epoch).toBe(2)
    expect(attempts).toBe(2)
  })

  it('looks again when the epoch was marked as due after the person last looked', async () => {
    const { server, everyone: [ana], id } = await conversationOf(2)
    server.requireRotation(id)
    let attempts = 0

    const epoch = await underCurrentEpoch(ana.deps, id, { keyEpoch: 1, keyRotationRequired: false }, async (e) => {
      attempts++
      return work(server, id)(e)
    })

    expect(epoch).toBe(2)
    expect(attempts).toBe(2)
  })

  it('gives up after the allowed number of attempts and says so', async () => {
    const { everyone: [ana], id } = await conversationOf(1)
    let attempts = 0

    await expect(
      underCurrentEpoch(ana.deps, id, { keyEpoch: 1, keyRotationRequired: false }, async () => {
        attempts++
        throw new EpochRefused('epoch-stale')
      }),
    ).rejects.toBeInstanceOf(EpochContestedError)

    expect(attempts).toBe(MAX_ATTEMPTS)
  })

  it('does not try again after a refusal that trying again cannot fix', async () => {
    const { everyone: [ana], id } = await conversationOf(1)
    let attempts = 0

    const failure = await underCurrentEpoch(ana.deps, id, { keyEpoch: 1, keyRotationRequired: false }, async () => {
      attempts++
      throw new EpochRefused('member-has-no-keys')
    }).catch((error: unknown) => error)

    expect((failure as EpochRefused).code).toBe('member-has-no-keys')
    expect(attempts).toBe(1)
  })
})
