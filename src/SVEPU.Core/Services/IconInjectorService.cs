using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SVEPU.Core.Services;

public sealed class IconInjectorService
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static byte[] CreateIcoFromImageFile(string imagePath)
    {
        var rawBytes = File.ReadAllBytes(imagePath);
        if (imagePath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && IsValidIco(rawBytes))
        {
            return rawBytes;
        }

        return WrapPngBytesAsIco(rawBytes);
    }

    public static byte[] WrapPngBytesAsIco(byte[] pngBytes)
    {
        var (width, height) = ReadPngDimensions(pngBytes);
        byte icoWidth = width >= 256 || width <= 0 ? (byte)0 : (byte)width;
        byte icoHeight = height >= 256 || height <= 0 ? (byte)0 : (byte)height;

        using var ms = new MemoryStream(22 + pngBytes.Length);
        using var bw = new BinaryWriter(ms);

        // ICONDIR (6 bytes)
        bw.Write((ushort)0); // Reserved
        bw.Write((ushort)1); // Type: 1 = ICO
        bw.Write((ushort)1); // Count: 1 image

        // ICONDIRENTRY (16 bytes)
        bw.Write(icoWidth);
        bw.Write(icoHeight);
        bw.Write((byte)0);   // Color palette count
        bw.Write((byte)0);   // Reserved
        bw.Write((ushort)1); // Color planes
        bw.Write((ushort)32);// Bits per pixel
        bw.Write((uint)pngBytes.Length); // Size of image data
        bw.Write((uint)22);  // Offset of image data (6 + 16)

        bw.Write(pngBytes);
        bw.Flush();
        return ms.ToArray();
    }

    public static bool TryInjectIconIntoWindowsExe(string exePath, string iconImagePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            !File.Exists(exePath) ||
            !File.Exists(iconImagePath))
        {
            return false;
        }

        try
        {
            var icoBytes = CreateIcoFromImageFile(iconImagePath);
            if (icoBytes.Length < 22)
            {
                return false;
            }

            byte width = icoBytes[6];
            byte height = icoBytes[7];
            byte colorCount = icoBytes[8];
            ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(10, 2));
            ushort bitCount = BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(12, 2));
            uint bytesInRes = BinaryPrimitives.ReadUInt32LittleEndian(icoBytes.AsSpan(14, 4));
            uint imageOffset = BinaryPrimitives.ReadUInt32LittleEndian(icoBytes.AsSpan(18, 4));

            if (bytesInRes == 0 ||
                bytesInRes > 10 * 1024 * 1024 ||
                (ulong)imageOffset + (ulong)bytesInRes > (ulong)icoBytes.Length)
            {
                return false;
            }

            var imagePayload = icoBytes.AsSpan((int)imageOffset, (int)bytesInRes).ToArray();

            // Build GRPICONDIR (6 + 14 = 20 bytes)
            var grpIconDir = new byte[20];
            BinaryPrimitives.WriteUInt16LittleEndian(grpIconDir.AsSpan(0, 2), 0); // Reserved
            BinaryPrimitives.WriteUInt16LittleEndian(grpIconDir.AsSpan(2, 2), 1); // Type = 1
            BinaryPrimitives.WriteUInt16LittleEndian(grpIconDir.AsSpan(4, 2), 1); // Count = 1
            grpIconDir[6] = width;
            grpIconDir[7] = height;
            grpIconDir[8] = colorCount;
            grpIconDir[9] = 0; // Reserved
            BinaryPrimitives.WriteUInt16LittleEndian(grpIconDir.AsSpan(10, 2), planes == 0 ? (ushort)1 : planes);
            BinaryPrimitives.WriteUInt16LittleEndian(grpIconDir.AsSpan(12, 2), bitCount == 0 ? (ushort)32 : bitCount);
            BinaryPrimitives.WriteUInt32LittleEndian(grpIconDir.AsSpan(14, 4), bytesInRes);
            BinaryPrimitives.WriteUInt16LittleEndian(grpIconDir.AsSpan(18, 2), 1); // Resource ID 1

            IntPtr hUpdate = BeginUpdateResourceW(exePath, false);
            if (hUpdate == IntPtr.Zero)
            {
                return false;
            }

            const int RT_ICON = 3;
            const int RT_GROUP_ICON = 14;
            const ushort LANG_NEUTRAL = 0;

            bool ok = UpdateResourceW(hUpdate, (IntPtr)RT_ICON, (IntPtr)1, LANG_NEUTRAL, imagePayload, (uint)imagePayload.Length);
            // Update both ID 1 and ID 32512 (IDI_APPLICATION used by .NET apphost)
            ok = ok && UpdateResourceW(hUpdate, (IntPtr)RT_GROUP_ICON, (IntPtr)1, LANG_NEUTRAL, grpIconDir, (uint)grpIconDir.Length);
            ok = ok && UpdateResourceW(hUpdate, (IntPtr)RT_GROUP_ICON, (IntPtr)32512, LANG_NEUTRAL, grpIconDir, (uint)grpIconDir.Length);

            return EndUpdateResourceW(hUpdate, !ok) && ok;
        }
        catch
        {
            return false;
        }
    }

    private static (int Width, int Height) ReadPngDimensions(byte[] bytes)
    {
        if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(PngSignature))
        {
            int width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            int height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            return (width, height);
        }
        return (64, 64);
    }

    private static bool IsValidIco(byte[] bytes)
    {
        if (bytes.Length < 22)
        {
            return false;
        }
        ushort reserved = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2));
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2));
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        return reserved == 0 && type == 1 && count >= 1;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BeginUpdateResourceW(string pFileName, [MarshalAs(UnmanagedType.Bool)] bool bDeleteExistingResources);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateResourceW(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, uint cbData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndUpdateResourceW(IntPtr hUpdate, [MarshalAs(UnmanagedType.Bool)] bool fDiscard);
}
