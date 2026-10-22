import { File as FileIcon, FileArchive, FileText, Film, Image } from 'lucide-react'

// The icon for a file that is shown as a chip, chosen by what kind of file it is.
export function AttachmentIcon({ contentType, className }: { contentType: string; className?: string }) {
  if (contentType.startsWith('image/')) return <Image className={className} aria-hidden="true" />
  if (contentType.startsWith('video/')) return <Film className={className} aria-hidden="true" />
  if (contentType === 'application/zip') return <FileArchive className={className} aria-hidden="true" />
  if (contentType === 'application/pdf' || contentType.startsWith('text/')) {
    return <FileText className={className} aria-hidden="true" />
  }

  return <FileIcon className={className} aria-hidden="true" />
}
