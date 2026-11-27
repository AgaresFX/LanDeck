using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PadNook.Platform;

/// <summary>
/// The real icon of a program/file/shortcut as Explorer shows it (up to 256 px, with transparency),
/// cropped to its visible area and encoded as PNG.
/// </summary>
internal static class ProgramIcon
{
    private const int Size = 256;

    public static Task<byte[]?> GetPngAsync(string path)
    {
        var tcs = new TaskCompletionSource<byte[]?>();
        // Shell COM objects are happiest on an STA thread.
        var t = new Thread(() =>
        {
            try { tcs.SetResult(Extract(path)); }
            catch { tcs.SetResult(null); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    private static byte[]? Extract(string path)
    {
        var guid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref guid, out var factory) != 0 || factory is null) return null;
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new SIZE { cx = Size, cy = Size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbmp) != 0) return null;
            using var bmp = FromHBitmap(hbmp);
            if (bmp is null) return null;
            using var cropped = CropTransparent(bmp);
            using var ms = new MemoryStream();
            cropped.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Copies a 32-bit HBITMAP keeping its alpha channel (Image.FromHbitmap drops it).</summary>
    private static Bitmap? FromHBitmap(IntPtr hbmp)
    {
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var info) == 0 || info.bmWidth <= 0 || info.bmHeight <= 0) return null;
        int w = info.bmWidth, h = info.bmHeight;
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[w * h * 4];
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbmp, 0, (uint)h, pixels, ref bmi, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }

        // Icons without an alpha channel come back fully transparent: treat them as opaque.
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha)
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        // The shell returns premultiplied alpha: convert to straight alpha for PNG.
        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (hasAlpha)
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    var a = pixels[i + 3];
                    if (a is 0 or 255) continue;
                    pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
                    pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
                    pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
                }
            for (var y = 0; y < h; y++) Marshal.Copy(pixels, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        }
        finally { result.UnlockBits(data); }
        return result;
    }

    /// <summary>Small icons come centered on a big transparent canvas: keep only the visible part (square).</summary>
    private static Bitmap CropTransparent(Bitmap src)
    {
        int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
        var data = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[src.Width * 4];
            for (var y = 0; y < src.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < src.Width; x++)
                {
                    if (row[x * 4 + 3] < 8) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        finally { src.UnlockBits(data); }
        if (maxX < 0) return new Bitmap(src);

        var side = Math.Max(maxX - minX + 1, maxY - minY + 1);
        var cx = (minX + maxX + 1) / 2;
        var cy = (minY + maxY + 1) / 2;
        var outBmp = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(outBmp);
        g.DrawImage(src, new Rectangle(0, 0, side, side), new Rectangle(cx - side / 2, cy - side / 2, side, side), GraphicsUnit.Pixel);
        return outBmp;
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        // Room for the (unused) color table GetDIBits may write.
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public int[]? colors;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
}
