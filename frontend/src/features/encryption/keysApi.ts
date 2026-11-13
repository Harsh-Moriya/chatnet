import { api } from '../../lib/api'
import type { BackupBlob, DeviceInfo, KeyStatus, KeysApi, LinkCreated, LinkRequestInfo, LinkStatus } from './types'

// The encryption endpoints, over the API client that attaches and renews the access token.
export const keysApi: KeysApi = {
  getStatus: async () => (await api.get<KeyStatus>('/keys/me')).data,

  setUp: async (body) => (await api.post<KeyStatus>('/keys/me', body)).data,

  getBackup: async () => (await api.get<BackupBlob>('/keys/me/backup')).data,

  replaceBackup: async (body) => (await api.put<{ version: number }>('/keys/me/backup', body)).data,

  addGeneration: async (body) => (await api.post<KeyStatus>('/keys/me/generations', body)).data,

  resetKeys: async (body) => (await api.post<KeyStatus>('/keys/me/reset', body)).data,

  registerDevice: async (body) => (await api.post<DeviceInfo>('/devices', body)).data,

  listDevices: async () => (await api.get<DeviceInfo[]>('/devices')).data,

  removeDevice: async (id) => {
    await api.delete(`/devices/${id}`)
  },

  openLink: async (body) => (await api.post<LinkCreated>('/devices/link-requests', body)).data,

  // The code goes in the query, which the server's request log leaves out.
  findLink: async (code) => (await api.get<LinkRequestInfo>('/devices/link-requests', { params: { code } })).data,

  pollLink: async (id) => (await api.get<LinkStatus>(`/devices/link-requests/${id}`)).data,

  approveLink: async (id, payload) => {
    await api.post(`/devices/link-requests/${id}/approve`, { payload })
  },
}
