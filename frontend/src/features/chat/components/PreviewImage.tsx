import { type ImgHTMLAttributes, useCallback } from 'react'

// Draws a picture the person has chosen but that is not on the server yet, straight from the file in memory.
//
// The temporary address that makes this possible has to be released when the picture goes away, or the file
// stays in memory. A ref callback is used instead of state and an effect: React calls the function it returns
// when the element is removed, and calls the callback again if the element is mounted a second time (as it
// is in development), so the address is always made and released as a pair. The callback is memoised so that
// an ordinary re-render does not release and remake the address.
export function PreviewImage({ file, alt, ...rest }: { file: File; alt: string } & ImgHTMLAttributes<HTMLImageElement>) {
  const attach = useCallback(
    (image: HTMLImageElement | null) => {
      if (!image) return

      const address = URL.createObjectURL(file)
      image.src = address
      return () => URL.revokeObjectURL(address)
    },
    [file],
  )

  return <img ref={attach} alt={alt} {...rest} />
}
