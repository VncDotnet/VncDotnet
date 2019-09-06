using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using VncDotnet.Messages;

namespace VncDotnet.WPF
{
    public class VncDotnet : Control
    {
        static VncDotnet()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(VncDotnet), new FrameworkPropertyMetadata(typeof(VncDotnet)));
        }

        private WriteableBitmap? Bitmap;


        public async Task ConnectAsync(string host, int port, string password, IEnumerable<SecurityType> securityTypes)
        {
            var client = await RfbConnection.ConnectAsync(host, port, password, securityTypes);
            client.OnVncUpdate += Client_OnVncUpdate;
            client.OnResolutionUpdate += Client_OnResolutionUpdate;
            client.Start();
        }

        private void Client_OnResolutionUpdate(int framebufferWidth, int framebufferHeight)
        {
            Application.Current?.Dispatcher.Invoke(new Action(() =>
            {
                var image = (Image) GetTemplateChild("Scene");
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
                            for (int ry = 0; ry < header.Height; ry++)
                            {
                                Marshal.Copy(data,
                                    ry * header.Width * 4,
                                    Bitmap.BackBuffer + ((
                                        ((header.Y + ry) * Bitmap.PixelWidth) +
                                        header.X
                                    ) * 4),
                                    header.Width * 4);
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
    }
}
