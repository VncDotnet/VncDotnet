using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.IO.Pipelines;
using System.Threading.Tasks;
using System.Threading;
using System.Buffers;
using System.Diagnostics;
using System.Linq;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.IO.Compression;
using System.IO;
using VncDotnet.Messages;
using VncDotnet.Encodings;
using System.Drawing;
using System.Collections;

namespace VncDotnet
{
    #region Enum Definitions
    public enum RfbEncoding
    {
        Raw = 0,
        ZRLE = 16
    }

    public enum RfbServerMessageType
    {
        FramebufferUpdate = 0,
        SetColorMapEntries = 1,
        Bell = 2,
        ServerCutText = 3
    }

    enum RfbClientMessageType
    {
        SetPixelFormat = 0,
        SetEncodings = 2,
        FramebufferUpdateRequest = 3,
        KeyEvent = 4,
        PointerEvent = 5,
        ClientCutText = 6
    }
    #endregion

    public class MonitorSnippet
    {
        public ushort X { get; set; }
        public ushort Y { get; set; }
        public ushort Width { get; set; }
        public ushort Height { get; set; }

        public MonitorSnippet()
        {

        }

        public MonitorSnippet(ushort x, ushort y, ushort width, ushort height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    public partial class RfbConnection
    {
        private readonly CancellationTokenSource CancelSource = new CancellationTokenSource();
        private readonly TcpClient Tcp;
        private readonly Pipe IncomingPacketsPipe;
        private readonly MonitorSnippet? Section;
        private readonly ZRLEEncoding ZRLEEncoding = new ZRLEEncoding();
        private readonly RawEncoding RawEncoding = new RawEncoding();
        private readonly List<IVncHandler> Handlers = new List<IVncHandler>();
        private readonly SemaphoreSlim Lock = new SemaphoreSlim(1, 1);

        public ushort FramebufferWidth { get; private set; }
        public ushort FramebufferHeight { get; private set; }
        public PixelFormat PixelFormat { get; private set; }
        public string Name { get; private set; }

        public Task Start()
        {
            return Task.Run(Loop);
        }

        public void Stop()
        {
            CancelSource.Cancel();
        }

        public async Task Attach(IVncHandler handler)
        {
            await Lock.WaitAsync();
            try
            {
                Handlers.Add(handler);
                ushort x = 0;
                ushort y = 0;
                ushort width = FramebufferWidth;
                ushort height = FramebufferHeight;
                if (Section != null)
                {
                    x = Section.X;
                    y = Section.Y;
                    width = Section.Width;
                    height = Section.Height;
                }
                SendFramebufferUpdateRequest(x, y, width, height, false);
                handler.HandleResolutionUpdate(width, height);
            }
            finally
            {
                Lock.Release();
            }
        }

        public async Task Detach(IVncHandler handler)
        {
            await Lock.WaitAsync();
            try
            {
                Handlers.Remove(handler);
            }
            finally
            {
                Lock.Release();
            }
        }

        public RfbConnection(TcpClient client, Pipe pipe, ServerInitMessage serverInitMessage, MonitorSnippet? section)
        {
            Tcp = client;
            IncomingPacketsPipe = pipe;
            FramebufferWidth = serverInitMessage.FramebufferWidth;
            FramebufferHeight = serverInitMessage.FramebufferHeight;
            PixelFormat = serverInitMessage.PixelFormat;
            Name = serverInitMessage.Name;
            Section = section;
        }

        private int SendFramebufferUpdateRequest(ushort x, ushort y, ushort width, ushort height, bool incremental)
        {
            var buf = new byte[10];
            buf[0] = (byte) RfbClientMessageType.FramebufferUpdateRequest;
            buf[1] = (byte) (incremental ? 1 : 0);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), x);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4, 2), y);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6, 2), width);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8, 2), height);
            try
            {
                return Tcp.Client.Send(buf); //TODO ensure all bytes are sent
            }
            catch (Exception e)
            {
                throw new VncConnectionException("WriteFramebufferUpdateRequest failed", e);
            }
        }

        private async Task<int> ParseFramebufferUpdateHeader(CancellationToken token)
        {
            ReadResult result = await IncomingPacketsPipe.Reader.ReadMinBytesAsync(3, token);
            var rectanglesCount = BinaryPrimitives.ReadUInt16BigEndian(result.Buffer.Slice(1, 2).ToArray());
            IncomingPacketsPipe.Reader.AdvanceTo(result.Buffer.GetPosition(3));
            return rectanglesCount;
        }

        private async Task<(RfbRectangleHeader, byte[])> ParseRectangle(PixelFormat format, CancellationToken token)
        {
            ReadResult result = await IncomingPacketsPipe.Reader.ReadMinBytesAsync(12, token);
            var header = ParseRectangleHeader(result.Buffer.Slice(0, 12).ToArray());
            IncomingPacketsPipe.Reader.AdvanceTo(result.Buffer.GetPosition(12));
            return header.Encoding switch
            {
                RfbEncoding.ZRLE => (header, await ZRLEEncoding.ParseRectangle(IncomingPacketsPipe.Reader, header, format, token)),
                RfbEncoding.Raw => (header, await RawEncoding.ParseRectangle(IncomingPacketsPipe.Reader, header, format, token)),
                _ => throw new Exception($"unknown enc {header.Encoding}"),
            };
        }

        private RfbRectangleHeader ParseRectangleHeader(ReadOnlySpan<byte> span)
        {
            return new RfbRectangleHeader(BinaryPrimitives.ReadUInt16BigEndian(span[0..2]),
                BinaryPrimitives.ReadUInt16BigEndian(span[2..4]),
                BinaryPrimitives.ReadUInt16BigEndian(span[4..6]),
                BinaryPrimitives.ReadUInt16BigEndian(span[6..8]),
                BinaryPrimitives.ReadInt32BigEndian(span[8..12]));
        }

        private async Task Loop()
        {
            try
            {
                ushort x = 0;
                ushort y = 0;
                ushort width = FramebufferWidth;
                ushort height = FramebufferHeight;
                if (Section != null)
                {
                    x = Section.X;
                    y = Section.Y;
                    width = Section.Width;
                    height = Section.Height;
                }
                OnResolutionUpdate(width, height);
                var stopWatch = new Stopwatch();
                SendFramebufferUpdateRequest(x, y, width, height, false);
                while (!CancelSource.IsCancellationRequested)
                {
                    stopWatch.Restart();
                    var messageType = (RfbServerMessageType)await IncomingPacketsPipe.Reader.ReadByteAsync(CancelSource.Token);
                    switch (messageType)
                    {
                        case RfbServerMessageType.FramebufferUpdate:
                            SendFramebufferUpdateRequest(x, y, width, height, true);
                            var rectanglesCount = await ParseFramebufferUpdateHeader(CancelSource.Token);
                            var rectangles = new (RfbRectangleHeader, byte[])[rectanglesCount];
                            for (var i = 0; i < rectanglesCount; ++i)
                            {
                                rectangles[i] = await ParseRectangle(PixelFormat, CancelSource.Token);
                            }
                            OnFramebufferUpdate(rectangles);
                            foreach (var rect in rectangles)
                            {
                                ArrayPool<byte>.Shared.Return(rect.Item2);
                            }
                            break;
                        case RfbServerMessageType.Bell:
                            Debug.WriteLine($"BELL");
                            break;
                        case RfbServerMessageType.ServerCutText:
                            await ParseServerCutText();
                            break;
                        case RfbServerMessageType.SetColorMapEntries:
                            throw new NotImplementedException();
                        default:
                            throw new InvalidDataException();
                    }
                }
            }
            catch (TaskCanceledException) { }
        }

        private async Task ParseServerCutText()
        {
            ReadResult result = await IncomingPacketsPipe.Reader.ReadMinBytesAsync(7, CancelSource.Token);
            var length = BinaryPrimitives.ReadUInt32BigEndian(result.Buffer.Slice(3, 4).ToArray());
            IncomingPacketsPipe.Reader.AdvanceTo(result.Buffer.GetPosition(7));
            long remaining = length;
            StringBuilder builder = new StringBuilder();
            while (remaining > 0)
            {
                result = await IncomingPacketsPipe.Reader.ReadAsync(CancelSource.Token);
                long read = 0;
                foreach (var segment in result.Buffer)
                {
                    if (remaining > segment.Length)
                    {
                        builder.Append(Encoding.ASCII.GetString(segment.Span));
                        read += segment.Length;
                        remaining -= segment.Length;
                    }
                    else
                    {
                        builder.Append(Encoding.ASCII.GetString(segment.Span.Slice(0, (int) remaining)));
                        read += remaining;
                        remaining -= remaining;
                        break;
                    }
                }
                IncomingPacketsPipe.Reader.AdvanceTo(result.Buffer.GetPosition(read));
                if (remaining > 0 && result.IsCompleted)
                    throw new VncConnectionException();
            }
            Debug.WriteLine($"ParseServerCutText {builder.ToString()}");
        }

        private void OnResolutionUpdate(ushort framebufferWidth, ushort framebufferHeight)
        {
            Lock.WaitAsync();
            try
            {
                foreach (var handler in Handlers)
                {
                    try
                    {
                        handler.HandleResolutionUpdate(framebufferWidth, framebufferHeight);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"{e.Message}\n{e.StackTrace}");
                    }
                }
            }
            finally
            {
                Lock.Release();
            }
        }

        private void OnFramebufferUpdate(IEnumerable<(RfbRectangleHeader, byte[])> rectangles)
        {
            Lock.Wait();
            try
            {
                foreach (var handler in Handlers)
                {
                    try
                    {
                        handler.HandleFramebufferUpdate(rectangles);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"{e.Message}\n{e.StackTrace}");
                    }
                }
            }
            finally
            {
                Lock.Release();
            }
        }
    }
}
