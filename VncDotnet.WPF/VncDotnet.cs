using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using VncDotnet.Messages;

namespace VncDotnet.WPF
{
    public class VncDotnet : Control, IDisposable
    {
        static VncDotnet()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(VncDotnet), new FrameworkPropertyMetadata(typeof(VncDotnet)));
        }

        private WriteableBitmap? Bitmap;
        private RfbConnection? Client = null;
        private MonitorSnippet? Section = null;
        private int FramebufferWidth;
        private int FramebufferHeight;

        public Task ConnectAsync(string host, int port, string password, CancellationToken token)
        {
            return ConnectAsync(host, port, password, RfbConnection.SupportedSecurityTypes, token);
        }

        public Task ConnectAsync(string host, int port, string password, MonitorSnippet? section, CancellationToken token)
        {
            return ConnectAsync(host, port, password, RfbConnection.SupportedSecurityTypes, section, token);
        }

        public Task ConnectAsync(string host, int port, string password, IEnumerable<SecurityType> securityTypes, CancellationToken token)
        {
            return ConnectAsync(host, port, password, securityTypes, null, token);
        }

        public async Task<Task> ConnectAsync(string host, int port, string password, IEnumerable<SecurityType> securityTypes, MonitorSnippet? section, CancellationToken token)
        {
            Client = await RfbConnection.ConnectAsync(host, port, password, securityTypes, section, token);
            Section = section;
            Client.OnVncUpdate += Client_OnVncUpdate;
            Client.OnResolutionUpdate += Client_OnResolutionUpdate;
            return Client.Start();
        }

        private void Client_OnResolutionUpdate(int framebufferWidth, int framebufferHeight)
        {
            Application.Current?.Dispatcher.Invoke(new Action(() =>
            {
                var image = (Image) GetTemplateChild("Scene");
                FramebufferWidth = framebufferWidth;
                FramebufferHeight = framebufferHeight;
                Bitmap = BitmapFactory.New(framebufferWidth, framebufferHeight);
                image.Source = Bitmap;
            }));
        }

        private void Client_OnVncUpdate(IEnumerable<(RfbRectangleHeader header, byte[] data)> rectangles)
        {
            Application.Current?.Dispatcher.Invoke(new Action(() =>
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();
                if (Bitmap == null)
                    throw new InvalidOperationException();
                using (var ctx = Bitmap.GetBitmapContext())
                {
                    Bitmap.Lock();

                    foreach ((var header, var data) in rectangles)
                    {
                        if (data != null)
                        {
                            for (ushort ry = 0; ry < header.Height; ry++)
                            {
                                int bitmapRow = ry + header.Y - BitmapY();

                                // skip rows outside the bitmap
                                if (bitmapRow < 0 || bitmapRow >= FramebufferHeight)
                                    continue;

                                // cull rows partly outside the bitmap
                                int rowLength = header.Width;

                                // left border
                                int leftSurplus = 0;
                                if (BitmapX() > header.X)
                                {
                                    int diff = BitmapX() - header.X;
                                    rowLength -= diff;
                                    leftSurplus += diff;
                                }
                                if (leftSurplus > header.Width)
                                    continue;
                                int leftPadding = 0;
                                if (header.X > BitmapX())
                                    leftPadding = header.X - BitmapX();

                                // right border
                                int rightSurplus = 0;
                                if (header.X + header.Width > BitmapX() + FramebufferWidth)
                                {
                                    rightSurplus = header.X + header.Width - BitmapX() - FramebufferWidth;
                                    rowLength -= rightSurplus;
                                }

                                // GO!
                                if (rowLength > 0)
                                {
                                    int srcOffset = ((ry * header.Width) + leftSurplus) * 4;
                                    int dstOffset = (((bitmapRow * FramebufferWidth) + leftPadding) * 4);
                                    if (dstOffset < 0 || dstOffset + (rowLength * 4) > Bitmap.PixelHeight * Bitmap.PixelWidth * 4)
                                        throw new InvalidDataException();
                                    Marshal.Copy(data,
                                        srcOffset,
                                        Bitmap.BackBuffer + dstOffset,
                                        rowLength * 4);
                                }
                            }
                            ArrayPool<byte>.Shared.Return(data);
                        }
                    }

                    Bitmap.DrawRectangle(0, 0, 64, 64, Colors.GreenYellow);
                    Bitmap.AddDirtyRect(new Int32Rect(0, 0, Bitmap.PixelWidth, Bitmap.PixelHeight));
                    Bitmap.Unlock();
                }
                stopwatch.Stop();
                //Debug.WriteLine($"Client_OnVncUpdate invocation took {stopwatch.Elapsed}");
            }));
        }

        private int BitmapX()
        {
            if (Section != null)
            {
                return Section.X;
            }
            return 0;
        }

        private int BitmapY()
        {
            if (Section != null)
            {
                return Section.Y;
            }
            return 0;
        }

        public void Dispose()
        {
            Client?.Stop();
        }
    }
}
