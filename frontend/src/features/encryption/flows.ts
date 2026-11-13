import {
  type AccountSecrets,
  CryptoError,
  createAccountSecrets,
  currentAccountKey,
  decodeSecrets,
  encodeSecrets,
  equal,
  findAccountKey,
  fromBase64Url,
  linkCode,
  linkUri,
  matchesLinkCode,
  newX25519,
  normalizeLinkCode,
  openBackup,
  openBundle,
  openLinkSecrets,
  rotateAccountKey,
  sealBackup,
  sealLinkSecrets,
  signBundle,
  startOverAccountSecrets,
  toBase64Url,
} from '../../lib/crypto'
import type { BundleEntry, DeviceKind, DeviceStore, KeysApi, LinkRequestInfo } from './types'

// What a person does with their keys, as functions of the server and of the store in this browser. Nothing here touches the
// network, IndexedDB or the page directly, so a test drives the real cryptography against a fake server.

export interface FlowDeps {
  api: KeysApi
  store: DeviceStore
  // What this device is called in the list of devices.
  device: { kind: DeviceKind; name: string }
  // Waits, or rejects when the signal is aborted.
  wait: (ms: number, signal?: AbortSignal) => Promise<void>
  // Forgets the rest of what this browser keeps for a person (the keys of their conversations and who they have seen), for a browser
  // that finds its device was removed: the keys of the device go and so must everything that is opened with them.
  forgetConversationKeys: (userId: string) => Promise<void>
}

// This browser has keys and is a device of the person.
export interface Ready {
  deviceId: string
  secrets: AccountSecrets
}

// 'outdated': this browser is a device of the person and holds keys, but the account key has a newer generation (a device was removed as
// lost from another device, and the newer private half is only in the backup). The keys here are not used until the passphrase brings
// the newer ones, so nothing is sealed to or opened with a generation that is no longer the current one.
export type EncryptionState =
  | { phase: 'notSetUp' }
  | { phase: 'locked' }
  | { phase: 'outdated'; deviceId: string }
  | ({ phase: 'ready' } & Ready)

export interface LoadedState {
  state: EncryptionState
  // This browser had keys, and the person removed it from their devices elsewhere: the keys were deleted here.
  removedElsewhere: boolean
}

// How often a new device asks whether it was approved.
export const LINK_POLL_MS = 3000

// ##### Errors the screen explains #####

// The backup or the keys from another device do not agree with the public keys on the server. Either the backup is older than the
// latest keys, or something is wrong with what was served; nothing is saved.
export class KeysMismatchError extends Error {
  constructor() {
    super('The keys do not match the ones on the server.')
    this.name = 'KeysMismatchError'
  }
}

// The keys were created but this browser could not be registered as a device; the person unlocks with the passphrase to finish.
export class SetUpIncompleteError extends Error {
  constructor(options: { cause: unknown }) {
    super('The keys were created, but this browser could not be added as a device.', options)
    this.name = 'SetUpIncompleteError'
  }
}

export class InvalidLinkCodeError extends Error {
  constructor() {
    super('That is not a link code.')
    this.name = 'InvalidLinkCodeError'
  }
}

// The server handed over a key that does not match the code the person typed. Nothing was approved.
export class LinkKeyMismatchError extends Error {
  constructor() {
    super('The request does not match the code.')
    this.name = 'LinkKeyMismatchError'
  }
}

// The request ended without this device getting the keys.
export class LinkEndedError extends Error {
  readonly reason: 'expired' | 'collected'

  constructor(reason: 'expired' | 'collected') {
    super(reason === 'expired' ? 'The request expired.' : 'The keys were already handed over.')
    this.name = 'LinkEndedError'
    this.reason = reason
  }
}

// ##### State #####

// What this browser has, and what the server says. A device that the person removed elsewhere finds out here: its keys are deleted.
// A failure to ask the server is not an answer, so nothing is deleted on one.
export async function loadState(deps: FlowDeps, userId: string): Promise<LoadedState> {
  let removedElsewhere = false

  const stored = await deps.store.load(userId)
  if (stored) {
    const devices = await deps.api.listDevices()
    if (devices.some((device) => device.id === stored.deviceId)) {
      const secrets = decodeSecrets(stored.secrets)
      const newest = Math.max(0, ...(await deps.api.getStatus()).bundles.map((entry) => entry.generation))

      if (newest > currentAccountKey(secrets).generation) return { state: { phase: 'outdated', deviceId: stored.deviceId }, removedElsewhere }
      return { state: { phase: 'ready', deviceId: stored.deviceId, secrets }, removedElsewhere }
    }

    await deps.store.remove(userId)
    await deps.forgetConversationKeys(userId)
    removedElsewhere = true
  }

  const status = await deps.api.getStatus()
  return { state: status.bundles.length === 0 ? { phase: 'notSetUp' } : { phase: 'locked' }, removedElsewhere }
}

// Registers this browser as a device and keeps the keys here. If the keys cannot be kept the device is taken off the server again,
// so the list of devices never names a browser that has nothing.
async function becomeDevice(deps: FlowDeps, userId: string, secrets: AccountSecrets): Promise<Ready> {
  const device = await deps.api.registerDevice(deps.device)

  try {
    await deps.store.save({ userId, deviceId: device.id, secrets: encodeSecrets(secrets) })
  } catch (failure) {
    await deps.api.removeDevice(device.id).catch(() => undefined)
    throw failure
  }

  return { deviceId: device.id, secrets }
}

// What was made or opened must be what the server holds the public half of, for every generation these keys hold, signed by the identity
// key. Generations below the lowest these keys hold are not checked: after a reset the keys start at a later generation, and the
// bundles of the generations before it belong to keys that were thrown away.
async function checkAgainstServer(userId: string, secrets: AccountSecrets, bundles: BundleEntry[]): Promise<void> {
  // Nothing to check against is not a pass: a server that listed no bundle must not get keys accepted unchecked. A bundle for a
  // generation these keys do not have and are not below them (the backup is older than the keys) fails in the loop.
  if (bundles.length === 0) throw new KeysMismatchError()

  const lowest = secrets.accounts[0].generation

  for (const entry of bundles) {
    if (entry.generation < lowest) continue

    const opened = await openBundle(userId, fromBase64Url(entry.bundle))
    const account = findAccountKey(secrets, entry.generation)

    if (
      opened.generation !== entry.generation ||
      !account ||
      !equal(account.publicKey, opened.accountPublic) ||
      !equal(secrets.identity.publicKey, opened.identityPublic)
    ) {
      throw new KeysMismatchError()
    }
  }
}

// ##### Setting up #####

// Makes the keys of a person, publishes the signed public half with the backup under the passphrase, and becomes the first device.
export async function setUp(deps: FlowDeps, userId: string, passphrase: string): Promise<Ready> {
  const secrets = await createAccountSecrets()
  const bundle = await signBundle(userId, secrets.identity, currentAccountKey(secrets))
  const backup = await sealBackup(userId, passphrase, secrets)

  await deps.api.setUp({ bundle: toBase64Url(bundle), backup: toBase64Url(backup) })

  try {
    return await becomeDevice(deps, userId, secrets)
  } catch (failure) {
    throw new SetUpIncompleteError({ cause: failure })
  }
}

// ##### Opening the backup #####

// A wrong passphrase and a damaged backup both come out as a CryptoError with the code 'authentication'.
export async function unlock(deps: FlowDeps, userId: string, passphrase: string): Promise<Ready> {
  const backup = await deps.api.getBackup()
  const secrets = await openBackup(userId, passphrase, fromBase64Url(backup.blob))

  await checkAgainstServer(userId, secrets, (await deps.api.getStatus()).bundles)
  return becomeDevice(deps, userId, secrets)
}

export function isWrongPassphrase(failure: unknown): boolean {
  return failure instanceof CryptoError && failure.code === 'authentication'
}

// ##### Changing the passphrase #####

// A new backup of the same secrets, made from the version of the backup this device last read. Another device that changed it first
// makes this a conflict, and the person retries from the new version.
export async function changePassphrase(deps: FlowDeps, userId: string, secrets: AccountSecrets, passphrase: string): Promise<void> {
  const current = await deps.api.getBackup()
  const blob = await sealBackup(userId, passphrase, secrets)

  await deps.api.replaceBackup({ blob: toBase64Url(blob), expectedVersion: current.version })
}

// ##### A lost device #####

// Makes the next generation of the account key, so a device that kept the private half of the older ones cannot open what is sealed
// from now on, and publishes it with the backup that holds it. Asks for the passphrase, which proves it is the person and not a browser
// someone left open, and puts the new generation in the backup under the same one. A device that is named is removed in the same step
// on the server (see the endpoint), and is the one that was lost. The identity key stays, so the people who know the person see
// nothing change.
//
// A failure leaves what this browser holds as it was. If the server took the change and this browser could not keep it, the next
// load finds the keys behind the server's and asks for the passphrase again, which brings them.
export async function addGeneration(
  deps: FlowDeps,
  userId: string,
  ready: Ready,
  passphrase: string,
  removeDeviceId?: string,
): Promise<Ready> {
  const current = await deps.api.getBackup()
  await openBackup(userId, passphrase, fromBase64Url(current.blob))

  // Made from keys that are the newest the server holds: a device that is behind would otherwise publish a generation made without the
  // private half of the one before it, and put a backup that lacks it in its place.
  await checkAgainstServer(userId, ready.secrets, (await deps.api.getStatus()).bundles)

  const secrets = await rotateAccountKey(ready.secrets)
  const bundle = await signBundle(userId, secrets.identity, currentAccountKey(secrets))
  const backup = await sealBackup(userId, passphrase, secrets)

  await deps.api.addGeneration({
    bundle: toBase64Url(bundle),
    backup: toBase64Url(backup),
    expectedBackupVersion: current.version,
    removeDeviceId,
  })

  await deps.store.save({ userId, deviceId: ready.deviceId, secrets: encodeSecrets(secrets) })
  return { deviceId: ready.deviceId, secrets }
}

// A device that is behind the server's newest generation opens the backup, which holds every generation, and keeps what it finds under
// the device it already is. A wrong passphrase and a damaged backup come out as a CryptoError with the code 'authentication'.
export async function catchUp(deps: FlowDeps, userId: string, deviceId: string, passphrase: string): Promise<Ready> {
  const backup = await deps.api.getBackup()
  const secrets = await openBackup(userId, passphrase, fromBase64Url(backup.blob))

  await checkAgainstServer(userId, secrets, (await deps.api.getStatus()).bundles)
  await deps.store.save({ userId, deviceId, secrets: encodeSecrets(secrets) })
  return { deviceId, secrets }
}

// ##### Starting over #####

// For a person with no device and no passphrase: new keys, as the generation after the newest the server has, under a new passphrase,
// confirmed with the account password. The server removes every device the person had. What was sealed to the old keys cannot be opened
// again by anyone, and the people who know the person are shown a new identity to accept.
export async function resetEncryption(deps: FlowDeps, userId: string, passphrase: string, password: string): Promise<Ready> {
  const status = await deps.api.getStatus()
  const newest = Math.max(0, ...status.bundles.map((entry) => entry.generation))

  const secrets = await startOverAccountSecrets(newest + 1)
  const bundle = await signBundle(userId, secrets.identity, currentAccountKey(secrets))
  const backup = await sealBackup(userId, passphrase, secrets)

  await deps.api.resetKeys({ bundle: toBase64Url(bundle), backup: toBase64Url(backup), password })

  try {
    return await becomeDevice(deps, userId, secrets)
  } catch (failure) {
    throw new SetUpIncompleteError({ cause: failure })
  }
}

// ##### Linking: the new device #####

export interface LinkAttempt {
  requestId: string
  code: string
  // The content of the QR code.
  uri: string
  expiresAt: string
  // Resolves when another device approved and this one holds the keys; rejects with LinkEndedError when the request ended, or
  // with an AbortError when the signal was aborted.
  result: Promise<Ready>
}

export async function startLink(deps: FlowDeps, userId: string, signal?: AbortSignal): Promise<LinkAttempt> {
  const temporary = await newX25519()
  const code = await linkCode(temporary.publicKey)
  const created = await deps.api.openLink({
    temporaryPublicKey: toBase64Url(temporary.publicKey),
    code,
    kind: deps.device.kind,
    name: deps.device.name,
  })

  async function collect(): Promise<Ready> {
    for (;;) {
      signal?.throwIfAborted()
      const status = await deps.api.pollLink(created.id)

      switch (status.state) {
        case 'Approved': {
          if (status.payload === null) throw new LinkEndedError('collected')
          const secrets = await openLinkSecrets(created.id, userId, temporary, fromBase64Url(status.payload))
          await checkAgainstServer(userId, secrets, (await deps.api.getStatus()).bundles)
          return becomeDevice(deps, userId, secrets)
        }
        case 'Collected':
          throw new LinkEndedError('collected')
        case 'Expired':
          throw new LinkEndedError('expired')
        case 'Pending':
          await deps.wait(LINK_POLL_MS, signal)
      }
    }
  }

  const result = collect()
  // The caller awaits it, but not necessarily in the same turn; a rejection must not be reported as unhandled meanwhile.
  result.catch(() => undefined)

  return { requestId: created.id, code, uri: linkUri(created.id, temporary.publicKey), expiresAt: created.expiresAt, result }
}

// ##### Linking: the device that approves #####

// Finds the request the person typed the code of, and checks the key the server returned is the one the code was made from. A
// server that returned a key of its own would fail here, and nothing is approved.
export async function findLinkRequest(deps: FlowDeps, input: string): Promise<LinkRequestInfo> {
  const code = normalizeLinkCode(input)
  if (code === undefined) throw new InvalidLinkCodeError()

  const found = await deps.api.findLink(code)
  if (!(await matchesLinkCode(code, fromBase64Url(found.temporaryPublicKey)))) throw new LinkKeyMismatchError()

  return found
}

export async function approveLinkRequest(deps: FlowDeps, userId: string, secrets: AccountSecrets, request: LinkRequestInfo): Promise<void> {
  const sealed = await sealLinkSecrets(request.id, userId, fromBase64Url(request.temporaryPublicKey), secrets)
  await deps.api.approveLink(request.id, toBase64Url(sealed))
}
