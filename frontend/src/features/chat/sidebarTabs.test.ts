import { describe, expect, it } from 'vitest'
import { SIDEBAR_VIEWS, viewAfterKey } from './sidebarTabs'

describe('moving along the bar with the arrow keys', () => {
  it('goes right to the next panel and round to the first after the last', () => {
    expect(viewAfterKey('chats', 'ArrowRight')).toBe('new')
    expect(viewAfterKey('new', 'ArrowRight')).toBe('search')
    expect(viewAfterKey('search', 'ArrowRight')).toBe('chats')
  })

  it('goes left to the previous panel and round to the last before the first', () => {
    expect(viewAfterKey('search', 'ArrowLeft')).toBe('new')
    expect(viewAfterKey('new', 'ArrowLeft')).toBe('chats')
    expect(viewAfterKey('chats', 'ArrowLeft')).toBe('search')
  })

  it('goes to the ends with Home and End, from anywhere', () => {
    for (const view of SIDEBAR_VIEWS) {
      expect(viewAfterKey(view, 'Home')).toBe('chats')
      expect(viewAfterKey(view, 'End')).toBe('search')
    }
  })

  it('leaves every other key alone', () => {
    for (const key of ['ArrowUp', 'ArrowDown', 'Enter', ' ', 'Tab', 'a']) expect(viewAfterKey('new', key)).toBeNull()
  })
})
