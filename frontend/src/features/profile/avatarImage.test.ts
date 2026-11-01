import { describe, expect, it } from 'vitest'
import { AVATAR_SIZE, coverSquare, outputSize } from './avatarImage'

describe('the part of a picture that is kept', () => {
  it('is the middle square of a wide picture', () => {
    expect(coverSquare(400, 200)).toEqual({ x: 100, y: 0, side: 200 })
  })

  it('is the middle square of a tall picture', () => {
    expect(coverSquare(200, 600)).toEqual({ x: 0, y: 200, side: 200 })
  })

  it('is the whole of a square picture', () => {
    expect(coverSquare(300, 300)).toEqual({ x: 0, y: 0, side: 300 })
  })

  it('stays on whole pixels when the difference is odd', () => {
    expect(coverSquare(101, 100)).toEqual({ x: 0, y: 0, side: 100 })
  })
})

describe('the size a picture is shrunk to', () => {
  it('is the avatar size for a large picture', () => {
    expect(outputSize(4000)).toBe(AVATAR_SIZE)
    expect(outputSize(AVATAR_SIZE)).toBe(AVATAR_SIZE)
  })

  it('is its own size for a picture smaller than that, which is not stretched', () => {
    expect(outputSize(64)).toBe(64)
  })
})
