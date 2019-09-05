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

    public partial class RfbConnection
    {
        private readonly TcpClient Tcp;
        private readonly Pipe IncomingPacketsPipe;
        private readonly ServerInitMessage ServerInitMessage;
        private readonly ZRLEEncoding ZRLEEncoding = new ZRLEEncoding();
        private readonly RawEncoding RawEncoding = new RawEncoding();

        public delegate void FramebufferUpdate(IEnumerable<(RfbRectangleHeader, byte[])> rectangles);
        public event FramebufferUpdate? OnVncUpdate;

        public delegate void ResolutionUpdate(int framebufferWidth, int framebufferHeight);
        public event ResolutionUpdate? OnResolutionUpdate;

        public void Start()
        {
            Task.Run(Loop);
        }

        public RfbConnection(TcpClient client, Pipe pipe, ServerInitMessage serverInitMessage)
        {
            Tcp = client;
            IncomingPacketsPipe = pipe;
            ServerInitMessage = serverInitMessage;
        }

        private async Task WriteFramebufferUpdateRequest(ushort x, ushort y, ushort width, ushort height, bool incremental)
        {
            var buf = new byte[10];
            buf[0] = (byte) RfbClientMessageType.FramebufferUpdateRequest;
            buf[1] = (byte) (incremental ? 1 : 0);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), x);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4, 2), y);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6, 2), width);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8, 2), height);
            await Tcp.Client.SendAsync(buf, SocketFlags.None);
        }

        private async Task<RfbServerMessageType> ParseServerMessageType()
        {
            ReadResult result = await IncomingPacketsPipe.Reader.ReadAsync();
            var type = result.Buffer.First.Span[0];
            IncomingPacketsPipe.Reader.AdvanceTo(result.Buffer.GetPosition(1));
            return (RfbServerMessageType) type;
        }

        private async Task<int> ParseFramebufferUpdateHeader()
        {
            ReadResult result = await ReadMinBytesAsync(IncomingPacketsPipe.Reader, 3);
            var rectanglesCount = BinaryPrimitives.ReadUInt16BigEndian(GetSpan(result.Buffer, 3).Slice(1, 2));
            IncomingPacketsPipe.Reader.AdvanceTo(result.Buffer.GetPosition(3));
            return rectanglesCount;
        }

        private async Task<(RfbRectangleHeader, byte[])> ParseRectangle(PixelFormat format)
        {
            ReadResult result = await ReadMinBytesAsync(IncomingPacketsPipe.Reader, 12);
            var header = ParseRectangleHeader(GetSpan(result.Buffer, 12));
            IncomingPacketsPipe.Reader.AdvanceTo(result.Buffer.GetPosition(12));
            //Debug.WriteLine($"rect {header}");
            return header.Encoding switch
            {
                RfbEncoding.ZRLE => (header, await ZRLEEncoding.ParseRectangle(IncomingPacketsPipe.Reader, header)),
                RfbEncoding.Raw => (header, await RawEncoding.ParseRectangle(IncomingPacketsPipe.Reader, header, format)),
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

        public async Task Loop()
        {
            OnResolutionUpdate?.Invoke(ServerInitMessage.FramebufferWidth, ServerInitMessage.FramebufferHeight);
            var updateTimeWatch = new Stopwatch();
            var waitTimeWatch = new Stopwatch();
            await WriteFramebufferUpdateRequest(0, 0, ServerInitMessage.FramebufferWidth, ServerInitMessage.FramebufferHeight, false);
            while (true)
            {
                var messageType = await ParseServerMessageType();
                switch (messageType)
                {
                    case RfbServerMessageType.FramebufferUpdate:
                        updateTimeWatch.Restart();
                        waitTimeWatch.Stop();
                        //Debug.WriteLine($"Wait time {waitTimeWatch.Elapsed}");
                        var rectanglesCount = await ParseFramebufferUpdateHeader();
                        var rectangles = new (RfbRectangleHeader, byte[])[rectanglesCount];
                        //Debug.WriteLine($"FramebufferUpdate with {rectanglesCount} rectangles");
                        for (var i = 0; i < rectanglesCount; ++i)
                        {
                            rectangles[i] = await ParseRectangle(ServerInitMessage.PixelFormat);
                        }
                        await Task.Run(() => OnVncUpdate?.Invoke(rectangles));
                        updateTimeWatch.Stop();
                        //Debug.WriteLine($"FramebufferUpdate ({rectanglesCount} rects) took {updateTimeWatch.Elapsed}");
                        waitTimeWatch.Restart();
                        await WriteFramebufferUpdateRequest(0, 0, ServerInitMessage.FramebufferWidth, ServerInitMessage.FramebufferHeight, true);
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

        private async Task ParseServerCutText()
        {
            ReadResult result = await ReadMinBytesAsync(IncomingPacketsPipe.Reader, 7);
            var length = BinaryPrimitives.ReadUInt32BigEndian(GetSpan(result.Buffer, 7).Slice(3, 4));
            IncomingPacketsPipe.Reader.AdvanceTo(result.Buffer.GetPosition(7));
            long remaining = length;
            StringBuilder builder = new StringBuilder();
            while (remaining > 0)
            {
                result = await IncomingPacketsPipe.Reader.ReadAsync();
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
            }
            Debug.WriteLine($"ParseServerCutText {builder.ToString()}");
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task<ReadResult> ReadMinBytesAsync(PipeReader reader, int bytes)
        {
            while (true)
            {
                ReadResult result = await IncomingPacketsPipe.Reader.ReadAsync();
                if (result.Buffer.Length >= bytes)
                {
                    return result;
                }
                reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            }
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ReadOnlySpan<byte> GetSpan(ReadOnlySequence<byte> sequence, int length)
        {
            if (length > sequence.Length)
                throw new InvalidOperationException();

            if (sequence.First.Length >= length)
                return sequence.First.Span;

            var slice = sequence.Slice(0, length);
            return slice.ToArray();
        }
    }
}
