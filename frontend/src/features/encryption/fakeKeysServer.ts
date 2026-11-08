import type {
  BackupBlob,
  BundleEntry,
  DeviceInfo,
  DeviceKind,
  DeviceStore,
  KeyStatus,
  KeysApi,
  LinkCreated,
  LinkRequestInfo,
  LinkState,
  LinkStatus,
  StoredDevice,
} from './types'

// For the tests of the flows: a server that behaves like the real one where it matters to them (one set-up, a versioned backup,
// devices, link requests whose sealed secrets are handed over once) and opens nothing, as the real one cannot. Not used by the app.

export class ServerRefused extends Error {
  readonly status: number

  constructor(status: number) {
    super(`The server answered ${status}.`)
    this.name = 'ServerRefused'
    this.status = status
  }
}

interface LinkRecord {
  info: LinkRequestInfo
  code: string
  state: LinkState
  payload: string | null
}

let counter = 0
const nextId = () => `00000000-0000-4000-8000-${String(++counter).padStart(12, '0')}`

export class FakeKeysServer implements KeysApi {
  bundles: BundleEntry[] = []
  backup: { blob: string; version: number } | null = null
  devices: DeviceInfo[] = []
  links = new Map<string, LinkRecord>()

  // Set to make the next call of that name fail.
  failNext = new Map<keyof KeysApi, Error>()
  calls: string[] = []

  private enter(name: keyof KeysApi): void {
    this.calls.push(name)
    const failure = this.failNext.get(name)
    if (failure) {
      this.failNext.delete(name)
      throw failure
    }
  }

  async getStatus(): Promise<KeyStatus> {
    this.enter('getStatus')
    return { bundles: [...this.bundles], backup: this.backup && { version: this.backup.version, updatedAt: new Date().toISOString() } }
  }

  async setUp(body: { bundle: string; backup: string }): Promise<KeyStatus> {
    this.enter('setUp')
    if (this.bundles.length > 0) throw new ServerRefused(409)
    this.bundles = [{ generation: 1, bundle: body.bundle }]
    this.backup = { blob: body.backup, version: 1 }
    return this.getStatus()
  }

  async getBackup(): Promise<BackupBlob> {
    this.enter('getBackup')
    if (!this.backup) throw new ServerRefused(404)
    return { ...this.backup, updatedAt: new Date().toISOString() }
  }

  async replaceBackup(body: { blob: string; expectedVersion: number }): Promise<{ version: number }> {
    this.enter('replaceBackup')
    if (!this.backup) throw new ServerRefused(404)
    if (this.backup.version !== body.expectedVersion) throw new ServerRefused(409)
    this.backup = { blob: body.blob, version: this.backup.version + 1 }
    return { version: this.backup.version }
  }

  async registerDevice(body: { kind: DeviceKind; name: string }): Promise<DeviceInfo> {
    this.enter('registerDevice')
    const now = new Date().toISOString()
    const device = { id: nextId(), kind: body.kind, name: body.name, createdAt: now, lastActiveAt: now }
    this.devices.push(device)
    return device
  }

  async listDevices(): Promise<DeviceInfo[]> {
    this.enter('listDevices')
    return [...this.devices]
  }

  async removeDevice(id: string): Promise<void> {
    this.enter('removeDevice')
    if (!this.devices.some((device) => device.id === id)) throw new ServerRefused(404)
    this.devices = this.devices.filter((device) => device.id !== id)
  }

  async openLink(body: { temporaryPublicKey: string; code: string; kind: DeviceKind; name: string }): Promise<LinkCreated> {
    this.enter('openLink')
    const id = nextId()
    const expiresAt = new Date(Date.now() + 600_000).toISOString()
    const info = { id, temporaryPublicKey: body.temporaryPublicKey, kind: body.kind, name: body.name, createdAt: new Date().toISOString(), expiresAt }
    this.links.set(id, { info, code: body.code, state: 'Pending', payload: null })
    return { id, expiresAt }
  }

  async findLink(code: string): Promise<LinkRequestInfo> {
    this.enter('findLink')
    const found = [...this.links.values()].find((link) => link.code === code && link.state === 'Pending')
    if (!found) throw new ServerRefused(404)
    return { ...found.info }
  }

  async pollLink(id: string): Promise<LinkStatus> {
    this.enter('pollLink')
    const link = this.links.get(id)
    if (!link) throw new ServerRefused(404)

    if (link.state === 'Approved' && link.payload !== null) {
      const payload = link.payload
      link.payload = null
      link.state = 'Collected'
      return { state: 'Approved', payload, expiresAt: link.info.expiresAt }
    }

    return { state: link.state, payload: null, expiresAt: link.info.expiresAt }
  }

  async approveLink(id: string, payload: string): Promise<void> {
    this.enter('approveLink')
    const link = this.links.get(id)
    if (!link) throw new ServerRefused(404)
    if (link.state !== 'Pending') throw new ServerRefused(409)
    link.state = 'Approved'
    link.payload = payload
  }

  // ##### For a test to arrange or tamper with #####

  expire(id: string): void {
    const link = this.links.get(id)
    if (link) link.state = 'Expired'
  }
}

export class MemoryDeviceStore implements DeviceStore {
  readonly records = new Map<string, StoredDevice>()
  private last: string | null = null
  failNextSave: Error | null = null

  async load(userId: string): Promise<StoredDevice | null> {
    return this.records.get(userId) ?? null
  }

  async save(device: StoredDevice): Promise<void> {
    if (this.failNextSave) {
      const failure = this.failNextSave
      this.failNextSave = null
      throw failure
    }
    this.records.set(device.userId, device)
    this.last = device.userId
  }

  async remove(userId: string): Promise<void> {
    this.records.delete(userId)
    if (this.last === userId) this.last = null
  }

  async lastDeviceId(): Promise<string | null> {
    return this.last === null ? null : (this.records.get(this.last)?.deviceId ?? null)
  }
}
