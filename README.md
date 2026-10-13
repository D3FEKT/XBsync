# XBsync

<img src="docs/icon.png" width="96" alt="XBsync app icon" />

XBsync is a small Windows app for reaching your Xbox cloud saves: browse them, download them, and put a single file back where it came from. It's written in C# on .NET 10 with a WinUI 3 interface.

It talks to Xbox's connected-storage service directly and reuses the Xbox sign-in already on the PC where it can, so you usually don't have to type anything in.

## What it does

- Signs in quietly using local Xbox credentials, with a device-code fallback if that isn't enough.
- Shows your profile, avatar, XUID, and played games, with search, filtering, sorting, and list or grid layouts.
- Lists the save containers and files Xbox holds for a title.
- Downloads the files you pick to a folder you choose, with progress, cancel, and safe names when a file already exists.
- Replaces one existing cloud file with a local copy.

## Downloading

1. Open XBsync and let it try to sign in. Click **Sign in** if it needs a hand.
2. Pick a game, then one of its save containers.
3. Choose the files you want and click **Download Selected**. **Open Download Folder** jumps to the last folder you used.

## Replacing a save

Close the game first, then:

1. Select exactly one existing file in the container.
2. Click **Upload & Replace** and choose a local copy.
3. Confirm the file name it shows you.

The original is backed up before anything is written, under:

    %LOCALAPPDATA%\XBsync\Backups\<operation-id>\

The backup path is shown after a successful replace. Backups are never pruned automatically, so clean them up yourself when you're done. XBsync doesn't touch any other file in the container.

## Things worth knowing

Xbox can refuse writes while you're browsing or downloading, and a failed write never reports success. If a transfer is cancelled after the commit step starts, the result on Xbox's side is uncertain, so refresh before retrying.

Replacing a file needs a strong ETag on the current version. When Xbox doesn't hand one over, the replace is blocked but reading still works.

Your session isn't renewed in the background, so you'll sign in again once it expires.

## Limitations

Not every save can be reached. Some titles appear to protect their save data with extra checks, and Xbox will refuse to hand it over or accept a replacement even though the account and container look fine. When that happens there is nothing XBsync can do about it, so those saves stay read-only or simply aren't accessible.

Titles that have never synced to the cloud, or that only keep saves on the console, won't show up at all.

## Credits

XBsync is created and maintained by **D3FEKT** - https://github.com/D3FEKT

Authentication is powered by **XboxAuthNet** by AlphaBs - https://github.com/AlphaBs/XboxAuthNet

## Support

[<img src="docs/kofi.png" width="180" alt="Support D3FEKT on Ko-fi" />](https://ko-fi.com/d3fekt)

XBsync is free and open source. If it has been useful to you, you can buy D3FEKT a coffee on Ko-fi: https://ko-fi.com/d3fekt

## Building

You'll need Windows and the .NET 10 SDK:

    dotnet build XBsync.slnx -c Debug -p:Platform=x64 -m:1 -p:BuildInParallel=false

The executable ends up at `XBsync/bin/Debug/net10.0-windows10.0.26100.0/win-x64/XBsync.exe`.

## Status

Signing in, browsing, and downloading are verified against live Xbox services. Replacement is implemented and covered by synthetic tests, but it has not been run against a real cloud save yet, so treat the first real replace as untested. The connected-storage protocol it uses is reverse-engineered from local reference code and could change without warning.

## License

XBsync is released under the GNU General Public License v3.0. See [LICENSE](LICENSE) for the full text.

