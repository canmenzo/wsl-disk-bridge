# WSL Disk Bridge

**Access your Linux partition from Windows — live, read-only, no drivers, no reboot.**

Dual-boot setups shouldn't mean your two operating systems can't talk to each other. If you're running Windows + Linux side by side, your Linux filesystem should be accessible right now — not after a reboot and a USB transfer.

`wsl --mount` is supposed to handle this. Except when your Linux drive has a boot partition, Windows locks the entire disk and tells you to go away. No amount of offlining, handle-closing, or prayer will fix it. This tool does.

## The Problem

If you dual-boot Windows + Linux (Arch, CachyOS, Fedora, Ubuntu, etc.) and your Linux drive has an EFI System Partition (spoiler: it does), Windows Boot Manager holds a lock on the entire physical disk. `wsl --mount` can't attach it — you get the dreaded:

```
Failed to attach disk '\\.\PhysicalDrive1' to WSL2:
The disk is in use or locked by another process.
Error code: Wsl/Service/AttachDisk/MountDisk/0x8007006c
```

No existing tool solves this cleanly:
- **WinBtrfs** — kernel driver, blocked by Secure Boot
- **DiskInternals Linux Reader** — GUI-only, no CLI, can't integrate into workflows
- **Paragon LinuxFS** — ext only, no btrfs/xfs/f2fs support
- **`wsl --mount`** — broken when the disk has boot entries

## The Solution

WSL Disk Bridge bypasses the lock entirely. Instead of asking Windows to hand the disk to WSL2 (which it refuses), it:

1. **Open the raw disk for reading** from an elevated Windows process (this works — the lock only blocks exclusive access, not shared reads)
2. **Serve the partition over NBD** (Network Block Device) protocol on localhost
3. **Connect from WSL2** using `nbd-client`, giving you a real `/dev/nbd0` block device
4. **Mount it** with the Linux kernel's native filesystem drivers

```
Windows (elevated)              WSL2 (Kali/Ubuntu/etc)
┌──────────────────┐            ┌──────────────────┐
│  nbd_server.exe  │◄──TCP────►│   nbd-client      │
│  reads raw disk  │  10809    │   /dev/nbd0       │
│  via Win32 API   │            │   mount -t xfs    │
└──────────────────┘            └──────────────────┘
```

### Bonus: XFS Feature Patching

Running a bleeding-edge distro like CachyOS or Fedora Rawhide? Your XFS filesystem might use features (like parent pointers and exchange-range) that need kernel 6.10+, but WSL2 ships kernel 6.6.

WSL Disk Bridge patches the XFS superblock **in-flight** — modifying the `features_incompat` field and recalculating the CRC32C checksum on the fly. Your actual disk is never modified. The WSL2 kernel sees a compatible filesystem and mounts it happily.

## Quick Start

### Prerequisites
- Windows 10/11 with WSL2 enabled
- A WSL2 distro installed (Kali, Ubuntu, etc.)
- .NET Framework 4.x (comes with Windows)
- `nbd-client` and filesystem tools in your WSL distro

### 1. Compile the server

```powershell
# Find your .NET compiler
$csc = (Get-ChildItem "C:\Windows\Microsoft.NET\Framework64" -Recurse -Filter "csc.exe" | Sort-Object FullName -Descending | Select-Object -First 1).FullName

# Compile
& $csc /out:nbd_server.exe /platform:x64 /optimize+ nbd_server.cs
```

### 2. Find your partition

```powershell
# List disks
Get-Disk | Format-Table Number, FriendlyName, Size

# List partitions on your Linux disk (e.g., Disk 1)
Get-Partition -DiskNumber 1 | Format-Table PartitionNumber, Offset, Size, Type
```

Update the constants in `nbd_server.cs`:
```csharp
const string DISK = @"\\.\PhysicalDrive1";      // your disk
const long PART_OFFSET = 2148532224L;             // partition byte offset
const long PART_SIZE   = 1998250384896L;          // partition size in bytes
```

### 3. Run the server (as Administrator)

```powershell
# Must run elevated — raw disk access requires admin
Start-Process .\nbd_server.exe -Verb RunAs
```

### 4. Connect from WSL2

```bash
# Install tools (Debian/Ubuntu/Kali)
sudo apt install nbd-client xfsprogs btrfs-progs

# Find the Windows host IP
ip route show default | awk '{print $3}'
# Or check: cat /etc/resolv.conf | grep nameserver

# Connect to the NBD server
sudo nbd-client <WINDOWS_IP> 10809 /dev/nbd0 -N '' -b 512

# Mount your filesystem
sudo mkdir -p /mnt/linux
sudo mount -t xfs -o ro,norecovery,nouuid /dev/nbd0 /mnt/linux
# or for btrfs:
sudo mount -t btrfs -o ro /dev/nbd0 /mnt/linux
# or for ext4:
sudo mount -t ext4 -o ro /dev/nbd0 /mnt/linux

# Access your files!
ls /mnt/linux/home/
```

### 5. Clean up

```bash
# In WSL
sudo umount /mnt/linux
sudo nbd-client -d /dev/nbd0
```

Then close the server window on Windows.

## How the XFS Patching Works

Modern XFS filesystems (especially from rolling-release distros) can have features that older kernels don't understand. The server intercepts reads that cover XFS AG superblocks and:

1. Detects `features_incompat` bits that WSL2's kernel doesn't support (0x40 EXCHRANGE, 0x80 PARENT)
2. Clears those bits in the data sent to WSL2 (the actual disk is never touched)
3. Recalculates the CRC32C checksum so the kernel's validation passes

This is safe for **read-only** access — the features being masked are about metadata bookkeeping (parent pointers, file exchange operations) that don't affect reading file data.

> **Note:** If you get `xfs_attr_shortform_verify` corruption errors when accessing files, it means the kernel is encountering parent pointer extended attributes it doesn't understand. In this case, use `xfs_db` to browse files directly, or wait for WSL2 to ship a 6.10+ kernel.

## Supported Filesystems

| Filesystem | Status | Notes |
|------------|--------|-------|
| XFS | Works | With automatic feature patching for newer XFS features |
| ext4 | Works | No patching needed |
| btrfs | Works | No patching needed |
| f2fs | Should work | Untested — try `mount -t f2fs` |
| ZFS | Not supported | ZFS requires its own kernel module |

## Why Not Just...

| Alternative | Why it doesn't work |
|------------|-------------------|
| `wsl --mount` | Disk locked by Windows Boot Manager when EFI partition exists |
| Disable Secure Boot + WinBtrfs | Security risk, only works for btrfs |
| DiskInternals Linux Reader | GUI-only, no CLI/scripting, proprietary |
| Reboot into Linux | Breaks your workflow; no live cross-OS access |
| Copy to USB first | Requires rebooting into Linux anyway; no automation |

## Technical Details

- The server uses Win32 `CreateFile` + `ReadFile` with `FILE_FLAG_NO_BUFFERING` for direct disk I/O
- NBD protocol: newstyle negotiation with `NBD_OPT_EXPORT_NAME` and `NBD_OPT_GO` support
- Read-only: the server never writes to the disk
- CRC32C: hardware-independent lookup table implementation for XFS superblock checksum recalculation
- All AG superblocks are patched (not just AG 0) to prevent validation failures across the filesystem

## Origin Story

I dual-boot Windows and Linux, and I wanted real cross-OS filesystem access without rebooting or messing with kernel drivers. What should have been a 30-second `wsl --mount` turned into a deep dive through Windows storage subsystem locks, NBD protocol specs, XFS on-disk format documentation, and CRC32C implementations.

Built with [Claude Code](https://claude.ai/code) in a single session — from "just mount my drive" to writing a custom block device server with on-the-fly filesystem patching.

## License

MIT — do whatever you want with it.

## Contributing

Found a bug? Want to add write support? Have a filesystem that needs patching? Open an issue or PR!
