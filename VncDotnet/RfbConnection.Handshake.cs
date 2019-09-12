using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VncDotnet.Messages;

namespace VncDotnet
{
    enum RfbVersion
    {
        v3_8
    }

    public enum SecurityType
    {
        None = 1,
        VncAuthentication = 2,
        RA2 = 5,
        RA2ne = 6,
        Right = 16,
        Ultra = 17,
        TLS = 18
    }

    public partial class RfbConnection
    {
        public static readonly SecurityType[] SupportedSecurityTypes = new SecurityType[] { SecurityType.None, SecurityType.VncAuthentication };
        public static readonly RfbEncoding[] SupportedEncodings = new RfbEncoding[] { RfbEncoding.ZRLE, RfbEncoding.Raw };

        public static async Task<RfbConnection> ConnectAsync(string host, int port, string password, IEnumerable<SecurityType> securityTypes, MonitorSnippet? section, CancellationToken token)
        {
            var tcpClient = new TcpClient
            {
                NoDelay = true
            };
            await tcpClient.ConnectAsync(host, port);
            var incomingPacketsPipe = new Pipe();
            var incomingPacketsPipeWriterTask = Task.Run(() => WriteToRfbPipeAsync(tcpClient.Client, incomingPacketsPipe.Writer));

            // Protocol Version
            var version = await ParseProtocolVersionAsync(incomingPacketsPipe.Reader, token);
            await SendProtocolVersionAsync(tcpClient.Client, token);

            // Security Types
            var theirSecurityTypes = await ParseSecurityTypesAsync(incomingPacketsPipe.Reader, token);
            if (theirSecurityTypes.Length == 0)
                throw new NoSecurityTypesException();
            if (theirSecurityTypes[0] == 0)
                //TODO read msg
                throw new RejectException();
            var securityType = SelectSecurityType(securityTypes, theirSecurityTypes);
            await SendSecurityTypeAsync(tcpClient.Client, securityType, token);

            if (securityType == SecurityType.None)
            {
                var securityResult = await ParseSecurityResultAsync(incomingPacketsPipe.Reader, token);
                if (securityResult != 0)
                    //TODO read msg
                    throw new RejectException();
            }
            else if (securityType == SecurityType.VncAuthentication)
            {
                var challenge = await ParseSecurityChallengeAsync(incomingPacketsPipe.Reader, token);
                await SendSecurityResponseAsync(tcpClient.Client, Utils.EncryptChallenge(password, challenge), token);
                var securityResult = await ParseSecurityResultAsync(incomingPacketsPipe.Reader, token);
                if (securityResult != 0)
                    //TODO read msg
                    throw new RejectException();
            }

            // Finish initializing protocol with host
            await SendClientInitAsync(tcpClient.Client, true, token);
            var serverInitMessage = await ParseServerInitAsync(incomingPacketsPipe.Reader, token);
            Debug.WriteLine(serverInitMessage);
            await SendSetEncodingsMessageAsync(tcpClient.Client, SupportedEncodings);

            var connection = new RfbConnection(tcpClient, incomingPacketsPipe, serverInitMessage, section);
            return connection;
        }


        private static readonly byte[][] SupportedServerProtocolVersions = new byte[][]
        {
            Encoding.ASCII.GetBytes("RFB 003.008\n")
        };

        private static async Task<RfbVersion> ParseProtocolVersionAsync(PipeReader reader, CancellationToken token)
        {
            var result = await reader.ReadMinBytesAsync(12, token);
            bool versionOk = false;
            foreach (var supportedVersion in SupportedServerProtocolVersions)
            {
                if (Enumerable.SequenceEqual(result.Buffer.ForceGetReadOnlySpan(12).Slice(0, 12).ToArray(), supportedVersion))
                {
                    versionOk = true;
                    break;
                }
            }
            if (!versionOk)
                throw new ProtocolVersionMismatchException();
            reader.AdvanceTo(result.Buffer.GetPosition(12));
            return RfbVersion.v3_8;
        }

        private static ValueTask<int> SendProtocolVersionAsync(Socket socket, CancellationToken token)
        {
            return socket.SendAsync(Encoding.ASCII.GetBytes($"RFB 003.008\n"), SocketFlags.None, token);
        }

        private static async Task<SecurityType[]> ParseSecurityTypesAsync(PipeReader reader, CancellationToken token)
        {
            SecurityType[] types;
            var typesLength = await reader.ReadByteAsync(token);
            if (typesLength == 0)
                throw new NoSecurityTypesException();
            types = new SecurityType[typesLength];

            for (var i = 0; i < typesLength; ++i)
            {
                types[i] = (SecurityType) await reader.ReadByteAsync(token);
            }
            return types;
        }

        private static SecurityType SelectSecurityType(IEnumerable<SecurityType> ourTypes, IEnumerable<SecurityType> theirTypes)
        {
            foreach (var theirType in theirTypes)
            {
                foreach (var ourType in ourTypes)
                {
                    if (theirType == ourType)
                    {
                        return theirType;
                    }
                }
            }
            throw new SecurityTypesMismatchException();
        }

        private static ValueTask<int> SendSecurityTypeAsync(Socket socket, SecurityType securityType, CancellationToken token)
        {
            return socket.SendAsync(new byte[] { (byte) securityType }, SocketFlags.None, token);
        }

        private static Task<uint> ParseSecurityResultAsync(PipeReader reader, CancellationToken token)
        {
            return reader.ReadU32BEAsync(token);
        }

        private static Task<byte[]> ParseSecurityChallengeAsync(PipeReader reader, CancellationToken token)
        {
            return reader.ReadBytesAsync(16, token);
        }

        private static ValueTask<int> SendSecurityResponseAsync(Socket socket, byte[] response, CancellationToken token)
        {
            return socket.SendAsync(response, SocketFlags.None, token); //TODO ensure everything is sent
        }

        private static ValueTask<int> SendClientInitAsync(Socket socket, bool shared, CancellationToken token)
        {
            return socket.SendAsync(new byte[] { (byte) (shared ? 1 : 0) }, SocketFlags.None, token); //TODO ensure everything is sent
        }

        private static async Task<ServerInitMessage> ParseServerInitAsync(PipeReader reader, CancellationToken token)
        {
            var result = await reader.ReadMinBytesAsync(24, token);
            var messageBytes = result.Buffer.Slice(0, 24).ToArray();
            reader.AdvanceTo(result.Buffer.GetPosition(24));
            var nameLength = BinaryPrimitives.ReadUInt32BigEndian(messageBytes.AsSpan(20, 4));
            var nameBytes = await reader.ReadBytesAsync((int) nameLength, token);
            return new ServerInitMessage(
                BinaryPrimitives.ReadUInt16BigEndian(messageBytes.AsSpan(0, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(messageBytes.AsSpan(2, 2)),
                new PixelFormat()
                {
                    BitsPerPixel = messageBytes[4],
                    Depth = messageBytes[5],
                    BigEndianFlag = messageBytes[6],
                    TrueColorFlag = messageBytes[7],
                    RedMax = BinaryPrimitives.ReadUInt16BigEndian(messageBytes.AsSpan(8, 2)),
                    GreenMax = BinaryPrimitives.ReadUInt16BigEndian(messageBytes.AsSpan(10, 2)),
                    BlueMax = BinaryPrimitives.ReadUInt16BigEndian(messageBytes.AsSpan(12, 2)),
                    RedShift = messageBytes[14],
                    GreenShift = messageBytes[15],
                    BlueShift = messageBytes[16]
                },
                Encoding.UTF8.GetString(nameBytes));
        }

        private static async Task SendSetEncodingsMessageAsync(Socket socket, IList<RfbEncoding> encodings)
        {
            byte[] buf = new byte[(encodings.Count * 4) + 4];
            buf[0] = (byte) RfbClientMessageType.SetEncodings;
            buf[1] = 0x00;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan().Slice(2, 2), (ushort) encodings.Count());
            for(int i = 0; i < encodings.Count(); i++)
            {
                BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan().Slice(4 + 4*i, 4), (int) encodings[i]);
            }
            await socket.SendAsync(buf, SocketFlags.None);
        }

        private static async Task WriteToRfbPipeAsync(Socket socket, PipeWriter writer)
        {
            while (true)
            {
                Memory<byte> memory = writer.GetMemory(256 * 1024);
                try
                {
                    int bytesRead = await socket.ReceiveAsync(memory, SocketFlags.None);
                    if (bytesRead == 0)
                    {
                        break;
                    }
                    writer.Advance(bytesRead);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"WriteToPipeAsync failed: {e}\n{e.StackTrace}");
                    break;
                }
                FlushResult result = await writer.FlushAsync();

                if (result.IsCompleted)
                {
                    break;
                }
            }
            writer.Complete();
        }
    }
}