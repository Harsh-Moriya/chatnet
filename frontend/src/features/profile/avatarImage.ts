// Gets a picture ready for upload in the browser, so what is stored, served and kept in caches is a few tens of
// kilobytes and not a photo straight from a camera. The server still checks the type and size of whatever arrives: this
// is for efficiency, not for safety, and a client can skip it.

// The side of the square a picture is shrunk to. Larger than the biggest place it is shown (80 px), for screens with
// twice the pixels.
export const AVATAR_SIZE = 256

// The file could not be read as a picture by this browser (not an image, or damaged).
export class PictureUnreadable extends Error {
  constructor() {
    super('That file is not a picture this browser can read.')
  }
}

// The largest square in the middle of an image: the part of it that is kept.
export function coverSquare(width: number, height: number): { x: number; y: number; side: number } {
  const side = Math.min(width, height)
  return { x: Math.floor((width - side) / 2), y: Math.floor((height - side) / 2), side }
}

// A small picture is kept at its own size and not stretched.
export function outputSize(side: number): number {
  return Math.min(AVATAR_SIZE, side)
}

// WebP where the browser can write it, JPEG where it cannot (a browser that does not know a type writes PNG instead).
const TYPES = [
  { type: 'image/webp', quality: 0.85 },
  { type: 'image/jpeg', quality: 0.9 },
] as const

export async function prepareAvatar(file: File): Promise<Blob> {
  let bitmap: ImageBitmap
  try {
    bitmap = await createImageBitmap(file)
  } catch {
    throw new PictureUnreadable()
  }

  try {
    const { x, y, side } = coverSquare(bitmap.width, bitmap.height)
    const size = outputSize(side)

    const canvas = document.createElement('canvas')
    canvas.width = size
    canvas.height = size

    const context = canvas.getContext('2d')
    if (!context) throw new PictureUnreadable()
    context.drawImage(bitmap, x, y, side, side, 0, 0, size, size)

    for (const { type, quality } of TYPES) {
      const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, type, quality))
      if (blob && blob.type === type) return blob
    }

    throw new PictureUnreadable()
  } finally {
    bitmap.close()
  }
}
