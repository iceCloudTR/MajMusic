#nullable enable

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using MajMusic.Presentation.Views;

namespace MajMusic.Preview
{
    internal sealed partial class DesktopMajView
    {
        private nint _clipAttachment;
        private SubclassProc? _clipProc;
        private PixelRect? _appliedClip;
        private double _aperture = 1;

        /// <inheritdoc />
        public void SetAperture(double fraction)
        {
            _aperture = fraction;
            if (_clipAttachment != 0)
            {
                ApplyWindowsDisc();
            }
        }

        /// <summary>Follows the actual client size, which WebView2 can change after Avalonia has finished layout.</summary>
        private void AttachWindowsDisc(object? sender, EventArgs args)
        {
            if (!_attached || _clipAttachment != 0 || TryGetPlatformHandle() is not { HandleDescriptor: "HWND", Handle: not 0 } web ||
                TopLevel.GetTopLevel(this)?.TryGetPlatformHandle() is not { Handle: not 0 } window)
            {
                return;
            }
            var attachment = GetParent(web.Handle);
            if (attachment == 0 || attachment == window.Handle || GetParent(attachment) != window.Handle)
            {
                return;
            }
            // Both windows belong to the UI thread. Retain this callback until detach or WM_NCDESTROY.
            _clipProc ??= OnAttachmentMessage;
            if (SetWindowSubclass(attachment, _clipProc, 1, 0))
            {
                _clipAttachment = attachment;
                PracticeStage.ReportNativeClip(this, ApplyWindowsDisc());
            }
        }

        private nint OnAttachmentMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
        {
            const uint size = 0x0005, destroying = 0x0082;
            if (message == destroying && window == _clipAttachment)
            {
                RemoveWindowSubclass(window, _clipProc!, id);
                _clipAttachment = 0;
                _appliedClip = null;
            }
            var result = DefSubclassProc(window, message, wParam, lParam);
            if (message == size && window == _clipAttachment)
            {
                ApplyWindowsDisc();
            }
            return result;
        }

        private bool ApplyWindowsDisc()
        {
            if (!GetClientRect(_clipAttachment, out var client))
            {
                return false;
            }
            var clip = PracticeStage.GetDiscClip(client.Right, client.Bottom, _aperture);
            if (_appliedClip == clip)
            {
                return true;
            }
            var region = CreateEllipticRgn(clip.X, clip.Y, clip.Right, clip.Bottom);
            if (region == 0)
            {
                return false;
            }
            if (SetWindowRgn(_clipAttachment, region, true) == 0)
            {
                DeleteObject(region);
                return false;
            }
            // On success ownership of the region passes to Windows.
            _appliedClip = clip;
            return true;
        }

        private void ReleaseWindowsDisc()
        {
            var attachment = _clipAttachment;
            if (attachment != 0)
            {
                _clipAttachment = 0;
                RemoveWindowSubclass(attachment, _clipProc!, 1);
                SetWindowRgn(attachment, 0, true);
            }
            _appliedClip = null;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

        [DllImport("comctl32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);

        [DllImport("comctl32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);

        [DllImport("comctl32.dll", ExactSpelling = true)]
        private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern nint GetParent(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(nint window, out NativeRect rect);

        [DllImport("gdi32.dll")]
        private static extern nint CreateEllipticRgn(int left, int top, int right, int bottom);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(nint region);
    }
}
