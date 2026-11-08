// The shapes the encryption endpoints send and take. Bytes are base64url without padding, as everywhere in the API.

export type DeviceKind = 'Web' | 'Ios' | 'Android'

export interface BundleEntry {
  generation: number
  bundle: string
}

export interface KeyStatus {
  bundles: BundleEntry[]
  backup: { version: number; updatedAt: string } | null
}

export interface BackupBlob {
  blob: string
  version: number
  updatedAt: string
}

export interface DeviceInfo {
  id: string
  kind: DeviceKind
  name: string
  createdAt: string
  lastActiveAt: string
}

export interface LinkCreated {
  id: string
  expiresAt: string
}

// What a signed-in device is shown to approve.
export interface LinkRequestInfo {
  id: string
  temporaryPublicKey: string
  kind: DeviceKind
  name: string
  createdAt: string
  expiresAt: string
}

export type LinkState = 'Pending' | 'Approved' | 'Collected' | 'Expired'

export interface LinkStatus {
  state: LinkState
  // The sealed secrets, on the one answer that says Approved.
  payload: string | null
  expiresAt: string
}

// Everything the flows ask of the server. The real one is keysApi.ts; the tests use a fake server behind the same interface.
export interface KeysApi {
  getStatus(): Promise<KeyStatus>
  setUp(body: { bundle: string; backup: string }): Promise<KeyStatus>
  getBackup(): Promise<BackupBlob>
  replaceBackup(body: { blob: string; expectedVersion: number }): Promise<{ version: number }>
  registerDevice(body: { kind: DeviceKind; name: string }): Promise<DeviceInfo>
  listDevices(): Promise<DeviceInfo[]>
  removeDevice(id: string): Promise<void>
  openLink(body: { temporaryPublicKey: string; code: string; kind: DeviceKind; name: string }): Promise<LinkCreated>
  findLink(code: string): Promise<LinkRequestInfo>
  pollLink(id: string): Promise<LinkStatus>
  approveLink(id: string, payload: string): Promise<void>
}

// What this browser keeps for a person who has keys on it: the secrets exactly as the backup holds them, and the id the server
// gave this device.
export interface StoredDevice {
  userId: string
  deviceId: string
  secrets: Uint8Array
}

export interface DeviceStore {
  load(userId: string): Promise<StoredDevice | null>
  save(device: StoredDevice): Promise<void>
  remove(userId: string): Promise<void>
  // The device of the person who last had keys saved here, so a sign-in can say which device it is before it knows who signs in.
  lastDeviceId(): Promise<string | null>
}
