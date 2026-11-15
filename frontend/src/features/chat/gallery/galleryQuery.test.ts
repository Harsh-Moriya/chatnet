import { describe, expect, it } from 'vitest'
import type { Message } from '../types'
import { FILES_PAGE_SIZE, nextFilesCursor } from './galleryQuery'

const page = (length: number): Message[] =>
  Array.from({ length }, (_, i) => ({ id: `m${length - i}` }) as Message)

describe('where the gallery continues from', () => {
  it('continues from the oldest message of a full page, which is the last one since the page comes newest first', () => {
    expect(nextFilesCursor(page(FILES_PAGE_SIZE))).toBe('m1')
  })

  it('stops at a page that is shorter than a full one', () => {
    expect(nextFilesCursor(page(FILES_PAGE_SIZE - 1))).toBeUndefined()
    expect(nextFilesCursor([])).toBeUndefined()
  })
})
