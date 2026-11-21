import { describe, expect, it } from 'vitest'
import { actionsInMenu } from './headerActions'

const action = (key: string, essential: boolean) => ({ key, essential })
const keys = (actions: readonly { key: string }[]) => actions.map((a) => a.key)

describe('which header actions go into the menu on a narrow header', () => {
  it('is the actions that are not essential, in the order they have in the header', () => {
    const header = [action('gallery', false), action('audio', true), action('video', true), action('safety', false), action('mute', false)]

    expect(keys(actionsInMenu(header))).toEqual(['gallery', 'safety', 'mute'])
  })

  it('leaves the essential ones out of the menu', () => {
    expect(actionsInMenu([action('audio', true), action('video', true), action('group', true)])).toEqual([])
  })

  it('is no menu for a single action, which stays a button (a group the reader left has only its pictures)', () => {
    expect(actionsInMenu([action('gallery', false)])).toEqual([])
    expect(actionsInMenu([action('gallery', false), action('group', true)])).toEqual([])
  })

  it('is a menu from two actions on', () => {
    expect(keys(actionsInMenu([action('gallery', false), action('mute', false)]))).toEqual(['gallery', 'mute'])
    expect(keys(actionsInMenu([action('gallery', false), action('group', true), action('mute', false)]))).toEqual(['gallery', 'mute'])
  })

  it('is no menu when there are no actions', () => {
    expect(actionsInMenu([])).toEqual([])
  })
})
