import { describe, expect, it } from 'vitest'
import { INLINE_PICTURE_MAX_BYTES, cleanFileName, imageTypeOf, isInlinePicture, kindOf, pdfTypeOf, videoTypeOf } from './fileNames'

describe('cleaning the name of a file', () => {
  it.each([
    ['photo.png', 'photo.png'],
    ['  spaced out.pdf  ', 'spaced out.pdf'],
    ['résumé 履歴書.pdf', 'résumé 履歴書.pdf'],
  ])('keeps an ordinary name: %s', (name, expected) => {
    expect(cleanFileName(name)).toBe(expected)
  })

  it.each([
    ['C:\\Users\\me\\docs\\plan.pdf', 'plan.pdf'],
    ['/etc/passwd', 'passwd'],
    ['../../secret.txt', 'secret.txt'],
    ['a/b\\c/d.txt', 'd.txt'],
  ])('keeps only the last part of a path: %s', (name, expected) => {
    expect(cleanFileName(name)).toBe(expected)
  })

  it('removes control characters', () => {
    expect(cleanFileName('a\r\nb\0.txt')).toBe('ab.txt')
    expect(cleanFileName('a\tb.txt')).toBe('ab.txt')
  })

  // The right-to-left override turns "invoice\u202Egpj.exe" into text that reads "invoiceexe.jpg".
  it('removes the invisible formatting characters that disguise an extension', () => {
    expect(cleanFileName('invoice\u202Egpj.exe')).toBe('invoicegpj.exe')
    expect(cleanFileName('a\u200Bb.pdf')).toBe('ab.pdf')
  })

  it.each([[''], ['   '], ['\r\n'], ['folder/'], ['\u202E']])('says "file" when nothing is left: %j', (name) => {
    expect(cleanFileName(name)).toBe('file')
  })

  it('cuts a name to the longest a file may have, and trims what that leaves', () => {
    expect(cleanFileName('a'.repeat(400))).toHaveLength(255)
    expect(cleanFileName(`${'a'.repeat(254)} b`)).toBe('a'.repeat(254))
  })
})

describe('what a type lets a file be shown as', () => {
  it.each(['image/png', 'IMAGE/PNG', 'image/png; charset=binary', '  image/jpeg ', 'image/gif', 'image/webp'])('draws %s as a picture', (type) => {
    expect(imageTypeOf(type)).toBe(type.split(';')[0].trim().toLowerCase())
    expect(kindOf({ type })).toBe('Image')
  })

  it.each(['image/svg+xml', 'text/html', 'application/javascript', 'image', '', 'image/png+xml', 'video/mp4'])(
    'does not draw %j as a picture',
    (type) => {
      expect(imageTypeOf(type)).toBeNull()
    },
  )

  it.each(['video/mp4', 'VIDEO/WEBM', 'video/webm; codecs=vp9'])('plays %s as a video', (type) => {
    expect(videoTypeOf(type)).not.toBeNull()
    expect(kindOf({ type })).toBe('Video')
  })

  it.each(['video/x-msvideo', 'video/quicktime', 'video/ogg', 'video/x-matroska', 'audio/mpeg', 'image/png', ''])('does not play %j as a video', (type) => {
    expect(videoTypeOf(type)).toBeNull()
  })

  it.each(['application/pdf', 'APPLICATION/PDF', 'application/pdf; version=1.7'])('draws %s page by page as a PDF', (type) => {
    expect(pdfTypeOf(type)).toBe('application/pdf')
    expect(kindOf({ type })).toBe('Pdf')
  })

  it.each(['application/x-pdf', 'application/pdfx', 'text/pdf', 'application/zip', 'image/png', ''])('does not draw %j as a PDF', (type) => {
    expect(pdfTypeOf(type)).toBeNull()
  })

  it.each(['text/plain', 'application/zip', 'text/html', 'image/svg+xml', 'application/x-msdownload', ''])(
    'offers %j as a file to save',
    (type) => {
      expect(kindOf({ type })).toBe('File')
    },
  )
})

describe('drawing a picture in the message', () => {
  it('draws a picture up to the largest size, and a file to save for one byte more', () => {
    expect(isInlinePicture({ type: 'image/png', size: INLINE_PICTURE_MAX_BYTES })).toBe(true)
    expect(isInlinePicture({ type: 'image/png', size: INLINE_PICTURE_MAX_BYTES + 1 })).toBe(false)
  })

  it.each(['video/mp4', 'application/pdf', 'image/svg+xml', ''])('never draws a file that says it is %s', (type) => {
    expect(isInlinePicture({ type, size: 100 })).toBe(false)
  })
})
