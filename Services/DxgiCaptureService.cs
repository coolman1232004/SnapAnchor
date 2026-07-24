using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace SnapAnchor.Services;

/// <summary>
/// Desktop Duplication (DXGI) still-image capture for cases where GDI
/// <see cref="Drawing.Graphics.CopyFromScreen"/> returns blank or stale frames
/// (some full-screen DirectX apps and multi-GPU setups). Always falls back to GDI.
/// </summary>
internal static class DxgiCaptureService
{
    private const uint CreateDeviceBgraSupport = 0x20;
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorCannotProtectContent = unchecked((int)0x887A002A);

    internal static bool TryCapture(Drawing.Rectangle bounds, bool includeCursor, out BitmapSource? image)
    {
        image = null;
        if (bounds.Width < 1 || bounds.Height < 1) return false;

        try
        {
            using var assembled = new Drawing.Bitmap(bounds.Width, bounds.Height, DrawingImaging.PixelFormat.Format32bppPArgb);
            using (var graphics = Drawing.Graphics.FromImage(assembled))
            {
                graphics.Clear(Drawing.Color.Transparent);
                var covered = false;
                foreach (var monitor in DisplayTopologyService.EnumerateMonitorBoundsPixels())
                {
                    var intersection = Drawing.Rectangle.Intersect(bounds, monitor);
                    if (intersection.Width < 1 || intersection.Height < 1) continue;
                    if (!TryCaptureMonitorRegion(monitor, intersection, out var regionBitmap) || regionBitmap is null)
                        return false;
                    using (regionBitmap)
                    {
                        graphics.DrawImageUnscaled(regionBitmap,
                            intersection.Left - bounds.Left,
                            intersection.Top - bounds.Top);
                    }
                    covered = true;
                }

                if (!covered) return false;
                if (includeCursor) DrawCursor(graphics, bounds);
            }

            image = ToBitmapSource(assembled);
            return image is not null;
        }
        catch
        {
            image = null;
            return false;
        }
    }

    private static bool TryCaptureMonitorRegion(Drawing.Rectangle monitorBounds, Drawing.Rectangle absoluteRegion, out Drawing.Bitmap? crop)
    {
        crop = null;
        var device = IntPtr.Zero;
        var context = IntPtr.Zero;
        var dxgiDevice = IntPtr.Zero;
        var adapter = IntPtr.Zero;
        var output = IntPtr.Zero;
        var output1 = IntPtr.Zero;
        var duplication = IntPtr.Zero;
        var desktopResource = IntPtr.Zero;
        var desktopTexture = IntPtr.Zero;
        var staging = IntPtr.Zero;

        try
        {
            var featureLevels = new[] { 0xb000 /* 11_0 */, 0xa100 /* 10_1 */, 0xa000 /* 10_0 */ };
            var hr = D3D11CreateDevice(
                IntPtr.Zero,
                1 /* D3D_DRIVER_TYPE_HARDWARE */,
                IntPtr.Zero,
                CreateDeviceBgraSupport,
                featureLevels,
                featureLevels.Length,
                7,
                out device,
                out _,
                out context);
            if (hr < 0 || device == IntPtr.Zero) return false;

            hr = Marshal.QueryInterface(device, ref IidDxgiDevice, out dxgiDevice);
            if (hr < 0 || dxgiDevice == IntPtr.Zero) return false;

            hr = IDXGIDevice_GetParent(dxgiDevice, ref IidDxgiAdapter, out adapter);
            if (hr < 0 || adapter == IntPtr.Zero) return false;

            if (!TryFindOutput(adapter, monitorBounds, out output) || output == IntPtr.Zero) return false;

            hr = Marshal.QueryInterface(output, ref IidDxgiOutput1, out output1);
            if (hr < 0 || output1 == IntPtr.Zero) return false;

            hr = IDXGIOutput1_DuplicateOutput(output1, device, out duplication);
            if (hr < 0 || duplication == IntPtr.Zero) return false;

            // First frames often time out while the desktop compositor warms up.
            // Keep the budget small so GDI fallback stays snappy when DXGI is cold.
            var frameInfo = new DxgiOutduplFrameInfo();
            for (var attempt = 0; attempt < 4; attempt++)
            {
                hr = IDXGIOutputDuplication_AcquireNextFrame(duplication, 40, out frameInfo, out desktopResource);
                if (hr == DxgiErrorWaitTimeout) continue;
                if (hr == DxgiErrorAccessLost || hr == DxgiErrorCannotProtectContent) return false;
                if (hr < 0) return false;
                break;
            }
            if (desktopResource == IntPtr.Zero) return false;

            hr = Marshal.QueryInterface(desktopResource, ref IidD3D11Texture2D, out desktopTexture);
            if (hr < 0 || desktopTexture == IntPtr.Zero) return false;

            D3D11Texture2D_GetDesc(desktopTexture, out var desc);
            desc.BindFlags = 0;
            desc.CPUAccessFlags = 0x20000; // D3D11_CPU_ACCESS_READ
            desc.Usage = 3; // D3D11_USAGE_STAGING
            desc.MiscFlags = 0;

            hr = ID3D11Device_CreateTexture2D(device, ref desc, IntPtr.Zero, out staging);
            if (hr < 0 || staging == IntPtr.Zero) return false;

            ID3D11DeviceContext_CopyResource(context, staging, desktopTexture);

            var mapped = new D3D11MappedSubresource();
            hr = ID3D11DeviceContext_Map(context, staging, 0, 1 /* READ */, 0, out mapped);
            if (hr < 0 || mapped.DataPointer == IntPtr.Zero) return false;

            try
            {
                var relative = new Drawing.Rectangle(
                    absoluteRegion.Left - monitorBounds.Left,
                    absoluteRegion.Top - monitorBounds.Top,
                    absoluteRegion.Width,
                    absoluteRegion.Height);
                relative.Intersect(new Drawing.Rectangle(0, 0, (int)desc.Width, (int)desc.Height));
                if (relative.Width < 1 || relative.Height < 1) return false;

                crop = new Drawing.Bitmap(relative.Width, relative.Height, DrawingImaging.PixelFormat.Format32bppPArgb);
                var data = crop.LockBits(
                    new Drawing.Rectangle(0, 0, relative.Width, relative.Height),
                    DrawingImaging.ImageLockMode.WriteOnly,
                    DrawingImaging.PixelFormat.Format32bppPArgb);
                try
                {
                    var rowBytes = relative.Width * 4;
                    var rowBuffer = new byte[rowBytes];
                    for (var y = 0; y < relative.Height; y++)
                    {
                        var source = IntPtr.Add(mapped.DataPointer,
                            (relative.Top + y) * mapped.RowPitch + relative.Left * 4);
                        Marshal.Copy(source, rowBuffer, 0, rowBytes);
                        Marshal.Copy(rowBuffer, 0, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
                    }
                }
                finally
                {
                    crop.UnlockBits(data);
                }
            }
            finally
            {
                ID3D11DeviceContext_Unmap(context, staging, 0);
            }

            return crop is not null;
        }
        finally
        {
            if (duplication != IntPtr.Zero)
            {
                try { IDXGIOutputDuplication_ReleaseFrame(duplication); } catch { /* best effort */ }
            }
            SafeRelease(staging);
            SafeRelease(desktopTexture);
            SafeRelease(desktopResource);
            SafeRelease(duplication);
            SafeRelease(output1);
            SafeRelease(output);
            SafeRelease(adapter);
            SafeRelease(dxgiDevice);
            SafeRelease(context);
            SafeRelease(device);
        }
    }

    private static bool TryFindOutput(IntPtr adapter, Drawing.Rectangle monitorBounds, out IntPtr output)
    {
        output = IntPtr.Zero;
        for (uint index = 0; ; index++)
        {
            var hr = IDXGIAdapter_EnumOutputs(adapter, index, out var candidate);
            if (hr < 0 || candidate == IntPtr.Zero) break;
            try
            {
                var desc = new DxgiOutputDesc();
                if (IDXGIOutput_GetDesc(candidate, out desc) >= 0)
                {
                    var bounds = new Drawing.Rectangle(
                        desc.DesktopCoordinates.Left,
                        desc.DesktopCoordinates.Top,
                        desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                        desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top);
                    if (bounds == monitorBounds ||
                        Drawing.Rectangle.Intersect(bounds, monitorBounds).Equals(monitorBounds))
                    {
                        output = candidate;
                        candidate = IntPtr.Zero;
                        return true;
                    }
                }
            }
            finally
            {
                SafeRelease(candidate);
            }
        }
        return false;
    }

    private static BitmapSource ToBitmapSource(Drawing.Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            return CaptureService.NormalizeDpi96(source);
        }
        finally
        {
            NativeMethods.DeleteObject(handle);
        }
    }

    private static void DrawCursor(Drawing.Graphics graphics, Drawing.Rectangle bounds)
    {
        var info = new NativeMethods.CursorInfo { Size = Marshal.SizeOf<NativeMethods.CursorInfo>() };
        if (!NativeMethods.GetCursorInfo(ref info) || (info.Flags & NativeMethods.CursorShowing) == 0 || info.Cursor == IntPtr.Zero) return;
        var hotspotX = 0;
        var hotspotY = 0;
        if (NativeMethods.GetIconInfo(info.Cursor, out var iconInfo))
        {
            hotspotX = iconInfo.HotspotX;
            hotspotY = iconInfo.HotspotY;
            if (iconInfo.MaskBitmap != IntPtr.Zero) NativeMethods.DeleteObject(iconInfo.MaskBitmap);
            if (iconInfo.ColorBitmap != IntPtr.Zero) NativeMethods.DeleteObject(iconInfo.ColorBitmap);
        }
        var dc = graphics.GetHdc();
        try
        {
            NativeMethods.DrawIconEx(dc,
                info.ScreenPosition.X - bounds.Left - hotspotX,
                info.ScreenPosition.Y - bounds.Top - hotspotY,
                info.Cursor, 0, 0, 0, IntPtr.Zero, NativeMethods.DiNormal);
        }
        finally
        {
            graphics.ReleaseHdc(dc);
        }
    }

    private static void SafeRelease(IntPtr punk)
    {
        if (punk != IntPtr.Zero) Marshal.Release(punk);
    }

    private static Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static Guid IidDxgiAdapter = new("2411e7e1-12ac-4ccf-bd14-9798e8534dc0");
    private static Guid IidDxgiOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static Guid IidD3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiOutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public NativeRect DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiOutduplFrameInfo
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public DxgiOutduplPointerPosition PointerPosition;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiOutduplPointerPosition
    {
        public int PositionX;
        public int PositionY;
        public int Visible;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11MappedSubresource
    {
        public IntPtr DataPointer;
        public int RowPitch;
        public int DepthPitch;
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        int[]? featureLevels,
        int featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    private static int IDXGIDevice_GetParent(IntPtr device, ref Guid riid, out IntPtr parent)
    {
        parent = IntPtr.Zero;
        var vtable = Marshal.ReadIntPtr(device);
        var method = Marshal.GetDelegateForFunctionPointer<GetParentDelegate>(Marshal.ReadIntPtr(vtable, 6 * IntPtr.Size));
        return method(device, ref riid, out parent);
    }

    private static int IDXGIAdapter_EnumOutputs(IntPtr adapter, uint index, out IntPtr output)
    {
        output = IntPtr.Zero;
        var vtable = Marshal.ReadIntPtr(adapter);
        var method = Marshal.GetDelegateForFunctionPointer<EnumOutputsDelegate>(Marshal.ReadIntPtr(vtable, 7 * IntPtr.Size));
        return method(adapter, index, out output);
    }

    private static int IDXGIOutput_GetDesc(IntPtr output, out DxgiOutputDesc desc)
    {
        desc = default;
        var vtable = Marshal.ReadIntPtr(output);
        var method = Marshal.GetDelegateForFunctionPointer<GetDescDelegate>(Marshal.ReadIntPtr(vtable, 7 * IntPtr.Size));
        return method(output, out desc);
    }

    private static int IDXGIOutput1_DuplicateOutput(IntPtr output1, IntPtr device, out IntPtr duplication)
    {
        duplication = IntPtr.Zero;
        var vtable = Marshal.ReadIntPtr(output1);
        // IDXGIOutput1::DuplicateOutput is slot 22 (IUnknown 0-2, IDXGIObject 3-6, IDXGIOutput 7-17, Output1 18-22)
        var method = Marshal.GetDelegateForFunctionPointer<DuplicateOutputDelegate>(Marshal.ReadIntPtr(vtable, 22 * IntPtr.Size));
        return method(output1, device, out duplication);
    }

    private static int IDXGIOutputDuplication_AcquireNextFrame(IntPtr duplication, uint timeoutMs, out DxgiOutduplFrameInfo frameInfo, out IntPtr desktopResource)
    {
        frameInfo = default;
        desktopResource = IntPtr.Zero;
        var vtable = Marshal.ReadIntPtr(duplication);
        var method = Marshal.GetDelegateForFunctionPointer<AcquireNextFrameDelegate>(Marshal.ReadIntPtr(vtable, 8 * IntPtr.Size));
        return method(duplication, timeoutMs, out frameInfo, out desktopResource);
    }

    private static int IDXGIOutputDuplication_ReleaseFrame(IntPtr duplication)
    {
        var vtable = Marshal.ReadIntPtr(duplication);
        var method = Marshal.GetDelegateForFunctionPointer<ReleaseFrameDelegate>(Marshal.ReadIntPtr(vtable, 14 * IntPtr.Size));
        return method(duplication);
    }

    private static void D3D11Texture2D_GetDesc(IntPtr texture, out D3D11Texture2DDesc desc)
    {
        desc = default;
        var vtable = Marshal.ReadIntPtr(texture);
        var method = Marshal.GetDelegateForFunctionPointer<GetTextureDescDelegate>(Marshal.ReadIntPtr(vtable, 10 * IntPtr.Size));
        method(texture, out desc);
    }

    private static int ID3D11Device_CreateTexture2D(IntPtr device, ref D3D11Texture2DDesc desc, IntPtr initialData, out IntPtr texture)
    {
        texture = IntPtr.Zero;
        var vtable = Marshal.ReadIntPtr(device);
        var method = Marshal.GetDelegateForFunctionPointer<CreateTexture2DDelegate>(Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size));
        return method(device, ref desc, initialData, out texture);
    }

    private static void ID3D11DeviceContext_CopyResource(IntPtr context, IntPtr destination, IntPtr source)
    {
        var vtable = Marshal.ReadIntPtr(context);
        var method = Marshal.GetDelegateForFunctionPointer<CopyResourceDelegate>(Marshal.ReadIntPtr(vtable, 47 * IntPtr.Size));
        method(context, destination, source);
    }

    private static int ID3D11DeviceContext_Map(IntPtr context, IntPtr resource, uint subresource, int mapType, uint mapFlags, out D3D11MappedSubresource mapped)
    {
        mapped = default;
        var vtable = Marshal.ReadIntPtr(context);
        var method = Marshal.GetDelegateForFunctionPointer<MapDelegate>(Marshal.ReadIntPtr(vtable, 14 * IntPtr.Size));
        return method(context, resource, subresource, mapType, mapFlags, out mapped);
    }

    private static void ID3D11DeviceContext_Unmap(IntPtr context, IntPtr resource, uint subresource)
    {
        var vtable = Marshal.ReadIntPtr(context);
        var method = Marshal.GetDelegateForFunctionPointer<UnmapDelegate>(Marshal.ReadIntPtr(vtable, 15 * IntPtr.Size));
        method(context, resource, subresource);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetParentDelegate(IntPtr self, ref Guid riid, out IntPtr parent);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumOutputsDelegate(IntPtr self, uint index, out IntPtr output);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescDelegate(IntPtr self, out DxgiOutputDesc desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DuplicateOutputDelegate(IntPtr self, IntPtr device, out IntPtr duplication);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AcquireNextFrameDelegate(IntPtr self, uint timeoutMs, out DxgiOutduplFrameInfo frameInfo, out IntPtr desktopResource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseFrameDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetTextureDescDelegate(IntPtr self, out D3D11Texture2DDesc desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DDelegate(IntPtr self, ref D3D11Texture2DDesc desc, IntPtr initialData, out IntPtr texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceDelegate(IntPtr self, IntPtr destination, IntPtr source);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapDelegate(IntPtr self, IntPtr resource, uint subresource, int mapType, uint mapFlags, out D3D11MappedSubresource mapped);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapDelegate(IntPtr self, IntPtr resource, uint subresource);
}
