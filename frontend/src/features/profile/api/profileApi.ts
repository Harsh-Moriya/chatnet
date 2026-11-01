import axios from 'axios'
import { api } from '../../../lib/api'
import { useAuthStore } from '../../auth/store/authStore'
import { UploadFailed } from '../../chat/api/uploadsApi'
import { useChatStore } from '../../chat/store/chatStore'
import type { UserSummary } from '../../chat/types'

// Every REST call of the profile and of pictures. As in chatApi, the request function is the place where what the server
// confirmed enters the stores, so the screen never shows a change the server refused; the same change is pushed to
// everyone who needs to hear of it (ProfileChanged, ConversationUpdated).

const auth = () => useAuthStore.getState()
const chat = () => useChatStore.getState()

// Whose picture: the signed-in user's own, or a group's.
export type PictureTarget = { kind: 'me' } | { kind: 'group'; conversationId: string }

const pathOf = (target: PictureTarget) =>
  target.kind === 'me' ? '/users/me/avatar' : `/conversations/${target.conversationId}/avatar`

// The signed-in user's profile, as everyone else sees it.
export async function fetchMe(signal?: AbortSignal): Promise<UserSummary> {
  const { data } = await api.get<UserSummary>('/users/me', { signal })
  takeProfile(data)
  return data
}

export async function updateDisplayName(displayName: string): Promise<UserSummary> {
  const { data } = await api.patch<UserSummary>('/users/me', { displayName })
  takeProfile(data)
  return data
}

interface UploadTicket {
  uploadKey: string
  url: string
  expiresAt: string
}

// Makes the picture the one. Three requests: permission, the picture itself straight to the storage (which never goes
// through the API), and naming it. Returns the address of the picture.
export async function setPicture(target: PictureTarget, picture: Blob): Promise<string | null> {
  const { data: ticket } = await api.post<UploadTicket>(`${pathOf(target)}/uploads`, {
    contentType: picture.type,
    sizeBytes: picture.size,
  })

  try {
    // The plain axios, not the API client: this goes to another host, and the API token must not be sent there. The
    // content type is part of what the link was signed for, so it has to match.
    await axios.put(ticket.url, picture, { headers: { 'Content-Type': picture.type } })
  } catch {
    throw new UploadFailed()
  }

  const { data } = await api.put<{ avatarUrl: string | null }>(pathOf(target), { uploadKey: ticket.uploadKey })
  takePicture(target, data.avatarUrl)
  return data.avatarUrl
}

export async function removePicture(target: PictureTarget): Promise<void> {
  await api.delete(pathOf(target))
  takePicture(target, null)
}

// A profile the server confirmed: the signed-in user's own, and the places the chat shows them.
function takeProfile(profile: UserSummary) {
  auth().updateProfile(profile)
  chat().applyProfileChange(profile)
}

function takePicture(target: PictureTarget, avatarUrl: string | null) {
  if (target.kind === 'me') {
    const user = auth().user
    if (user) takeProfile({ id: user.userId, userName: user.userName, displayName: user.displayName, avatarUrl })
    return
  }

  const held = chat().conversations[target.conversationId]
  if (held) chat().applyConversationUpdate({ id: held.id, name: held.name, avatarUrl })
}
