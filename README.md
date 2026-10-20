# Owk Copy

Fast, parallel file copy and move for Windows that never stalls waiting for you.

Built after Explorer froze at 10% moving 118k files because of two broken npm
workspace junctions in `node_modules`. Owk Copy skips links, decides conflicts
up front, retries errors and keeps going.

## Features

- **Parallel small-file copy** (1–32 threads) plus a dedicated unbuffered lane for files ≥ 64 MB
- **No mid-job prompts**: choose *replace if newer*, *always replace*, *skip* or *keep both* before starting
- **Skips symlinks / junctions** (broken ones are what hang Explorer)
- **Retries** each failed file 3×, logs it, continues; **Retry failed** re-runs only those
- **Instant same-drive moves** (rename instead of copy)
- Long paths, read-only files, pause / resume / cancel, optional byte-for-byte verify, exclude patterns (`node_modules;.git;*.tmp`)
- **Default Ctrl+V handler**: a tray agent catches Ctrl+V in Explorer and on the desktop when the clipboard holds files. Text paste (rename box, address bar) is untouched; locations it can't handle (zip folders, This PC) fall back to Explorer.
- **"Owk Paste here"** right-click entry (Windows 11: under *Show more options*)
- Compact window, Windows 11 Fluent theme, light/dark

## Install

1. Install the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (x64).
2. Download `OwkCopy-win-x64.zip` from [Releases](../../releases), extract anywhere.
3. Run `OwkCopy.exe`. Under **Options** enable *Default copy handler* and/or the right-click menu.

## Command line

```
OwkCopy.exe                              open the window
OwkCopy.exe copy|move <src...> <dst>     run a job immediately
OwkCopy.exe paste "<folder>"             paste Explorer clipboard (copy or cut)
OwkCopy.exe --default-on | --default-off Ctrl+V takeover (tray agent, starts at login)
OwkCopy.exe --register | --unregister    right-click menu entries
```

Everything is per-user (HKCU); no admin rights needed.

## Limitations

Left-button drag & drop and Explorer's own context-menu *Paste* still use the
Windows copy engine. Intercepting those requires a native shell extension DLL
loaded into Explorer, which Owk Copy deliberately avoids.

## Build

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

Requires the .NET 9 SDK.

## License

MIT
