import { describe, expect, it } from 'vitest'
import {
  ALLOWED_CONTENT_TYPES,
  FILE_PICKER_ACCEPT,
  MAX_FILE_BYTES,
  MAX_FILES_PER_MESSAGE,
  attachmentPreview,
  attachmentTypeOf,
  checkFile,
} from './attachments'

const file = (type: string, size = 1000, name = 'file.bin') => ({ name, size, type })

describe('attachmentTypeOf', () => {
  it('treats images and videos as such and everything else as a file', () => {
    expect(attachmentTypeOf('image/png')).toBe('Image')
    expect(attachmentTypeOf('image/webp')).toBe('Image')
    expect(attachmentTypeOf('video/mp4')).toBe('Video')
    expect(attachmentTypeOf('application/pdf')).toBe('File')
    expect(attachmentTypeOf('text/plain')).toBe('File')
    expect(attachmentTypeOf('application/zip')).toBe('File')
  })
})

describe('checkFile', () => {
  it('accepts every allowed type', () => {
    for (const type of ALLOWED_CONTENT_TYPES) expect(checkFile(file(type), 0)).toBeNull()
  })

  it('refuses a type that is not allowed, naming the file', () => {
    const problem = checkFile(file('application/x-msdownload', 1000, 'setup.exe'), 0)

    expect(problem).toContain('setup.exe')
    expect(problem).toContain('cannot be sent')
  })

  it('refuses a file without a type, which is what the browser reports for one it does not know', () => {
    expect(checkFile(file('', 1000, 'mystery'), 0)).toContain('mystery')
  })

  it('refuses SVG, which can carry script', () => {
    expect(checkFile(file('image/svg+xml'), 0)).not.toBeNull()
  })

  it('refuses an empty file', () => {
    expect(checkFile(file('image/png', 0, 'blank.png'), 0)).toContain('empty')
  })

  it('accepts a file of exactly the size limit and refuses one byte more', () => {
    expect(checkFile(file('image/png', MAX_FILE_BYTES), 0)).toBeNull()
    expect(checkFile(file('image/png', MAX_FILE_BYTES + 1, 'big.png'), 0)).toContain('10 MB')
  })

  it('refuses a file once the message has as many as it may carry', () => {
    expect(checkFile(file('image/png'), MAX_FILES_PER_MESSAGE - 1)).toBeNull()
    expect(checkFile(file('image/png'), MAX_FILES_PER_MESSAGE)).toContain(String(MAX_FILES_PER_MESSAGE))
  })

  it('offers the file dialog every allowed type', () => {
    for (const type of ALLOWED_CONTENT_TYPES) expect(FILE_PICKER_ACCEPT).toContain(type)
  })
})

describe('attachmentPreview', () => {
  it('says "Attachment" for one file and counts several', () => {
    expect(attachmentPreview(1)).toBe('Attachment')
    expect(attachmentPreview(3)).toBe('3 attachments')
  })
})
