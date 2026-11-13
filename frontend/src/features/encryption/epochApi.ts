import axios from 'axios'
import { api } from '../../lib/api'
import { EpochRefused } from './epochs'
import type { ConversationKeyState, EpochApi, EpochBody, EpochPageWire, UserBundleWire } from './types'

// The conversation key endpoints, over the API client that attaches and renews the access token.

// A conflict that carries the server's "code" is about the keys of a conversation (see KeyEpochCodes on the server); anything else, a
// conflict about something else included, is passed on as it was.
export function refusalOf(failure: unknown): unknown {
  if (axios.isAxiosError(failure) && failure.response?.status === 409) {
    const code = (failure.response.data as { code?: unknown } | undefined)?.code
    if (typeof code === 'string') return new EpochRefused(code)
  }

  return failure
}

export const epochApi: EpochApi = {
  lookupKeys: async (userIds) => (await api.post<{ bundles: UserBundleWire[] }>('/keys/lookup', { userIds })).data.bundles,

  getKeyState: async (conversationId) => {
    const { data } = await api.get<ConversationKeyState>(`/conversations/${conversationId}`)
    return { keyEpoch: data.keyEpoch, keyRotationRequired: data.keyRotationRequired, keyEpochAt: data.keyEpochAt ?? null }
  },

  getMemberIds: async (conversationId) =>
    (await api.get<{ userId: string }[]>(`/conversations/${conversationId}/members`)).data.map((member) => member.userId),

  getEpochs: async (conversationId, from, limit) =>
    (await api.get<EpochPageWire>(`/conversations/${conversationId}/key-epochs`, { params: { from, limit } })).data,

  postEpoch: async (conversationId: string, body: EpochBody) => {
    try {
      await api.post(`/conversations/${conversationId}/key-epochs`, body)
    } catch (failure) {
      throw refusalOf(failure)
    }
  },
}
