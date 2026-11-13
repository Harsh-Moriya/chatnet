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
  // The next generation of the account key with the backup that holds it, and the lost device that goes with it, if there is one.
  addGeneration(body: { bundle: string; backup: string; expectedBackupVersion: number; removeDeviceId?: string }): Promise<KeyStatus>
  // New keys for a person with neither a device nor the passphrase, confirmed with the account password.
  resetKeys(body: { bundle: string; backup: string; password: string }): Promise<KeyStatus>
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

// ##### Conversation keys #####

// The key of each period of each conversation the person has opened here, so it is not opened from its envelope again. Kept per
// person, so two people who use one browser each have their own.
export interface EpochKeyStore {
  load(userId: string, conversationId: string, epoch: number): Promise<Uint8Array | null>
  save(userId: string, conversationId: string, epoch: number, key: Uint8Array): Promise<void>
  // The newest epoch of the conversation held here, or 0 when there is none.
  highest(userId: string, conversationId: string): Promise<number>
  // Everything of one person, at sign-out.
  removeAll(userId: string): Promise<void>
}

// The identity key a person had the first time they were seen. A later key that differs is a change the person must accept.
// "verifiedAt" is set when the person looking compared the safety number with them and said it matched; it belongs to this key, so
// accepting a different one starts without it.
export interface Pin {
  ownerId: string
  contactId: string
  identityPublic: Uint8Array
  firstSeenAt: string
  verifiedAt?: string
}

export interface PinStore {
  load(ownerId: string, contactId: string): Promise<Pin | null>
  save(pin: Pin): Promise<void>
  removeAll(ownerId: string): Promise<void>
}

// The current public keys of one person, as the server holds them.
export interface UserBundleWire {
  userId: string
  generation: number
  bundle: string
}

export interface EnvelopeWire {
  userId: string
  generation: number
  envelope: string
}

// A new epoch as it is sent: base64url bytes, an envelope for each member.
export interface EpochBody {
  epoch: number
  commitment: string
  signature: string
  envelopes: EnvelopeWire[]
}

export type EpochReason = 'Created' | 'MemberAdded' | 'Rotated'

// One epoch as the server gives it to one member: the proof, the people it was made for, and this member's own envelope.
export interface EpochRecordWire {
  epoch: number
  creatorId: string
  reason: EpochReason
  createdAt: string
  commitment: string
  signature: string
  members: { userId: string; generation: number }[]
  generation: number
  envelope: string
}

export interface EpochPageWire {
  epochs: EpochRecordWire[]
  hasMore: boolean
}

export interface ConversationKeyState {
  keyEpoch: number
  keyRotationRequired: boolean
}

// Everything the conversation keys ask of the server. The real one is epochApi.ts; the tests use a fake server behind the same interface.
export interface EpochApi {
  lookupKeys(userIds: string[]): Promise<UserBundleWire[]>
  getKeyState(conversationId: string): Promise<ConversationKeyState>
  // The people who are in the conversation now.
  getMemberIds(conversationId: string): Promise<string[]>
  getEpochs(conversationId: string, from: number, limit?: number): Promise<EpochPageWire>
  postEpoch(conversationId: string, body: EpochBody): Promise<void>
}
