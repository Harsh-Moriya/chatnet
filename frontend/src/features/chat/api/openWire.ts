import type { FileRef } from '../../../lib/crypto/message'
import { createKeyedQueue } from '../../../lib/keyedQueue'
import type { UnreadableReason } from '../../encryption/sealedMessages'
import type { NotificationItem, NotificationWire } from '../../notifications/types'
import { joinFiles, unvouched } from '../files/joinFiles'
import { previewOf } from '../preview'
import type { Conversation, ConversationWire, LastMessage, Message, MessageWire } from '../types'

// Where what the server hands over is opened. The server sends a message sealed and cannot read it, so a message, the preview of a
// conversation and a notification each arrive as a blob, and are opened here before the rest of the app sees them. Opening never fails:
// a message that cannot be opened is one that says why (see Unreadable), and the rest of the conversation is read as usual. The
// cryptography is loaded when the first message is opened.

type Keys = typeof import('../../encryption/conversationKeys')

// Every message that is opened needs the module, and many are opened at once (a page of history, the previews of the list), so they share
// one request for it. A request that failed is not kept, so the next message tries again.
let keys: Promise<Keys> | null = null

function loadKeys(): Promise<Keys> {
  keys ??= import('../../encryption/conversationKeys').catch((failure: unknown) => {
    keys = null
    throw failure
  })
  return keys
}

// What a message was sealed as, whatever it is part of.
interface Sealed {
  conversationId: string
  senderId: string
  clientMessageId: string
  editCount: number
  keyEpoch: number
  ciphertext: string
}

async function open(sealed: Sealed): Promise<{ text: string; files: FileRef[] } | { reason: UnreadableReason }> {
  try {
    const result = await (await loadKeys()).messageOpener().open(sealed)
    return result.readable ? { text: result.text, files: result.files } : { reason: result.reason }
  } catch {
    // The keys are not available (nobody is signed in, or the page that holds them could not be loaded): nothing is lost, and the
    // message is opened again when they are.
    return { reason: 'noKey' }
  }
}

// The pushed messages of one conversation are opened one after another, in the order they arrived, so an edit cannot overtake the
// message it is an edit of while that is still being opened.
const pushes = createKeyedQueue()

export const inArrivalOrder = <T>(conversationId: string, job: () => Promise<T>): Promise<T> => pushes.run(conversationId, job)

// ##### Messages #####

// The message as the server sent it, with nothing opened: its files are not vouched for until the sealed message says what they are.
function held(wire: MessageWire): Message {
  return { ...wire, content: null, unreadable: null, attachments: unvouched(wire.attachments) }
}

// Whoever keeps what the person reads (the local store search runs over) is handed each message as it is opened, so there is one
// place that sees every message that reaches the browser: a page of history, a push, the answer to a send or an edit. It is registered
// from outside, so the chat does not depend on it, and what it does with a message can never stop the message from being shown.
type OpenedSink = (message: Message) => void

let sink: OpenedSink | null = null

export function setOpenedSink(next: OpenedSink | null): void {
  sink = next
}

function remember(message: Message): Message {
  try {
    sink?.(message)
  } catch {
    // Keeping a message is for later; it is never a reason not to show it.
  }
  return message
}

async function openOne(wire: MessageWire): Promise<Message> {
  if (wire.ciphertext === null) return held(wire)

  const opened = await open({
    conversationId: wire.conversationId,
    senderId: wire.senderId,
    clientMessageId: wire.clientMessageId,
    editCount: wire.editCount,
    keyEpoch: wire.keyEpoch,
    ciphertext: wire.ciphertext,
  })

  return 'text' in opened
    ? { ...held(wire), content: opened.text, attachments: joinFiles(wire.attachments, opened.files) }
    : { ...held(wire), unreadable: opened.reason }
}

export const openMessage = async (wire: MessageWire): Promise<Message> => remember(await openOne(wire))

export const openMessages = (wires: MessageWire[]): Promise<Message[]> => Promise.all(wires.map(openMessage))

// Opens messages for the one that keeps them, which writes what it opens itself and must not be handed it twice.
export const openMessagesQuietly = (wires: MessageWire[]): Promise<Message[]> => Promise.all(wires.map(openOne))

// The message this tab sealed and the server stored: the text and the entries of its files are known, so it is not opened again.
export function ownMessage(wire: MessageWire, text: string, files: readonly FileRef[]): Message {
  return remember({ ...held(wire), content: text, attachments: joinFiles(wire.attachments, files) })
}

// A message as the app holds it, sealed again as the server would hand it over, to open it once its key has arrived.
export function sealedOf(message: Message): MessageWire | null {
  if (message.ciphertext == null || message.keyEpoch === undefined || message.clientMessageId === null || message.changeSeq === undefined) return null

  return {
    id: message.id,
    conversationId: message.conversationId,
    senderId: message.senderId,
    senderDisplayName: message.senderDisplayName,
    ciphertext: message.ciphertext,
    keyEpoch: message.keyEpoch,
    createdAt: message.createdAt,
    attachments: message.attachments.map(({ id, sizeBytes, url }) => ({ id, sizeBytes, url })),
    clientMessageId: message.clientMessageId,
    editedAt: message.editedAt,
    editCount: message.editCount,
    isDeleted: message.isDeleted,
    changeSeq: message.changeSeq,
  }
}

// ##### The preview of a conversation #####

// The conversation as the list shows it before its last message is opened: the preview is there, and says it is being opened.
export function conversationOf(wire: ConversationWire): Conversation {
  const { lastMessage, ...rest } = wire
  if (lastMessage === null) return { ...rest, lastMessage: null }

  const { ciphertext, keyEpoch, clientMessageId, editCount, ...shown } = lastMessage
  return {
    ...rest,
    lastMessage: {
      ...shown,
      content: null,
      unreadable: ciphertext === null ? null : 'pending',
      sealed: ciphertext === null ? null : { ciphertext, keyEpoch, clientMessageId, editCount },
    },
  }
}

// The last message with its text opened, or with the reason it could not be.
export async function openLastMessage(conversationId: string, last: LastMessage): Promise<LastMessage> {
  if (!last.sealed) return last

  const opened = await open({ conversationId, senderId: last.senderId, ...last.sealed })
  return 'text' in opened
    ? { ...last, content: previewOf(opened.text), unreadable: null }
    : { ...last, content: null, unreadable: opened.reason }
}

// ##### Notifications #####

export async function openNotification(wire: NotificationWire): Promise<NotificationItem> {
  const { message, ...rest } = wire
  if (message === null || wire.conversationId === null) return { ...rest, messagePreview: null }

  const opened = await open({ conversationId: wire.conversationId, senderId: wire.actorId, ...message })
  return { ...rest, messagePreview: 'text' in opened ? previewOf(opened.text) : null }
}
