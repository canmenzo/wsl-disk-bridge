#!/bin/bash
# WSL Disk Bridge - Custom WSL2 Kernel Builder
# https://github.com/canmenzo/wsl-disk-bridge
#
# Builds a WSL2 kernel with full XFS parent pointer support.
# The stock WSL2 kernel (6.6.x) can't read XFS filesystems that use
# parent pointers (common on CachyOS, Fedora 40+, etc.), causing
# "Structure needs cleaning" errors on directory access.
#
# This script downloads Linux 6.12 LTS and builds it with Microsoft's
# WSL2 config, giving you native support for modern XFS features.
#
# Usage:
#   Run inside WSL2 as root:
#     sudo ./build-wsl-kernel.sh
#
#   Then from Windows (PowerShell):
#     wsl --shutdown
#     # WSL will use the new kernel on next start

set -euo pipefail

# --- Configuration ---
KERNEL_VERSION="${KERNEL_VERSION:-6.12.78}"
KERNEL_MAJOR="${KERNEL_VERSION%%.*}"
BUILD_DIR="/usr/src"
JOBS="$(nproc)"
WIN_USER="${WIN_USER:-$(ls /mnt/c/Users/ | grep -v -E 'Public|Default|All Users|Default User' | head -1)}"
KERNEL_OUTPUT="/mnt/c/Users/${WIN_USER}/wsl-kernel-${KERNEL_VERSION}"
WSLCONFIG="/mnt/c/Users/${WIN_USER}/.wslconfig"

echo "=== WSL Disk Bridge - Kernel Builder ==="
echo "Kernel version:  ${KERNEL_VERSION}"
echo "Build jobs:      ${JOBS}"
echo "Output:          ${KERNEL_OUTPUT}"
echo "Windows user:    ${WIN_USER}"
echo ""

# --- Check root ---
if [ "$(id -u)" -ne 0 ]; then
    echo "ERROR: Must run as root (sudo ./build-wsl-kernel.sh)"
    exit 1
fi

# --- Install dependencies ---
echo "[1/6] Installing build dependencies..."
if command -v apt-get &>/dev/null; then
    apt-get update -qq
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
        build-essential flex bison libssl-dev libelf-dev bc dwarves \
        python3 cpio curl 2>&1 | tail -1
elif command -v pacman &>/dev/null; then
    pacman -Sy --noconfirm --needed base-devel flex bison openssl libelf bc pahole python cpio curl
elif command -v dnf &>/dev/null; then
    dnf install -y gcc make flex bison openssl-devel elfutils-libelf-devel bc dwarves python3 cpio curl
else
    echo "WARNING: Unknown package manager. Ensure build deps are installed."
fi

# --- Download kernel source ---
TARBALL="${BUILD_DIR}/linux-${KERNEL_VERSION}.tar.xz"
SOURCE="${BUILD_DIR}/linux-${KERNEL_VERSION}"

if [ -d "${SOURCE}" ]; then
    echo "[2/6] Kernel source already exists at ${SOURCE}, skipping download."
else
    echo "[2/6] Downloading Linux ${KERNEL_VERSION}..."
    curl -L "https://cdn.kernel.org/pub/linux/kernel/v${KERNEL_MAJOR}.x/linux-${KERNEL_VERSION}.tar.xz" \
        -o "${TARBALL}"
    echo "      Extracting..."
    tar xf "${TARBALL}" -C "${BUILD_DIR}"
    rm -f "${TARBALL}"
fi

cd "${SOURCE}"

# --- Get Microsoft WSL2 config ---
echo "[3/6] Downloading Microsoft WSL2 kernel config..."
curl -sL "https://raw.githubusercontent.com/microsoft/WSL2-Linux-Kernel/linux-msft-wsl-6.6.y/arch/x86/configs/config-wsl" \
    -o .config

# Adapt config to the newer kernel version
make olddefconfig 2>&1 | grep -v "^#" || true

# --- Verify critical options ---
echo "[4/6] Verifying kernel config..."

verify_config() {
    local key="$1"
    local expected="$2"
    local actual
    actual=$(grep "^${key}=" .config 2>/dev/null || echo "NOT SET")
    if [ "${actual}" != "${key}=${expected}" ]; then
        echo "  WARNING: ${key} is '${actual}', expected '${expected}'"
        echo "  Fixing..."
        sed -i "s/.*${key}.*/${key}=${expected}/" .config
        make olddefconfig 2>/dev/null
    fi
}

verify_config "CONFIG_XFS_FS" "y"
verify_config "CONFIG_BTRFS_FS" "m"
verify_config "CONFIG_BLK_DEV_NBD" "m"
verify_config "CONFIG_HYPERV" "y"

echo "  CONFIG_XFS_FS=y          (XFS with parent pointer support)"
echo "  CONFIG_BTRFS_FS=m        (btrfs module)"
echo "  CONFIG_BLK_DEV_NBD=m     (NBD block device module)"
echo "  CONFIG_HYPERV=y          (Hyper-V / WSL2 support)"

# --- Build ---
echo "[5/6] Building kernel (${JOBS} jobs)... this may take 5-15 minutes."
make -j"${JOBS}" bzImage modules 2>&1 | tail -5

echo "      Installing modules..."
make modules_install 2>&1 | tail -1

# --- Install ---
echo "[6/6] Installing kernel..."
cp arch/x86/boot/bzImage "${KERNEL_OUTPUT}"

KERNEL_SIZE=$(du -h "${KERNEL_OUTPUT}" | cut -f1)
echo "      Kernel saved to: ${KERNEL_OUTPUT} (${KERNEL_SIZE})"

# Configure .wslconfig
KERNEL_WIN_PATH="C:\\\\Users\\\\${WIN_USER}\\\\wsl-kernel-${KERNEL_VERSION}"
if [ -f "${WSLCONFIG}" ]; then
    if grep -q '^\[wsl2\]' "${WSLCONFIG}"; then
        if grep -q '^kernel=' "${WSLCONFIG}"; then
            sed -i "s|^kernel=.*|kernel=${KERNEL_WIN_PATH}|" "${WSLCONFIG}"
        else
            sed -i "/^\[wsl2\]/a kernel=${KERNEL_WIN_PATH}" "${WSLCONFIG}"
        fi
    else
        echo "" >> "${WSLCONFIG}"
        echo "[wsl2]" >> "${WSLCONFIG}"
        echo "kernel=${KERNEL_WIN_PATH}" >> "${WSLCONFIG}"
    fi
else
    cat > "${WSLCONFIG}" <<WSLEOF
[wsl2]
kernel=${KERNEL_WIN_PATH}
WSLEOF
fi

echo ""
echo "=== Done! ==="
echo ""
echo "To activate the new kernel:"
echo "  1. Open PowerShell (or cmd)"
echo "  2. Run: wsl --shutdown"
echo "  3. Start any WSL distro"
echo "  4. Verify: uname -r  (should show ${KERNEL_VERSION}-microsoft-standard-WSL2)"
echo ""
echo "XFS parent pointers, btrfs, and NBD are all supported."
echo "You can now use wsl-disk-bridge to mount modern XFS filesystems"
echo "without 'Structure needs cleaning' errors."
