import { type AccountSecrets, createAccountSecrets, currentAccountKey, signBundle, toBase64Url } from '../../lib/crypto'
import { type EpochDeps, keepNewConversationKey, prepareNewConversation } from './epochs'
import { FakeEpochServer, MemoryEpochKeyStore, MemoryPinStore } from './fakeEpochServer'
import type { EpochApi } from './types'

// For the tests that run the conversation keys against the fake server: people with real keys of their own, a conversation started the
// way a browser starts one, and a server whose answers can be changed on their way. Not used by the app.

export interface Person {
  id: string
  secrets: AccountSecrets
  keys: MemoryEpochKeyStore
  pins: MemoryPinStore
  deps: EpochDeps
}

export async function bundleOf(userId: string, secrets: AccountSecrets): Promise<string> {
  return toBase64Url(await signBundle(userId, secrets.identity, currentAccountKey(secrets)))
}

export async function newPerson(server: FakeEpochServer, options: { register?: boolean } = {}): Promise<Person> {
  const id = crypto.randomUUID()
  const secrets = await createAccountSecrets()
  if (options.register !== false) server.registerBundle(id, 1, await bundleOf(id, secrets))
  return personFrom(server, id, secrets)
}

// The person reads the fake server's clock, not the real one: the server dates the epochs it stores, and a person who compared those dates
// with today's would find every key older than a week as soon as the real date had moved on from the one the server starts at.
export function personFrom(server: FakeEpochServer, id: string, secrets: AccountSecrets): Person {
  const keys = new MemoryEpochKeyStore()
  const pins = new MemoryPinStore()
  return { id, secrets, keys, pins, deps: { api: server.as(id), keys, pins, self: { userId: id, secrets }, now: () => server.now } }
}

// A conversation started by `creator` for these people, as a browser starts one.
export async function start(server: FakeEpochServer, creator: Person, others: Person[]): Promise<string> {
  const prepared = await prepareNewConversation(creator.deps, others.map((person) => person.id))
  server.start(creator.id, prepared.conversationId, [creator.id, ...others.map((person) => person.id)], prepared.body)
  await keepNewConversationKey(creator.deps, prepared, prepared.conversationId)
  return prepared.conversationId
}

// The page the server gives a person, changed on its way.
export function serving(server: FakeEpochServer, person: Person, change: (page: Awaited<ReturnType<EpochApi['getEpochs']>>) => void): Person {
  const api = server.as(person.id)
  const altered: EpochApi = {
    ...api,
    getEpochs: async (...args) => {
      const page = await api.getEpochs(...args)
      change(page)
      return page
    },
  }
  return { ...person, deps: { ...person.deps, api: altered } }
}
