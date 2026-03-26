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

And even if you get the disk mounted, the stock WSL2 kernel (6.6.x) can't handle modern XFS features like parent pointers — you'll hit `Structure needs cleaning` errors on every directory access.

No existing tool solves this cleanly:
- **WinBtrfs** — kernel driver, blocked by Secure Boot
- **DiskInternals Linux Reader** — GUI-only, no CLI, can't integrate into workflows
- **Paragon LinuxFS** — ext only, no btrfs/xfs/f2fs support
- **`wsl --mount`** — broken when the disk has boot entries

## The Solution

WSL Disk Bridge bypasses the lock entirely. Instead of asking Windows to hand the disk to WSL2 (which it refuses), it:

1. **Opens the raw disk for reading** from an elevated Windows process (this works — the lock only blocks exclusive access, not shared reads)
2. **Serves the partition over NBD** (Network Block Device) protocol on localhost
3. **Connects from WSL2** using `nbd-client`, giving you a real `/dev/nbd0` block device
4. **Mounts it** with the Linux kernel's native filesystem drivers

```
Windows (elevated)              WSL2 (Kali/Ubuntu/etc)
┌──────────────────┐            ┌──────────────────┐
│  nbd_server.exe  │◄──TCP────►│   nbd-client      │
│  reads raw disk  │  10809    │   /dev/nbd0       │
│  via Win32 API   │            │   mount -t xfs    │
└──────────────────┘            └──────────────────┘
```

### Custom Kernel for Modern Filesystems

Running a bleeding-edge distro like CachyOS or Fedora 40+? Your XFS filesystem likely uses **parent pointers** — a feature that requires kernel 6.7+. The stock WSL2 kernel (6.6.x) will mount the filesystem but choke on directory access:

```
ls: cannot access '/mnt/linux/home': Structure needs cleaning
```

The superblock-level patching in the NBD server masks the `features_incompat` bits so the filesystem mounts, but the kernel still can't parse parent pointer extended attributes stored in individual inodes. The real fix is a newer kernel.

This repo includes `build-wsl-kernel.sh` — a one-command script that builds a **Linux 6.12 LTS kernel** configured for WSL2, giving you native support for XFS parent pointers, exchange-range, and all modern filesystem features.

## Quick Start

### Prerequisites
- Windows 10/11 with WSL2 enabled
- A WSL2 distro installed (Kali, Ubuntu, etc.)
- .NET Framework 4.x (comes with Windows)
- `nbd-client` and filesystem tools in your WSL distro

### 1. Build the custom kernel (if you have modern XFS)

If your Linux distro uses XFS with parent pointers (CachyOS, Fedora 40+, Arch with recent mkfs.xfs), you need to build a custom WSL2 kernel first. **Skip this step if you use ext4 or btrfs.**

```bash
# In WSL2 (run as root)
sudo ./build-wsl-kernel.sh
```

Then restart WSL from PowerShell:

```powershell
wsl --shutdown
# Next WSL launch will use the 6.12 kernel
```

Verify:
```bash
uname -r
# Should show: 6.12.78-microsoft-standard-WSL2
```

### 2. Compile the NBD server

```powershell
# Find your .NET compiler
$csc = (Get-ChildItem "C:\Windows\Microsoft.NET\Framework64" -Recurse -Filter "csc.exe" | Sort-Object FullName -Descending | Select-Object -First 1).FullName

# Compile
& $csc /out:nbd_server.exe /platform:x64 /optimize+ nbd_server.cs
```

### 3. Find your partition

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

### 4. Run the server (as Administrator)

```powershell
# Must run elevated — raw disk access requires admin
Start-Process .\nbd_server.exe -Verb RunAs
```

### 5. Connect from WSL2

```bash
# Install tools (Debian/Ubuntu/Kali)
sudo apt install nbd-client xfsprogs btrfs-progs

# Load the NBD kernel module
sudo modprobe nbd

# Find the Windows host IP
WIN_IP=$(ip route show default | awk '{print $3}')

# Connect to the NBD server
sudo nbd-client -N export $WIN_IP 10809 /dev/nbd0 -b 512

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

### 6. Clean up

```bash
# In WSL
sudo umount /mnt/linux
sudo nbd-client -d /dev/nbd0
```

Then close the server window on Windows.

## How the XFS Superblock Patching Works

The NBD server includes an in-flight XFS superblock patcher as a fallback for users who don't want to build a custom kernel. It intercepts reads that cover XFS AG superblocks and:

1. Detects `features_incompat` bits that the stock WSL2 kernel doesn't support (0x40 EXCHRANGE, 0x80 PARENT)
2. Clears those bits in the data sent to WSL2 (the actual disk is never touched)
3. Recalculates the CRC32C checksum so the kernel's validation passes

This gets the filesystem mounted, but **directory access will still fail** if inodes contain parent pointer extended attributes (which they will on any filesystem created with parent pointers enabled). The superblock patching alone is not enough — you need the custom kernel for full access.

## Custom Kernel Details

The `build-wsl-kernel.sh` script:

1. Downloads **Linux 6.12 LTS** from kernel.org
2. Applies **Microsoft's WSL2 kernel config** (from `WSL2-Linux-Kernel` repo)
3. Runs `make olddefconfig` to adapt the 6.6 config to the 6.12 source
4. Verifies critical options: `XFS_FS=y`, `BTRFS_FS=m`, `BLK_DEV_NBD=m`, `HYPERV=y`
5. Builds `bzImage` and installs modules
6. Copies the kernel to Windows and updates `.wslconfig`

The resulting kernel is **17 MB** and fully compatible with WSL2. All existing WSL2 functionality (networking, GPU, Docker, etc.) works unchanged.

### What the custom kernel fixes

| Feature | Stock 6.6.x | Custom 6.12.x |
|---------|-------------|---------------|
| XFS parent pointers | "Structure needs cleaning" | Works |
| XFS exchange-range | Needs superblock patching | Native support |
| XFS metadata dir | Not supported | Supported |
| btrfs | Works | Works |
| ext4 | Works | Works |

### Reverting to the stock kernel

Delete or rename `C:\Users\<you>\.wslconfig` and run `wsl --shutdown`. WSL will use the stock kernel on next start.

## Supported Filesystems

| Filesystem | Status | Notes |
|------------|--------|-------|
| XFS | Works | Custom kernel required for parent pointers |
| ext4 | Works | No patching or custom kernel needed |
| btrfs | Works | No patching or custom kernel needed |
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

The stock WSL2 kernel then threw `Structure needs cleaning` on every directory — XFS parent pointers. So we built a custom 6.12 LTS kernel with full support, and now it all just works.

Built with [Claude Code](https://claude.ai/code).

## License

MIT — do whatever you want with it.

## Contributing

Found a bug? Want to add write support? Have a filesystem that needs patching? Open an issue or PR!
