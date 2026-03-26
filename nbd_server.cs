// WSL Disk Bridge - NBD Server for accessing Linux partitions from Windows
// https://github.com/canmenzo/wsl-disk-bridge
//
// Serves a raw disk partition over NBD protocol so WSL2 can mount it.
// Includes on-the-fly XFS feature patching for newer filesystems.
//
// Compile: csc /out:nbd_server.exe /platform:x64 /optimize+ nbd_server.cs
// Run:     Start-Process .\nbd_server.exe -Verb RunAs

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

class NbdServer {

    // =====================================================================
    // CONFIGURATION - Update these for your system
    // =====================================================================

    // Physical disk path (use Get-Disk in PowerShell to find yours)
    const string DISK = @"\\.\PhysicalDrive1";

    // Partition byte offset and size (use Get-Partition -DiskNumber N to find)
    const long PART_OFFSET = 2148532224L;
    const long PART_SIZE   = 1998250384896L;

    // NBD server port (default NBD port)
    const int PORT = 10809;

    // XFS feature patching: set to true to patch incompatible XFS features
    // Only needed if your XFS uses features newer than WSL2's kernel supports
    static bool patchXfsFeatures = true;

    // =====================================================================
    // Win32 API imports for raw disk access
    // =====================================================================

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string fn, uint access, uint share,
        IntPtr sa, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFilePointerEx(SafeFileHandle h, long dist, out long newp, uint method);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint toRead, out uint read, IntPtr ovl);

    static SafeFileHandle diskHandle;

    // =====================================================================
    // Logging
    // =====================================================================

    static StreamWriter logWriter;
    static void Log(string msg) {
        Console.WriteLine(msg);
        if (logWriter != null) { logWriter.WriteLine(msg); logWriter.Flush(); }
    }

    // =====================================================================
    // Disk I/O
    // =====================================================================

    static byte[] DiskRead(long offset, int length) {
        long absOffset = PART_OFFSET + offset;
        long aligned = (absOffset / 4096) * 4096;
        int prefix = (int)(absOffset - aligned);
        int alignedLen = ((prefix + length + 4095) / 4096) * 4096;

        long newPos;
        if (!SetFilePointerEx(diskHandle, aligned, out newPos, 0)) {
            Log("Seek failed: " + Marshal.GetLastWin32Error());
            return new byte[length];
        }

        byte[] buf = new byte[alignedLen];
        uint bytesRead;
        if (!ReadFile(diskHandle, buf, (uint)alignedLen, out bytesRead, IntPtr.Zero)) {
            Log("Read failed at " + aligned + ": " + Marshal.GetLastWin32Error());
            return new byte[length];
        }

        byte[] result = new byte[length];
        Buffer.BlockCopy(buf, prefix, result, 0, length);

        if (patchXfsFeatures) {
            PatchFeaturesIncompat(result, offset, length);
        }

        return result;
    }

    // =====================================================================
    // XFS Feature Patching
    // =====================================================================
    //
    // Modern XFS features like EXCHRANGE (0x40) and PARENT (0x80) require
    // kernel 6.10+, but WSL2 ships kernel 6.6. We patch the
    // features_incompat field in all AG superblocks on-the-fly and
    // recalculate the CRC32C checksum. The actual disk is never modified.
    //
    // XFS on-disk layout references:
    //   - sb_features_incompat: offset 216, big-endian uint32
    //   - sb_crc:               offset 224, little-endian uint32
    //   - sb_sectsize:          offset 102, big-endian uint16

    const long SB_FEAT_INCOMPAT_OFF = 216;
    const uint FEAT_MASK = 0xFFFFFF3F;  // clears bits 6 (EXCHRANGE) and 7 (PARENT)
    const int SB_CRC_OFF = 224;
    const int SB_SECTSIZE_OFF = 102;

    static long agsize = 0;
    static uint totalAGs = 16;

    static void PatchFeaturesIncompat(byte[] data, long readOffset, int readLen) {
        PatchAtSbOffset(data, readOffset, readLen, 0);
        if (agsize > 0) {
            for (uint ag = 1; ag < totalAGs; ag++) {
                PatchAtSbOffset(data, readOffset, readLen, ag * agsize);
            }
        }
    }

    static void PatchAtSbOffset(byte[] data, long readOffset, int readLen, long sbStart) {
        long featOff = sbStart + SB_FEAT_INCOMPAT_OFF;
        if (readOffset <= featOff && readOffset + readLen >= featOff + 4) {
            int idx = (int)(featOff - readOffset);
            uint val = (uint)(data[idx] << 24 | data[idx+1] << 16 | data[idx+2] << 8 | data[idx+3]);
            if ((val & 0xC0) != 0) {
                uint newVal = val & FEAT_MASK;
                data[idx]   = (byte)(newVal >> 24);
                data[idx+1] = (byte)(newVal >> 16);
                data[idx+2] = (byte)(newVal >> 8);
                data[idx+3] = (byte)(newVal);
                Log("  [PATCH] features_incompat at AG offset " + sbStart + ": 0x" + val.ToString("x") + " -> 0x" + newVal.ToString("x"));

                // Recalculate CRC32C
                int sbIdx = (int)(sbStart - readOffset);
                long crcOff = sbStart + SB_CRC_OFF;
                long ssOff = sbStart + SB_SECTSIZE_OFF;
                if (sbIdx >= 0 && readOffset + readLen >= crcOff + 4 && readOffset + readLen >= ssOff + 2) {
                    int ssIdx = (int)(ssOff - readOffset);
                    int sectsize = (data[ssIdx] << 8) | data[ssIdx + 1];
                    if (sectsize == 0) sectsize = 512;
                    if (sbIdx + sectsize <= readLen) {
                        int crcIdx = (int)(crcOff - readOffset);
                        data[crcIdx] = 0; data[crcIdx+1] = 0; data[crcIdx+2] = 0; data[crcIdx+3] = 0;
                        uint newCrc = Crc32c(data, sbIdx, sectsize);
                        data[crcIdx]   = (byte)(newCrc);
                        data[crcIdx+1] = (byte)(newCrc >> 8);
                        data[crcIdx+2] = (byte)(newCrc >> 16);
                        data[crcIdx+3] = (byte)(newCrc >> 24);
                        Log("  [CRC]   recalculated: 0x" + newCrc.ToString("x8"));
                    }
                }
            }
        }
    }

    // =====================================================================
    // CRC32C (Castagnoli) - used by XFS for metadata checksums
    // =====================================================================

    static uint[] crc32cTable;

    static void InitCrc32c() {
        crc32cTable = new uint[256];
        for (uint i = 0; i < 256; i++) {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78 : crc >> 1;
            crc32cTable[i] = crc;
        }
    }

    static uint Crc32c(byte[] data, int offset, int length) {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < length; i++)
            crc = (crc >> 8) ^ crc32cTable[(crc ^ data[offset + i]) & 0xFF];
        return crc ^ 0xFFFFFFFF;
    }

    // =====================================================================
    // NBD Protocol helpers
    // =====================================================================

    static void WriteBytes(NetworkStream s, byte[] d) { s.Write(d, 0, d.Length); }

    static byte[] ReadBytes(NetworkStream s, int n) {
        byte[] buf = new byte[n];
        int total = 0;
        while (total < n) {
            int r = s.Read(buf, total, n - total);
            if (r == 0) throw new IOException("Client disconnected");
            total += r;
        }
        return buf;
    }

    static byte[] BE64(ulong v) { byte[] b = BitConverter.GetBytes(v); if (BitConverter.IsLittleEndian) Array.Reverse(b); return b; }
    static byte[] BE32(uint v)  { byte[] b = BitConverter.GetBytes(v); if (BitConverter.IsLittleEndian) Array.Reverse(b); return b; }
    static byte[] BE16(ushort v){ byte[] b = BitConverter.GetBytes(v); if (BitConverter.IsLittleEndian) Array.Reverse(b); return b; }

    static ulong  ReadBE64(byte[] b, int o) { byte[] t = new byte[8]; Buffer.BlockCopy(b, o, t, 0, 8); if (BitConverter.IsLittleEndian) Array.Reverse(t); return BitConverter.ToUInt64(t, 0); }
    static uint   ReadBE32(byte[] b, int o) { byte[] t = new byte[4]; Buffer.BlockCopy(b, o, t, 0, 4); if (BitConverter.IsLittleEndian) Array.Reverse(t); return BitConverter.ToUInt32(t, 0); }
    static ushort ReadBE16(byte[] b, int o) { byte[] t = new byte[2]; Buffer.BlockCopy(b, o, t, 0, 2); if (BitConverter.IsLittleEndian) Array.Reverse(t); return BitConverter.ToUInt16(t, 0); }

    // =====================================================================
    // Main
    // =====================================================================

    static void Main() {
        try { Run(); }
        catch (Exception ex) {
            Console.Error.WriteLine("FATAL: " + ex);
            try { File.WriteAllText("nbd_crash.txt", ex.ToString()); } catch {}
        }
    }

    static void Run() {
        string logPath = Path.Combine(Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location), "nbd_server.log");
        logWriter = new StreamWriter(logPath, false);
        InitCrc32c();

        Log("WSL Disk Bridge - NBD Server");
        Log("  Disk:      " + DISK);
        Log("  Partition: offset=" + PART_OFFSET + " size=" + PART_SIZE + " (" + (PART_SIZE / 1073741824) + " GB)");
        Log("  XFS patch: " + (patchXfsFeatures ? "enabled" : "disabled"));
        Log("");

        // Open disk for reading
        diskHandle = CreateFile(DISK, 0x80000000, 3, IntPtr.Zero, 3, 0x20000000, IntPtr.Zero);
        if (diskHandle.IsInvalid) {
            Log("ERROR: Failed to open " + DISK + " (error " + Marshal.GetLastWin32Error() + ")");
            Log("Make sure you're running as Administrator!");
            Console.ReadKey();
            return;
        }
        Log("Disk opened successfully.");

        // Detect filesystem and read XFS metadata if applicable
        byte[] sb = DiskRead(0, 512);
        string fsMagic = Encoding.ASCII.GetString(sb, 0, 4);
        if (fsMagic == "XFSB") {
            Log("Filesystem: XFS");
            uint bs = ReadBE32(sb, 4);
            uint agb = ReadBE32(sb, 84);
            agsize = (long)agb * (long)bs;
            totalAGs = ReadBE32(sb, 88);
            uint feat = ReadBE32(sb, 216);
            Log("  Block size: " + bs + "  AG count: " + totalAGs + "  AG size: " + agsize);
            Log("  features_incompat: 0x" + feat.ToString("x"));
            if (patchXfsFeatures && (feat & 0xC0) != 0) {
                Log("  Will patch features 0x" + (feat & 0xC0).ToString("x") + " on the fly");
            }
        } else if (sb[0x438] == 0x53 && sb[0x439] == 0xEF) {
            Log("Filesystem: ext2/ext3/ext4");
            patchXfsFeatures = false;
        } else {
            // Check for btrfs magic at offset 64 of superblock at +65536
            byte[] bsb = DiskRead(65536, 72);
            string bm = Encoding.ASCII.GetString(bsb, 64, 8);
            if (bm == "_BHRfS_M") {
                Log("Filesystem: btrfs");
            } else {
                Log("Filesystem: unknown (magic: " + fsMagic + ")");
            }
            patchXfsFeatures = false;
        }
        Log("");

        // Start NBD server
        TcpListener listener = new TcpListener(IPAddress.Any, PORT);
        listener.Start();
        Log("NBD server listening on port " + PORT);
        Log("Connect from WSL2 with:");
        Log("  sudo nbd-client <WINDOWS_IP> " + PORT + " /dev/nbd0 -N '' -b 512");
        Log("");

        while (true) {
            Log("Waiting for connection...");
            TcpClient client = listener.AcceptTcpClient();
            Log("Client connected: " + client.Client.RemoteEndPoint);

            try {
                HandleClient(client);
            } catch (Exception ex) {
                Log("Session error: " + ex.Message);
            } finally {
                client.Close();
            }
        }
    }

    static void HandleClient(TcpClient client) {
        NetworkStream ns = client.GetStream();

        // Newstyle NBD negotiation
        WriteBytes(ns, BE64(0x4e42444d41474943)); // NBDMAGIC
        WriteBytes(ns, BE64(0x49484156454F5054)); // IHAVEOPT
        WriteBytes(ns, BE16(3));                   // FIXED_NEWSTYLE | NO_ZEROES

        byte[] cfBuf = ReadBytes(ns, 4);
        uint clientFlags = ReadBE32(cfBuf, 0);
        bool noZeroes = (clientFlags & 2) != 0;

        // Option haggling
        while (true) {
            byte[] hdr = ReadBytes(ns, 16);
            uint optId = ReadBE32(hdr, 8);
            uint optLen = ReadBE32(hdr, 12);
            if (optLen > 0) ReadBytes(ns, (int)optLen);

            if (optId == 1) { // NBD_OPT_EXPORT_NAME
                WriteBytes(ns, BE64((ulong)PART_SIZE));
                WriteBytes(ns, BE16(3)); // HAS_FLAGS | READ_ONLY
                if (!noZeroes) WriteBytes(ns, new byte[124]);
                Log("Negotiation complete (EXPORT_NAME).");
                break;
            } else if (optId == 7) { // NBD_OPT_GO
                byte[] info = new byte[12];
                Buffer.BlockCopy(BE16(0), 0, info, 0, 2);
                Buffer.BlockCopy(BE64((ulong)PART_SIZE), 0, info, 2, 8);
                Buffer.BlockCopy(BE16(3), 0, info, 10, 2);

                WriteBytes(ns, BE64(0x3e889045565a9)); WriteBytes(ns, BE32(optId));
                WriteBytes(ns, BE32(3)); WriteBytes(ns, BE32((uint)info.Length)); WriteBytes(ns, info);

                WriteBytes(ns, BE64(0x3e889045565a9)); WriteBytes(ns, BE32(optId));
                WriteBytes(ns, BE32(1)); WriteBytes(ns, BE32(0)); // ACK
                Log("Negotiation complete (GO).");
                break;
            } else {
                WriteBytes(ns, BE64(0x3e889045565a9)); WriteBytes(ns, BE32(optId));
                WriteBytes(ns, BE32(0x80000001)); WriteBytes(ns, BE32(0));
            }
        }

        // Transmission phase
        long reads = 0, bytes = 0;
        while (true) {
            byte[] req = ReadBytes(ns, 28);
            if (ReadBE32(req, 0) != 0x25609513) { Log("Bad request magic"); break; }

            ushort cmd = ReadBE16(req, 6);
            ulong handle = ReadBE64(req, 8);
            ulong offset = ReadBE64(req, 16);
            uint length = ReadBE32(req, 24);

            if (cmd == 0) { // READ
                reads++; bytes += length;
                if (reads % 10000 == 0)
                    Log("  Progress: " + reads + " reads, " + (bytes / 1048576) + " MB served");

                byte[] data = DiskRead((long)offset, (int)length);
                WriteBytes(ns, BE32(0x67446698)); // REPLY_MAGIC
                WriteBytes(ns, BE32(0));           // OK
                WriteBytes(ns, BE64(handle));
                WriteBytes(ns, data);
            } else if (cmd == 2) { // DISCONNECT
                Log("Client disconnected gracefully.");
                break;
            } else {
                WriteBytes(ns, BE32(0x67446698));
                WriteBytes(ns, BE32(22)); // EINVAL
                WriteBytes(ns, BE64(handle));
            }
        }

        Log("Session ended: " + reads + " reads, " + (bytes / 1048576) + " MB total.");
    }
}
