import axios from 'axios'
import { api } from '../../../lib/api'
import type { Registration } from '../pushKeys'

// The server's side of push. The address a browser is reached at (its endpoint) is a capability, so it goes in the body of a
// request and never in a URL, where it would end up in logs.

// The key a browser subscribes with, or null when this server has no push keys (it answers 404, and the page leaves push out).
export async function fetchPushKey(signal?: AbortSignal): Promise<string | null> {
  try {
    const { data } = await api.get<{ publicKey: string }>('/push/key', { signal })
    return data.publicKey
  } catch (error) {
    if (axios.isAxiosError(error) && error.response?.status === 404) return null
    throw error
  }
}

// Registers this browser for the signed-in person, or renews the registration. The page does it every time it loads.
export async function registerBrowser(registration: Registration): Promise<void> {
  await api.put('/push/subscriptions', registration)
}

export async function removeBrowser(endpoint: string): Promise<void> {
  await api.delete('/push/subscriptions', { data: { endpoint } })
}
