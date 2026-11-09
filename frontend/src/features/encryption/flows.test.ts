import { describe, expect, it } from 'vitest'
import {
  CryptoError,
  createAccountSecrets,
  currentAccountKey,
  encodeSecrets,
  fromBase64Url,
  newX25519,
  rotateAccountKey,
  sealLinkSecrets,
  signBundle,
  toBase64Url,
  toHex,
} from '../../lib/crypto'
import { MemoryEpochKeyStore, MemoryPinStore } from './fakeEpochServer'
import { FakeKeysServer, MemoryDeviceStore, ServerRefused } from './fakeKeysServer'
import { forgetKept } from './release'
import {
  type FlowDeps,
  InvalidLinkCodeError,
  KeysMismatchError,
  LINK_POLL_MS,
  LinkEndedError,
  LinkKeyMismatchError,
  SetUpIncompleteError,
  approveLinkRequest,
  changePassphrase,
  findLinkRequest,
  isWrongPassphrase,
  loadState,
  setUp,
  startLink,
  unlock,
} from './flows'

const USER = '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f'
const PASSPHRASE = 'correct horse battery staple'

// A browser: its own store, and the same server as every other browser of the test.
// With a gate, a poll waits until the test opens it, so a test can act between two polls of the new device without racing it.
function browser(server: FakeKeysServer, name = 'Chrome on Windows', gate?: Promise<void>) {
  const store = new MemoryDeviceStore()
  const epochKeys = new MemoryEpochKeyStore()
  const pins = new MemoryPinStore()
  const waits: number[] = []
  const deps: FlowDeps = {
    api: server,
    store,
    device: { kind: 'Web', name },
    // Yields so that another browser of the test can act between two polls, and records how long a real one would have waited.
    wait: async (ms, signal) => {
      waits.push(ms)
      signal?.throwIfAborted()
      await (gate ?? new Promise((resolve) => setTimeout(resolve, 0)))
    },
    forgetConversationKeys: (userId) => forgetKept({ epochKeys, pins }, userId),
  }
  return { deps, store, epochKeys, pins, waits }
}

async function rejection(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => {
      throw new Error('Expected the promise to reject.')
    },
    (failure: unknown) => failure,
  )
}

describe('setting up', () => {
  it('publishes a signed bundle and a backup, registers this browser and keeps the keys here', async () => {
    const server = new FakeKeysServer()
    const a = browser(server, 'Firefox on Linux')

    const ready = await setUp(a.deps, USER, PASSPHRASE)

    expect(server.bundles).toHaveLength(1)
    expect(server.bundles[0].generation).toBe(1)
    expect(server.backup?.version).toBe(1)
    expect(server.devices).toEqual([expect.objectContaining({ id: ready.deviceId, kind: 'Web', name: 'Firefox on Linux' })])
    const stored = await a.store.load(USER)
    expect(stored?.deviceId).toBe(ready.deviceId)
    expect(toHex(stored!.secrets)).toBe(toHex(encodeSecrets(ready.secrets)))
  })

  it('makes a backup that the passphrase opens on another browser, and a bundle that agrees with it', async () => {
    const server = new FakeKeysServer()
    const first = await setUp(browser(server).deps, USER, PASSPHRASE)
    const other = browser(server, 'Safari on macOS')

    const second = await unlock(other.deps, USER, PASSPHRASE)

    expect(toHex(encodeSecrets(second.secrets))).toBe(toHex(encodeSecrets(first.secrets)))
    expect(second.deviceId).not.toBe(first.deviceId)
    expect(server.devices).toHaveLength(2)
    expect((await other.store.load(USER))?.deviceId).toBe(second.deviceId)
  })

  it('does not set up twice', async () => {
    const server = new FakeKeysServer()
    await setUp(browser(server).deps, USER, PASSPHRASE)
    const again = browser(server)

    const failure = await rejection(setUp(again.deps, USER, PASSPHRASE))

    expect(failure).toBeInstanceOf(ServerRefused)
    expect(await again.store.load(USER)).toBeNull()
    expect(server.devices).toHaveLength(1)
  })

  it('refuses a passphrase the backup would refuse, before anything reaches the server', async () => {
    const server = new FakeKeysServer()

    const failure = await rejection(setUp(browser(server).deps, USER, 'too short'))

    expect(failure).toBeInstanceOf(CryptoError)
    expect(server.calls).toEqual([])
  })

  it('says so when the keys were created but this browser could not be added as a device', async () => {
    const server = new FakeKeysServer()
    server.failNext.set('registerDevice', new Error('network'))
    const a = browser(server)

    const failure = await rejection(setUp(a.deps, USER, PASSPHRASE))

    expect(failure).toBeInstanceOf(SetUpIncompleteError)
    expect((failure as SetUpIncompleteError).cause).toEqual(new Error('network'))
    expect(server.bundles).toHaveLength(1)
    expect(await a.store.load(USER)).toBeNull()
    // And the person finishes with the passphrase they just chose.
    expect((await loadState(a.deps, USER)).state.phase).toBe('locked')
    await unlock(a.deps, USER, PASSPHRASE)
    expect((await loadState(a.deps, USER)).state.phase).toBe('ready')
  })

  it('takes the device off the server again when the keys cannot be kept here', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    a.store.failNextSave = new Error('quota')

    const failure = await rejection(setUp(a.deps, USER, PASSPHRASE))

    expect(failure).toBeInstanceOf(SetUpIncompleteError)
    expect(((failure as SetUpIncompleteError).cause as Error).message).toBe('quota')
    expect(server.devices).toEqual([])
  })
})

describe('what this browser has', () => {
  it('is not set up when the server has no keys', async () => {
    const loaded = await loadState(browser(new FakeKeysServer()).deps, USER)

    expect(loaded).toEqual({ state: { phase: 'notSetUp' }, removedElsewhere: false })
  })

  it('is locked when the server has keys and this browser does not', async () => {
    const server = new FakeKeysServer()
    await setUp(browser(server).deps, USER, PASSPHRASE)

    const loaded = await loadState(browser(server).deps, USER)

    expect(loaded).toEqual({ state: { phase: 'locked' }, removedElsewhere: false })
  })

  it('is ready when this browser has keys and is still one of the devices', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    const ready = await setUp(a.deps, USER, PASSPHRASE)

    const loaded = await loadState(a.deps, USER)

    expect(loaded.state.phase).toBe('ready')
    if (loaded.state.phase === 'ready') {
      expect(loaded.state.deviceId).toBe(ready.deviceId)
      expect(toHex(encodeSecrets(loaded.state.secrets))).toBe(toHex(encodeSecrets(ready.secrets)))
    }
    expect(loaded.removedElsewhere).toBe(false)
  })

  it('forgets the keys when the person removed this browser from their devices elsewhere, and says so', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    const ready = await setUp(a.deps, USER, PASSPHRASE)
    server.devices = server.devices.filter((device) => device.id !== ready.deviceId)

    const loaded = await loadState(a.deps, USER)

    expect(loaded).toEqual({ state: { phase: 'locked' }, removedElsewhere: true })
    expect(await a.store.load(USER)).toBeNull()
  })

  it('forgets the keys of the conversations and who was seen too, for that person only', async () => {
    const OTHER = '9d8c7b6a-5e4f-4a3b-8c2d-1e0f9a8b7c6d'
    const server = new FakeKeysServer()
    const a = browser(server)
    const ready = await setUp(a.deps, USER, PASSPHRASE)
    await a.epochKeys.save(USER, 'room', 1, Uint8Array.of(1))
    await a.epochKeys.save(OTHER, 'room', 1, Uint8Array.of(2))
    await a.pins.save({ ownerId: USER, contactId: OTHER, identityPublic: Uint8Array.of(3), firstSeenAt: 'then' })
    await a.pins.save({ ownerId: OTHER, contactId: USER, identityPublic: Uint8Array.of(4), firstSeenAt: 'then' })
    server.devices = server.devices.filter((device) => device.id !== ready.deviceId)

    await loadState(a.deps, USER)

    expect(await a.epochKeys.load(USER, 'room', 1)).toBeNull()
    expect(await a.pins.load(USER, OTHER)).toBeNull()
    expect(await a.epochKeys.load(OTHER, 'room', 1)).not.toBeNull()
    expect(await a.pins.load(OTHER, USER)).not.toBeNull()
  })

  it('keeps the keys of the conversations while the device is still listed', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    await setUp(a.deps, USER, PASSPHRASE)
    await a.epochKeys.save(USER, 'room', 1, Uint8Array.of(1))
    await a.pins.save({ ownerId: USER, contactId: 'cy', identityPublic: Uint8Array.of(3), firstSeenAt: 'then' })

    await loadState(a.deps, USER)

    expect(await a.epochKeys.load(USER, 'room', 1)).not.toBeNull()
    expect(await a.pins.load(USER, 'cy')).not.toBeNull()
  })

  // Not being able to ask is not an answer: the keys are not deleted on a hunch.
  it('keeps the keys when the server cannot be asked', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    await setUp(a.deps, USER, PASSPHRASE)
    await a.epochKeys.save(USER, 'room', 1, Uint8Array.of(1))
    server.failNext.set('listDevices', new Error('offline'))

    const failure = await rejection(loadState(a.deps, USER))

    expect((failure as Error).message).toBe('offline')
    expect(await a.store.load(USER)).not.toBeNull()
    expect(await a.epochKeys.load(USER, 'room', 1)).not.toBeNull()
  })

  it('keeps one person from seeing the keys of another on the same browser', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    await setUp(a.deps, USER, PASSPHRASE)

    expect(await a.store.load('someone-else')).toBeNull()
  })
})

describe('opening the backup', () => {
  it('names a wrong passphrase, and saves nothing', async () => {
    const server = new FakeKeysServer()
    await setUp(browser(server).deps, USER, PASSPHRASE)
    const other = browser(server)

    const failure = await rejection(unlock(other.deps, USER, 'not the passphrase'))

    expect(isWrongPassphrase(failure)).toBe(true)
    expect(await other.store.load(USER)).toBeNull()
    expect(server.devices).toHaveLength(1)
  })

  it('refuses a backup that is older than the keys on the server', async () => {
    const server = new FakeKeysServer()
    const first = await setUp(browser(server).deps, USER, PASSPHRASE)
    // The server now has a second generation of the account key, and still serves the backup from before it.
    const rotated = await rotateAccountKey(first.secrets)
    server.bundles.push({ generation: 2, bundle: toBase64Url(await signBundle(USER, rotated.identity, currentAccountKey(rotated))) })
    const other = browser(server)

    const failure = await rejection(unlock(other.deps, USER, PASSPHRASE))

    expect(failure).toBeInstanceOf(KeysMismatchError)
    expect(await other.store.load(USER)).toBeNull()
    expect(server.devices).toHaveLength(1)
  })

  it('refuses keys whose public half the server does not hold', async () => {
    const server = new FakeKeysServer()
    await setUp(browser(server).deps, USER, PASSPHRASE)
    const stranger = await createAccountSecrets()
    server.bundles = [{ generation: 1, bundle: toBase64Url(await signBundle(USER, stranger.identity, currentAccountKey(stranger))) }]
    const other = browser(server)

    const failure = await rejection(unlock(other.deps, USER, PASSPHRASE))

    expect(failure).toBeInstanceOf(KeysMismatchError)
    expect(await other.store.load(USER)).toBeNull()
  })

  // Each of these has one thing wrong and the rest right, so each check is tested on its own.
  it('refuses keys when the server lists no bundle at all, instead of accepting them unchecked', async () => {
    const server = new FakeKeysServer()
    await setUp(browser(server).deps, USER, PASSPHRASE)
    server.bundles = []
    const other = browser(server)

    const failure = await rejection(unlock(other.deps, USER, PASSPHRASE))

    expect(failure).toBeInstanceOf(KeysMismatchError)
    expect(await other.store.load(USER)).toBeNull()
    expect(server.devices).toHaveLength(1)
  })

  it('refuses a bundle that claims another generation than the server lists it under', async () => {
    const server = new FakeKeysServer()
    const first = await setUp(browser(server).deps, USER, PASSPHRASE)
    const account = currentAccountKey(first.secrets)
    server.bundles = [{ generation: 1, bundle: toBase64Url(await signBundle(USER, first.secrets.identity, { ...account, generation: 2 })) }]

    const failure = await rejection(unlock(browser(server).deps, USER, PASSPHRASE))

    expect(failure).toBeInstanceOf(KeysMismatchError)
  })

  it('refuses keys whose account key differs from the one the server holds, though the identity agrees', async () => {
    const server = new FakeKeysServer()
    const first = await setUp(browser(server).deps, USER, PASSPHRASE)
    const other = await createAccountSecrets()
    server.bundles = [{ generation: 1, bundle: toBase64Url(await signBundle(USER, first.secrets.identity, currentAccountKey(other))) }]

    const failure = await rejection(unlock(browser(server).deps, USER, PASSPHRASE))

    expect(failure).toBeInstanceOf(KeysMismatchError)
  })

  it('refuses keys whose identity key differs from the one the server holds, though the account key agrees', async () => {
    const server = new FakeKeysServer()
    const first = await setUp(browser(server).deps, USER, PASSPHRASE)
    const other = await createAccountSecrets()
    server.bundles = [{ generation: 1, bundle: toBase64Url(await signBundle(USER, other.identity, currentAccountKey(first.secrets))) }]

    const failure = await rejection(unlock(browser(server).deps, USER, PASSPHRASE))

    expect(failure).toBeInstanceOf(KeysMismatchError)
  })

  it('refuses a bundle whose signature does not verify', async () => {
    const server = new FakeKeysServer()
    await setUp(browser(server).deps, USER, PASSPHRASE)
    const bundle = fromBase64Url(server.bundles[0].bundle).slice()
    bundle[100] ^= 1
    server.bundles[0].bundle = toBase64Url(bundle)

    const failure = await rejection(unlock(browser(server).deps, USER, PASSPHRASE))

    expect(failure).toBeInstanceOf(CryptoError)
    expect((failure as CryptoError).code).toBe('authentication')
  })

  it('says there is no backup when the server has none', async () => {
    const failure = await rejection(unlock(browser(new FakeKeysServer()).deps, USER, PASSPHRASE))

    expect(failure).toBeInstanceOf(ServerRefused)
  })
})

describe('changing the passphrase', () => {
  it('makes the new passphrase open the backup and the old one not', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    const ready = await setUp(a.deps, USER, PASSPHRASE)

    await changePassphrase(a.deps, USER, ready.secrets, 'a brand new passphrase')

    expect(server.backup?.version).toBe(2)
    expect(isWrongPassphrase(await rejection(unlock(browser(server).deps, USER, PASSPHRASE)))).toBe(true)
    const other = await unlock(browser(server).deps, USER, 'a brand new passphrase')
    expect(toHex(encodeSecrets(other.secrets))).toBe(toHex(encodeSecrets(ready.secrets)))
  })

  // The version it is made from is the one the server holds now, not always the first.
  it('can be done again, from the version the first change made', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    const ready = await setUp(a.deps, USER, PASSPHRASE)

    await changePassphrase(a.deps, USER, ready.secrets, 'a brand new passphrase')
    await changePassphrase(a.deps, USER, ready.secrets, 'and another new passphrase')

    expect(server.backup?.version).toBe(3)
    await unlock(browser(server).deps, USER, 'and another new passphrase')
  })

  it('is a conflict when another device changed the passphrase first, and the backup is left as that device made it', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    const ready = await setUp(a.deps, USER, PASSPHRASE)
    server.failNext.set('replaceBackup', new ServerRefused(409))
    const before = server.backup

    const failure = await rejection(changePassphrase(a.deps, USER, ready.secrets, 'a brand new passphrase'))

    expect((failure as ServerRefused).status).toBe(409)
    expect(server.backup).toEqual(before)
  })

  it('refuses a new passphrase the backup would refuse', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    const ready = await setUp(a.deps, USER, PASSPHRASE)

    const failure = await rejection(changePassphrase(a.deps, USER, ready.secrets, 'short'))

    expect(failure).toBeInstanceOf(CryptoError)
    expect(server.backup?.version).toBe(1)
  })
})

describe('linking a new device', () => {
  async function arrange() {
    const server = new FakeKeysServer()
    const a = browser(server, 'Chrome on Windows')
    const ready = await setUp(a.deps, USER, PASSPHRASE)
    const b = browser(server, 'Safari on iOS')
    return { server, a, b, ready }
  }

  it('gives the keys of the first device to the second, which then holds them and is a device', async () => {
    const { server, a, b, ready } = await arrange()

    const attempt = await startLink(b.deps, USER)
    // The code is read off the screen of the new device and typed on the first, in any case and with the hyphen.
    const typed = `${attempt.code.slice(0, 5)}-${attempt.code.slice(5)}`.toLowerCase()
    const request = await findLinkRequest(a.deps, typed)
    expect(request).toEqual(expect.objectContaining({ id: attempt.requestId, name: 'Safari on iOS', kind: 'Web' }))
    await approveLinkRequest(a.deps, USER, ready.secrets, request)
    const linked = await attempt.result

    expect(toHex(encodeSecrets(linked.secrets))).toBe(toHex(encodeSecrets(ready.secrets)))
    expect(server.devices.map((device) => device.id)).toContain(linked.deviceId)
    expect((await b.store.load(USER))?.deviceId).toBe(linked.deviceId)
    expect(attempt.uri).toBe(`chatnet-link:1:${attempt.requestId}:${server.links.get(attempt.requestId)!.info.temporaryPublicKey}`)
    expect(attempt.code).toMatch(/^[0-9A-HJKMNP-TV-Z]{10}$/)
  })

  it('waits between polls while nothing is approved', async () => {
    const { a, b, ready } = await arrange()
    const attempt = await startLink(b.deps, USER)

    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(b.waits.length).toBeGreaterThan(0)
    expect(new Set(b.waits)).toEqual(new Set([LINK_POLL_MS]))

    await approveLinkRequest(a.deps, USER, ready.secrets, await findLinkRequest(a.deps, attempt.code))
    await attempt.result
  })

  it('refuses a code that is not a code, without asking the server', async () => {
    const { server, a } = await arrange()
    const before = server.calls.length

    expect(await rejection(findLinkRequest(a.deps, 'nope'))).toBeInstanceOf(InvalidLinkCodeError)
    expect(server.calls.length).toBe(before)
  })

  // The server answers the search for a code with a key. If it is not the key the code was made from, someone is between the two.
  it('approves nothing when the key the server returns is not the one the code was made from', async () => {
    const { server, a, b } = await arrange()
    const controller = new AbortController()
    const attempt = await startLink(b.deps, USER, controller.signal)
    server.links.get(attempt.requestId)!.info.temporaryPublicKey = toBase64Url((await newX25519()).publicKey)

    const failure = await rejection(findLinkRequest(a.deps, attempt.code))

    expect(failure).toBeInstanceOf(LinkKeyMismatchError)
    expect(server.calls).not.toContain('approveLink')
    // The new device is still waiting; the person gives up, which ends it.
    controller.abort()
    expect(((await rejection(attempt.result)) as Error).name).toBe('AbortError')
  })

  it('does not take keys that were sealed to somebody else', async () => {
    const { server, b, ready } = await arrange()
    const attempt = await startLink(b.deps, USER)
    const attacker = await newX25519()
    // Sealed first and set in one step, so the new device never sees an approval without its payload.
    const sealed = toBase64Url(await sealLinkSecrets(attempt.requestId, USER, attacker.publicKey, ready.secrets))
    const record = server.links.get(attempt.requestId)!
    record.state = 'Approved'
    record.payload = sealed

    const failure = await rejection(attempt.result)

    expect(failure).toBeInstanceOf(CryptoError)
    expect((failure as CryptoError).code).toBe('authentication')
    expect(await b.store.load(USER)).toBeNull()
    expect(server.devices).toHaveLength(1)
  })

  it('does not take keys that the server does not hold the public half of', async () => {
    const { server, a, b } = await arrange()
    const attempt = await startLink(b.deps, USER)
    const stranger = await createAccountSecrets()
    const request = await findLinkRequest(a.deps, attempt.code)
    await approveLinkRequest(a.deps, USER, stranger, request)

    const failure = await rejection(attempt.result)

    expect(failure).toBeInstanceOf(KeysMismatchError)
    expect(await b.store.load(USER)).toBeNull()
    expect(server.devices).toHaveLength(1)
  })

  it('ends as handed over when an approval comes with no secrets', async () => {
    const { server, b } = await arrange()
    const attempt = await startLink(b.deps, USER)
    const record = server.links.get(attempt.requestId)!
    record.state = 'Approved'
    record.payload = null

    const failure = await rejection(attempt.result)

    expect(failure).toBeInstanceOf(LinkEndedError)
    expect((failure as LinkEndedError).reason).toBe('collected')
  })

  it('ends with the reason when the request expired', async () => {
    const { server, b } = await arrange()
    const attempt = await startLink(b.deps, USER)
    server.expire(attempt.requestId)

    const failure = await rejection(attempt.result)

    expect(failure).toBeInstanceOf(LinkEndedError)
    expect((failure as LinkEndedError).reason).toBe('expired')
  })

  it('ends with the reason when the keys were already handed over', async () => {
    const { server, a, ready } = await arrange()
    let open!: () => void
    const gate = new Promise<void>((resolve) => {
      open = resolve
    })
    const b = browser(server, 'Safari on iOS', gate)
    const attempt = await startLink(b.deps, USER)
    await approveLinkRequest(a.deps, USER, ready.secrets, await findLinkRequest(a.deps, attempt.code))
    // Something else collected the answer before the new device asked again.
    await server.pollLink(attempt.requestId)
    open()

    const failure = await rejection(attempt.result)

    expect(failure).toBeInstanceOf(LinkEndedError)
    expect((failure as LinkEndedError).reason).toBe('collected')
  })

  it('stops waiting when the person gives up', async () => {
    const { b } = await arrange()
    const controller = new AbortController()
    const attempt = await startLink(b.deps, USER, controller.signal)

    controller.abort()

    const failure = await rejection(attempt.result)
    expect((failure as Error).name).toBe('AbortError')
    expect(await b.store.load(USER)).toBeNull()
  })

  it('cannot be approved twice, and the second approval changes nothing', async () => {
    const { server, a, ready } = await arrange()
    let open!: () => void
    const gate = new Promise<void>((resolve) => {
      open = resolve
    })
    const b = browser(server, 'Safari on iOS', gate)
    const attempt = await startLink(b.deps, USER)
    const request = await findLinkRequest(a.deps, attempt.code)
    await approveLinkRequest(a.deps, USER, ready.secrets, request)
    const payload = server.links.get(attempt.requestId)!.payload

    // A second approval, with other keys, while the new device has not asked yet.
    const failure = await rejection(approveLinkRequest(a.deps, USER, await createAccountSecrets(), request))
    open()

    expect((failure as ServerRefused).status).toBe(409)
    expect(server.links.get(attempt.requestId)!.payload).toBe(payload)
    const linked = await attempt.result
    expect(toHex(encodeSecrets(linked.secrets))).toBe(toHex(encodeSecrets(ready.secrets)))
  })
})
