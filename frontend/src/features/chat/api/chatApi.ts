import { api } from '../../../lib/api'
import { getApiError } from '../../../lib/errors'
import { newClientMessageId } from '../../../lib/uuid'
import { useAuthStore } from '../../auth/store/authStore'
import { describeKeyFailure } from '../../encryption/keyFailures'
import { HISTORY_PAGE_SIZE } from '../history'
import { mentionedUserIds } from '../mentions'
import { useChatStore } from '../store/chatStore'
import { waitForConnection } from '../store/waitForConnection'
import type {
  Conversation,
  ConversationUpdate,
  ConversationWire,
  Member,
  Message,
  MessageWire,
  PendingAttachment,
  UserSummary,
} from '../types'
import { entriesOf } from '../files/joinFiles'
import { conversationOf, openMessage as openWireMessage, openMessages, ownMessage } from './openWire'
import { openPreviews } from './previews'
import { fetchAttachmentLink } from './uploadsApi'

// Every REST call the chat makes. Plain functions, so they can be tested without React; the hooks
// in this folder only hand them to TanStack Query. REST loads data and confirms changes, SignalR
// only pushes (see the architecture overview), so nothing here touches the hub.
//
// Functions that bring server data into the store do so themselves. That makes the request
// function the one place where REST data enters the store, whether the request was started by a
// component, a refetch or a reconnect.

// A send that gets no answer in this time is shown as failed instead of sending for ever.
const SEND_TIMEOUT_MS = 15_000

const chat = () => useChatStore.getState()

// ##### Loading #####
// These two wait for the live connection first (see waitForConnection).

export async function fetchConversations(signal?: AbortSignal): Promise<Conversation[]> {
  await waitForConnection(signal)

  const { data } = await api.get<ConversationWire[]>('/conversations', { signal })
  const conversations = data.map(conversationOf)
  chat().seedConversations(conversations)
  void openPreviews(conversations)
  return conversations
}


// One page of a conversation's history, oldest message first: the latest page, or with `before` the
// page just older than that message id (what the reader scrolls back to), or with `after` the page
// just newer than it.
export interface MessagePageOptions {
  before?: string
  after?: string
  // A message to be shown in its place: the page is a page just older than it, the message, and a page just newer.
  around?: string
  signal?: AbortSignal
}

export async function fetchMessages(
  conversationId: string,
  options: MessagePageOptions = {},
): Promise<Message[]> {
  const page = await requestMessages(conversationId, options)
  chat().seedMessages(conversationId, page)
  return page
}

// The same request, without adding the page to the store. For a caller that has to decide first whether the page may
// go in: a page that does not join up with what the store holds would leave a hole in the history (the catch-up).
export async function requestMessages(
  conversationId: string,
  { before, after, around, signal }: MessagePageOptions = {},
): Promise<Message[]> {
  await waitForConnection(signal)

  const { data } = await api.get<MessageWire[]>(`/conversations/${conversationId}/messages`, {
    params: { before, after, around },
    signal,
  })
  return openMessages(data)
}

// ##### Reading a window of the history #####
// A notification can name a message far back. The conversation then shows a window of its history
// around that message (chatStore.showWindow), read in both directions from there until it reaches the newest message and is
// live again. These three requests are that: they put the window up, and extend it upwards and downwards.

// Shows the message in its place. The page comes back with up to a page on each side, so it says by its length whether there
// is more on either side.
export async function openMessage(conversationId: string, messageId: string, signal?: AbortSignal): Promise<void> {
  const page = await requestMessages(conversationId, { around: messageId, signal })

  const index = page.findIndex((m) => m.id === messageId)
  const older = index < 0 ? 0 : index
  const newer = index < 0 ? 0 : page.length - index - 1

  chat().showWindow(conversationId, page, {
    hasOlder: older >= HISTORY_PAGE_SIZE,
    reachesNewest: newer < HISTORY_PAGE_SIZE,
  })
}

// The page of messages just older than the first one held.
export async function loadOlderInWindow(conversationId: string, signal?: AbortSignal): Promise<void> {
  const first = chat().messages[conversationId]?.[0]
  if (!first) return

  const page = await requestMessages(conversationId, { before: first.id, signal })
  chat().seedMessages(conversationId, page)
  chat().setWindowHasOlder(conversationId, page.length >= HISTORY_PAGE_SIZE)
}

// The page of messages just newer than the last one held. A short page is the newest there is: what was pushed meanwhile joins the
// window and the conversation is live again.
export async function loadNewerInWindow(conversationId: string, signal?: AbortSignal): Promise<void> {
  const held = chat().messages[conversationId]
  const last = held?.[held.length - 1]
  if (!last) return

  const page = await requestMessages(conversationId, { after: last.id, signal })
  chat().seedMessages(conversationId, page)
  if (page.length < HISTORY_PAGE_SIZE) chat().reattach(conversationId)
}

// The people in a conversation and how far each has read. Read positions are pushed too, so this
// waits for the connection like the other loads.
export async function fetchMembers(conversationId: string, signal?: AbortSignal): Promise<Member[]> {
  await waitForConnection(signal)

  const { data } = await api.get<Member[]>(`/conversations/${conversationId}/members`, { signal })
  chat().seedMembers(conversationId, data)
  return data
}

// Search results are not pushed over the connection, so there is nothing to wait for.
export async function searchUsers(query: string, signal?: AbortSignal): Promise<UserSummary[]> {
  const { data } = await api.get<UserSummary[]>('/users', { params: { query }, signal })
  return data
}

// ##### Starting conversations #####
// The server also announces a new conversation over SignalR, to this tab as well as the others.
// Adding it here as well means the sidebar does not depend on that push arriving first; adding the
// same conversation twice is harmless.

// A conversation is made together with its first key period, made here for the people who will be in it and sealed to each of them: the
// server stores what is sent and checks that it is complete. The cryptography is loaded when the first conversation is started.
const loadKeys = () => import('../../encryption/conversationKeys')

export async function createDirect(otherUserId: string): Promise<Conversation> {
  const keys = await loadKeys()
  const deps = keys.epochDeps()

  const data = await keys.withEpochRetries(async () => {
    const prepared = await keys.prepareNewConversation(deps, [otherUserId])

    try {
      const { data } = await api.post<ConversationWire>('/conversations/direct', {
        id: prepared.conversationId,
        otherUserId,
        epoch: prepared.body,
      })
      // Asking for a conversation that exists answers with that one, whose key is not the one made here.
      await keys.keepNewConversationKey(deps, prepared, data.id)
      return data
    } catch (failure) {
      throw keys.refusalOf(failure)
    }
  })

  const conversation = conversationOf(data)
  chat().addConversation(conversation)
  void openPreviews([conversation])
  return conversation
}

export async function createGroup(name: string, memberIds: string[]): Promise<Conversation> {
  const keys = await loadKeys()
  const deps = keys.epochDeps()

  const data = await keys.withEpochRetries(async () => {
    const prepared = await keys.prepareNewConversation(deps, memberIds)

    try {
      const { data } = await api.post<ConversationWire>('/conversations/group', {
        id: prepared.conversationId,
        name,
        memberIds,
        epoch: prepared.body,
      })
      await keys.keepNewConversationKey(deps, prepared, data.id)
      return data
    } catch (failure) {
      throw keys.refusalOf(failure)
    }
  })

  const conversation = conversationOf(data)
  chat().addConversation(conversation)
  return conversation
}

// ##### Reading #####

// Tells the server the signed-in user has read up to and including this message. Once it accepts,
// the position is applied here at once rather than waiting for the push of it, which also reaches
// this tab; applying it twice changes nothing. A failure is the caller's to handle: the count stays,
// so the next change that makes marking worthwhile tries again.
export async function markRead(conversationId: string, messageId: string, selfId: string): Promise<void> {
  await api.post(`/conversations/${conversationId}/read`, { messageId })

  const message = chat().messages[conversationId]?.find((m) => m.id === messageId)
  if (message) chat().applyRead(conversationId, selfId, message.createdAt, selfId)
}

// ##### Changing a sent message #####

// What a failure to send or edit says, to show where the message is. The failures that have to do with keys say it in their own words
// (someone has not set up encryption, the keys were being made too often); anything else is the server's own answer.
function sentenceFor(error: unknown, conversationId: string): string {
  const members = chat().members[conversationId]
  const nameOf = (userId: string) => members?.find((m) => m.userId === userId)?.displayName ?? 'Someone'

  return describeKeyFailure(error, nameOf) ?? getApiError(error)
}

// What this tab knows of the keys of a conversation, to start from: a conversation whose key it holds costs the server nothing.
function keyStateOf(conversationId: string) {
  const conversation = chat().conversations[conversationId]
  return {
    keyEpoch: conversation?.keyEpoch ?? 0,
    keyRotationRequired: conversation?.keyRotationRequired ?? false,
    keyEpochAt: conversation?.keyEpochAt ?? null,
  }
}

// Replaces the text of one of the signed-in user's own messages. The new text is sealed for the next edit count, and the server settles
// the rest: it answers with the message as it is now and pushes the same to everyone in the conversation, this tab included; whichever
// arrives first is applied, and the other changes nothing. A refusal (the window has closed, the message was deleted, it was edited
// somewhere else first) is the caller's to show, in the words of the error.
export async function editMessage(
  conversationId: string,
  messageId: string,
  content: string,
  selfId: string,
): Promise<Message> {
  const held = chat().messages[conversationId]?.find((m) => m.id === messageId)
  if (!held || held.clientMessageId === null) throw new Error('This message is not loaded, so it cannot be edited.')

  // The edit replaces the whole sealed message, so the entries of its files go into it again, or nobody could open the files. An entry
  // that cannot be given would shift the others out of place.
  const files = entriesOf(held.attachments)
  if (files === null) throw new Error('This message has a file that cannot be opened, so it cannot be edited.')

  const text = content.trim()
  const editCount = held.editCount + 1
  const mentioned = mentionedUserIds(text, chat().members[conversationId], selfId)
  const keys = await loadKeys()

  try {
    const data = await keys.sealAndSend(
      conversationId,
      keyStateOf(conversationId),
      { clientMessageId: held.clientMessageId, editCount, text, files },
      async (sealed) => {
        try {
          const { data } = await api.patch<MessageWire>(`/conversations/${conversationId}/messages/${messageId}`, {
            ciphertext: sealed.ciphertext,
            editCount,
            mentionedUserIds: mentioned,
          })
          return data
        } catch (failure) {
          throw keys.refusalOf(failure)
        }
      },
    )

    const message = ownMessage(data, text, files)
    chat().replaceMessage(message, selfId)
    return message
  } catch (failure) {
    throw new Error(sentenceFor(failure, conversationId), { cause: failure })
  }
}

// Deletes a message: the signed-in user's own, or anyone's in a group they own or administer.
export async function deleteMessage(conversationId: string, messageId: string, selfId: string): Promise<Message> {
  const { data } = await api.delete<MessageWire>(`/conversations/${conversationId}/messages/${messageId}`)
  const message = await openWireMessage(data)
  chat().replaceMessage(message, selfId)
  return message
}

// ##### Muting #####

// Mutes or unmutes the conversation for the signed-in user. Applied here once the server has stored it; their
// other tabs hear about it by push. A failure is the caller's to show.
export async function setMuted(conversationId: string, isMuted: boolean): Promise<void> {
  await api.put(`/conversations/${conversationId}/mute`, { isMuted })
  chat().setMuted(conversationId, isMuted)
}

// ##### Managing a group #####
// Each answers with what the server stored, and the store takes it from there, so the screen never shows a change the server
// refused; the same change is pushed to everyone else in the group. A refusal is the caller's to show.

// Adds people to a group. The answer is the group's people as they are now. The group gets a new key period made for the people in it
// now and the ones added, so those who join cannot read what came before them; it is saved together with their joining.
export async function addMembers(conversationId: string, userIds: string[]): Promise<Member[]> {
  const keys = await loadKeys()
  const deps = keys.epochDeps()

  const data = await keys.withEpochRetries(async () => {
    const prepared = await keys.prepareAddingMembers(deps, conversationId, userIds)

    try {
      const { data } = await api.post<Member[]>(`/conversations/${conversationId}/members`, { userIds, epoch: prepared.body })
      await keys.saveKey(deps, conversationId, prepared.body.epoch, prepared.key)
      return data
    } catch (failure) {
      throw keys.refusalOf(failure)
    }
  })

  chat().seedMembers(conversationId, data)
  return data
}

export async function removeMember(conversationId: string, userId: string): Promise<Member[]> {
  const { data } = await api.delete<Member[]>(`/conversations/${conversationId}/members/${userId}`)
  chat().seedMembers(conversationId, data)
  return data
}

// Makes someone an admin or a plain member, or the owner, which makes the signed-in owner an admin.
export async function changeMemberRole(
  conversationId: string,
  userId: string,
  role: Member['role'],
): Promise<Member[]> {
  const { data } = await api.put<Member[]>(`/conversations/${conversationId}/members/${userId}/role`, { role })
  chat().seedMembers(conversationId, data)
  return data
}

// The signed-in user leaves. The conversation stays in their list, read-only.
export async function leaveConversation(conversationId: string): Promise<void> {
  const { data } = await api.post<{ leftAt: string }>(`/conversations/${conversationId}/leave`)
  chat().markLeft(conversationId, data.leftAt)
}

export async function renameConversation(conversationId: string, name: string): Promise<ConversationUpdate> {
  const { data } = await api.patch<ConversationUpdate>(`/conversations/${conversationId}`, { name })
  chat().applyConversationUpdate(data)
  return data
}

// ##### Sending #####
// The message appears at once, marked as sending. The server's copy replaces it when the response
// arrives or when the push does, whichever is first; both carry the id invented here.

// The files have already been uploaded (see uploadsApi); the message only names them. A message may be
// only files, with no text.
export async function sendMessage(
  conversationId: string,
  text: string,
  attachments: PendingAttachment[] = [],
): Promise<void> {
  const content = text.trim()
  if (content === '' && attachments.length === 0) return

  // A message sent while reading far back must appear where the conversation is: back at the newest messages.
  if (chat().windows[conversationId]?.buffer) chat().leaveWindow(conversationId)

  const clientMessageId = newClientMessageId()
  chat().addPending({
    clientMessageId,
    conversationId,
    content,
    attachments,
    createdAt: new Date().toISOString(),
    status: 'sending',
    error: null,
  })

  await post(conversationId, clientMessageId, content, attachments)
}

// Sends a failed message again with the same id. If the first attempt did reach the server and only
// its answer was lost, the push of that attempt still matches this message and confirms it. Only a
// failed message can be retried, so a double click cannot send it twice.
export async function retryMessage(conversationId: string, clientMessageId: string): Promise<void> {
  const failed = chat().pending[conversationId]?.find((p) => p.clientMessageId === clientMessageId)
  if (failed?.status !== 'failed') return

  chat().retryPending(conversationId, clientMessageId)
  await post(conversationId, clientMessageId, failed.content, failed.attachments)
}

export function dismissMessage(conversationId: string, clientMessageId: string): void {
  chat().removePending(conversationId, clientMessageId)
}

async function post(
  conversationId: string,
  clientMessageId: string,
  content: string,
  attachments: PendingAttachment[],
): Promise<void> {
  try {
    const keys = await loadKeys()
    const mentioned = mentionedUserIds(content, chat().members[conversationId], useAuthStore.getState().user?.userId)

    // Sealed under the epoch the conversation is in, which is looked at again if the server says it was the wrong one.
    const data = await keys.sealAndSend(
      conversationId,
      keyStateOf(conversationId),
      { clientMessageId, editCount: 0, text: content, files: attachments.map((attachment) => attachment.ref) },
      async (sealed) => {
        try {
          const { data } = await api.post<MessageWire>(
            `/conversations/${conversationId}/messages`,
            {
              ciphertext: sealed.ciphertext,
              clientMessageId,
              mentionedUserIds: mentioned,
              // Left out when there are none, so a message of only text is the request it always was.
              ...(attachments.length > 0 && {
                attachments: attachments.map(({ uploadKey }) => ({ uploadKey })),
              }),
            },
            { timeout: SEND_TIMEOUT_MS },
          )
          return data
        } catch (failure) {
          throw keys.refusalOf(failure)
        }
      },
    )

    // The response to a send is the sender's own message, so its sender is the signed-in user, and its text and files are what was sealed.
    chat().receiveMessage(ownMessage(data, content, attachments.map((attachment) => attachment.ref)), data.senderId)
  } catch (error) {
    // A message the push already confirmed is no longer unconfirmed, and failing an unknown id
    // does nothing, so a lost response cannot turn a delivered message into a failed one.
    chat().failPending(conversationId, clientMessageId, sentenceFor(error, conversationId))
  }
}

// ##### Attachment links #####

// The links that come with a message stop working after a while. A file that cannot be fetched asks for a
// new one, and the store puts it in the message so the file is fetched again. Answers the new address.
export async function refreshAttachmentUrl(
  conversationId: string,
  messageId: string,
  attachmentId: string,
): Promise<string> {
  const { url } = await fetchAttachmentLink(attachmentId)
  chat().setAttachmentUrl(conversationId, messageId, attachmentId, url)
  return url
}
