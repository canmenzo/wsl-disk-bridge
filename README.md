# 🌉 WSL Disk Bridge

[![license](https://img.shields.io/github/license/canmenzo/wsl-disk-bridge)](LICENSE)
![platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20%2B%20WSL2-0078D6?logo=windows&logoColor=white)
![C#](https://img.shields.io/badge/C%23-.NET%20Framework%204.x-512BD4?logo=dotnet&logoColor=white)

Read your Linux partition from Windows on a dual-boot machine, live and read-only, without drivers or a reboot. For setups where `wsl --mount` fails because the Linux disk is locked by Windows Boot Manager.

```
Failed to attach disk '\\.\PhysicalDrive1' to WSL2:
The disk is in use or locked by another process.
Error code: Wsl/Service/AttachDisk/MountDisk/0x8007006c
```

A small elevated Windows process opens the raw disk with shared read access (which the lock allows), serves one partition over NBD on port 10809, and WSL2 mounts it with `nbd-client` and the kernel's own filesystem drivers.

```
Windows (elevated)              WSL2 (Kali/Ubuntu/etc)
┌──────────────────┐            ┌──────────────────┐
│  nbd_server.exe  │◄──TCP────►│   nbd-client      │
│  reads raw disk  │  10809    │   /dev/nbd0       │
│  via Win32 API   │            │   mount -t xfs    │
└──────────────────┘            └──────────────────┘
```

### ✨ Features
- 🔒 Read-only: the server never writes to the disk
- 🗂️ Mounts XFS, ext4 and btrfs (f2fs untested, ZFS not supported)
- 🔎 Detects the filesystem on startup and logs XFS geometry and feature flags
- 🩹 Optional on-the-fly XFS superblock patching (clears EXCHRANGE/PARENT bits, recomputes CRC32C) so the stock WSL2 kernel can mount newer XFS
- 🐧 `build-wsl-kernel.sh` builds a Linux 6.12 LTS kernel for WSL2 with full XFS parent pointer support and wires it into `.wslconfig`
- 📦 Single C# file, compiles with the `csc.exe` that ships with Windows

### 🚀 Quick start

**Requirements:** Windows 10/11 with WSL2 and a distro installed, .NET Framework 4.x (built into Windows), admin rights.

**1. Custom kernel (only for XFS with parent pointers).** CachyOS, Fedora 40+ and recent Arch `mkfs.xfs` enable parent pointers, which the stock 6.6 WSL2 kernel can't read (`Structure needs cleaning` on every directory). Skip this for ext4 or btrfs.

```bash
# in WSL2
sudo ./build-wsl-kernel.sh      # optional: KERNEL_VERSION=6.12.78 WIN_USER=<you>
```
```powershell
wsl --shutdown                  # next launch uses the new kernel; check with: uname -r
```

**2. Find your partition** (PowerShell):

```powershell
Get-Disk | Format-Table Number, FriendlyName, Size
Get-Partition -DiskNumber 1 | Format-Table PartitionNumber, Offset, Size, Type
```

Set the constants at the top of `nbd_server.cs`:

```csharp
const string DISK = @"\\.\PhysicalDrive1";      // your disk
const long PART_OFFSET = 2148532224L;             // partition byte offset
const long PART_SIZE   = 1998250384896L;          // partition size in bytes
```

**3. Compile and run the server** (PowerShell):

```powershell
$csc = (Get-ChildItem "C:\Windows\Microsoft.NET\Framework64" -Recurse -Filter "csc.exe" | Sort-Object FullName -Descending | Select-Object -First 1).FullName
& $csc /out:nbd_server.exe /platform:x64 /optimize+ nbd_server.cs
Start-Process .\nbd_server.exe -Verb RunAs     # raw disk access needs admin
```

**4. Connect and mount from WSL2:**

```bash
sudo apt install nbd-client xfsprogs btrfs-progs
sudo modprobe nbd
WIN_IP=$(ip route show default | awk '{print $3}')
sudo nbd-client -N export $WIN_IP 10809 /dev/nbd0 -b 512

sudo mkdir -p /mnt/linux
sudo mount -t xfs -o ro,norecovery,nouuid /dev/nbd0 /mnt/linux
# btrfs: sudo mount -t btrfs -o ro /dev/nbd0 /mnt/linux
# ext4:  sudo mount -t ext4  -o ro /dev/nbd0 /mnt/linux
```

**5. Clean up:** `sudo umount /mnt/linux && sudo nbd-client -d /dev/nbd0`, then close the server window.

> ⚠️ The server listens on all interfaces on port 10809 with no authentication, so anyone who can reach that port can read the partition. Keep it blocked in Windows Firewall for anything but the WSL network, and stop the server when you're done.

### ⚙️ Configuration
All settings are constants at the top of `nbd_server.cs`: `DISK`, `PART_OFFSET`, `PART_SIZE`, `PORT` (default 10809) and `patchXfsFeatures` (default `true`, turned off automatically for ext4 and btrfs). The server writes a log to `nbd_server.log` next to the exe.

To go back to the stock WSL2 kernel, remove the `kernel=` line from `C:\Users\<you>\.wslconfig` and run `wsl --shutdown`.

<details>
<summary>🔬 Technical details</summary>

**Why not the alternatives**

| Alternative | Problem |
|---|---|
| `wsl --mount` | Disk locked by Windows Boot Manager when an EFI partition exists |
| WinBtrfs | Kernel driver, blocked by Secure Boot; btrfs only |
| DiskInternals Linux Reader | GUI only, no CLI, proprietary |
| Paragon LinuxFS | ext only |
| Reboot / copy via USB | No live cross-OS access |

**Filesystem support**

| Filesystem | Status | Notes |
|---|---|---|
| XFS | Works | Custom kernel required for parent pointers |
| ext4 | Works | No patching or custom kernel needed |
| btrfs | Works | No patching or custom kernel needed |
| f2fs | Should work | Untested |
| ZFS | Not supported | Needs its own kernel module |

**XFS superblock patching.** Reads that cover any AG superblock have `features_incompat` bits 0x40 (EXCHRANGE) and 0x80 (PARENT) cleared and the CRC32C recomputed, so the stock kernel's validation passes. The disk itself is never touched. This gets the filesystem mounted, but directory access still fails when inodes carry parent pointer xattrs. For those filesystems you need the custom kernel.

**Custom kernel script.** Downloads Linux 6.12 LTS from kernel.org, applies Microsoft's WSL2 config (`linux-msft-wsl-6.6.y`), runs `make olddefconfig`, enforces `XFS_FS=y`, `BTRFS_FS=m`, `BLK_DEV_NBD=m`, `HYPERV=y`, builds `bzImage` and modules, copies the kernel to `C:\Users\<you>\wsl-kernel-<version>` and sets `kernel=` in `.wslconfig`. Supports apt, pacman and dnf for build dependencies.

| Feature | Stock 6.6.x | Custom 6.12.x |
|---|---|---|
| XFS parent pointers | "Structure needs cleaning" | Works |
| XFS exchange-range | Needs superblock patching | Native |
| XFS metadata dir | Not supported | Supported |
| btrfs / ext4 | Works | Works |

**Server internals**
- Win32 `CreateFile` + `ReadFile` with `FILE_FLAG_NO_BUFFERING`, 4 KiB aligned reads
- NBD fixed newstyle negotiation with `NBD_OPT_EXPORT_NAME` and `NBD_OPT_GO`, export flagged read-only
- Table-based CRC32C, all AG superblocks patched (not only AG 0)

</details>

### 🙏 Credits
Built with [Claude Code](https://claude.ai/code). Bug reports and PRs (write support, more filesystems) are welcome.

### 📄 License
MIT
