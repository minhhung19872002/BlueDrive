/** A file chosen for upload; relativePath keeps the folder structure ("Album/2024/a.jpg") for folder uploads. */
export interface PickedFile {
  file: File
  relativePath: string | null
}

/**
 * Open the native file chooser through a temporary hidden input (`webkitdirectory` for folders).
 * Must be called from a user gesture. Resolves with an empty list when the dialog is cancelled.
 */
export function pickFiles(directory = false): Promise<PickedFile[]> {
  return new Promise((resolve) => {
    const input = document.createElement('input')
    input.type = 'file'
    input.multiple = true
    input.style.display = 'none'
    if (directory) {
      input.setAttribute('webkitdirectory', '')
      input.setAttribute('directory', '')
    }
    let settled = false
    const finish = (files: PickedFile[]) => {
      if (settled) return
      settled = true
      input.remove()
      resolve(files)
    }
    input.addEventListener('change', () => {
      const files = Array.from(input.files ?? []).map((file) => ({
        file,
        relativePath: file.webkitRelativePath ? file.webkitRelativePath : null,
      }))
      finish(files)
    })
    input.addEventListener('cancel', () => finish([]))
    document.body.appendChild(input)
    input.click()
  })
}

function readAllEntries(reader: FileSystemDirectoryReader): Promise<FileSystemEntry[]> {
  return new Promise((resolve, reject) => {
    const all: FileSystemEntry[] = []
    // readEntries returns results in batches (max 100 in Chrome): call until it returns an empty batch.
    const next = () =>
      reader.readEntries((batch) => {
        if (batch.length === 0) resolve(all)
        else {
          all.push(...batch)
          next()
        }
      }, reject)
    next()
  })
}

function entryFile(entry: FileSystemFileEntry): Promise<File> {
  return new Promise((resolve, reject) => entry.file(resolve, reject))
}

async function walk(entry: FileSystemEntry, out: PickedFile[]): Promise<void> {
  if (entry.isFile) {
    const file = await entryFile(entry as FileSystemFileEntry)
    // fullPath is "/name" for a top-level file and "/Folder/sub/name" inside a dropped folder.
    const path = entry.fullPath.replace(/^\/+/, '')
    out.push({ file, relativePath: path.includes('/') ? path : null })
  } else if (entry.isDirectory) {
    const children = await readAllEntries((entry as FileSystemDirectoryEntry).createReader())
    for (const child of children) await walk(child, out)
  }
}

/**
 * Collect files from an OS drag & drop, walking dropped directories (webkitGetAsEntry) and preserving
 * their relative paths. The entries are captured synchronously: DataTransfer is emptied after the event.
 */
export async function collectDroppedFiles(dt: DataTransfer): Promise<PickedFile[]> {
  const entries = Array.from(dt.items ?? [])
    .filter((i) => i.kind === 'file')
    .map((i) => (typeof i.webkitGetAsEntry === 'function' ? i.webkitGetAsEntry() : null))
  const plainFiles = Array.from(dt.files ?? [])
  if (entries.length === 0 || entries.some((e) => e === null)) {
    return plainFiles.map((file) => ({ file, relativePath: null }))
  }
  const out: PickedFile[] = []
  for (const entry of entries) {
    if (entry) await walk(entry, out)
  }
  return out
}

/** True when a drag operation carries files from the operating system. */
export function isOsFileDrag(dt: DataTransfer | null): boolean {
  return !!dt && Array.from(dt.types).includes('Files')
}
