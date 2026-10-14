using System;
using System.Runtime.InteropServices;

namespace WinTester.Tools.WebcamMic;

// GDI cleanup for CameraCaptureSession.ToBitmapSource: Bitmap.GetHbitmap() hands back a GDI handle that
// .NET does not own or free on its own — CreateBitmapSourceFromHBitmap copies the pixel data into a
// managed BitmapSource, so the HBITMAP has to be deleted immediately after, or every frame leaks a GDI
// object for the lifetime of the process.
internal static class NativeMethods
{
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);
}
