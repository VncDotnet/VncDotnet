using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Sockets;
using System.Text;
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
        public static readonly RfbEncoding[] SupportedEncodings = new RfbEncoding[] { RfbEncoding.Raw };

        public static async Task<RfbConnection> ConnectAsync(string host, int port, string password, IEnumerable<SecurityType> securityTypes)
        {
            var tcpClient = new TcpClient
            {
                NoDelay = true
            };
            await tcpClient.ConnectAsync(host, port);
            var incomingPacketsPipe = new Pipe();
            var incomingPacketsPipeWriterTask = Task.Run(() => WriteToRfbPipeAsync(tcpClient.Client, incomingPacketsPipe.Writer));

            // Protocol Version
            var version = await ParseProtocolVersionAsync(incomingPacketsPipe.Reader);
            await SendProtocolVersionAsync(tcpClient.Client);

            // Security Types
            var theirSecurityTypes = await ParseSecurityTypesAsync(incomingPacketsPipe.Reader);
            if (theirSecurityTypes.Length == 0)
                throw new NoSecurityTypesException();
            if (theirSecurityTypes[0] == 0)
                //TODO read msg
                throw new RejectException();
            var securityType = SelectSecurityType(securityTypes, theirSecurityTypes);
            await SendSecurityTypeAsync(tcpClient.Client, securityType);

            if (securityType == SecurityType.None)
            {
                var securityResult = await ParseSecurityResultAsync(incomingPacketsPipe.Reader);
                if (securityResult != 0)
                    //TODO read msg
                    throw new RejectException();
            }
            else if (securityType == SecurityType.VncAuthentication)
            {
                var challenge = await ParseSecurityChallengeAsync(incomingPacketsPipe.Reader);
                await SendSecurityResponseAsync(tcpClient.Client, Utils.EncryptChallenge(password, challenge));
                var securityResult = await ParseSecurityResultAsync(incomingPacketsPipe.Reader);
                if (securityResult != 0)
                    //TODO read msg
                    throw new RejectException();
            }

            // Finish initializing protocol with host
            await SendClientInitAsync(tcpClient.Client, true);
            var serverInitMessage = await ParseServerInitAsync(incomingPacketsPipe.Reader);
            Debug.WriteLine(serverInitMessage);
            await SendSetEncodingsMessageAsync(tcpClient.Client, SupportedEncodings);

            var connection = new RfbConnection(tcpClient, incomingPacketsPipe, serverInitMessage);
            return connection;
        }


        private static readonly byte[][] SupportedServerProtocolVersions = new byte[][]
        {
            Encoding.ASCII.GetBytes("RFB 003.008\n")
        };

        private static async Task<RfbVersion> ParseProtocolVersionAsync(PipeReader reader)
        {
            var result = await reader.ReadMinBytesAsync(12);
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

        private static async Task SendProtocolVersionAsync(Socket socket)
        {
            await socket.SendAsync(Encoding.ASCII.GetBytes($"RFB 003.008\n"), SocketFlags.None);
        }

        private static async Task<SecurityType[]> ParseSecurityTypesAsync(PipeReader reader)
        {
            SecurityType[] types;
            var typesLength = await reader.ReadByteAsync();
            if (typesLength == 0)
                throw new NoSecurityTypesException();
            types = new SecurityType[typesLength];

            for (var i = 0; i < typesLength; ++i)
            {
                types[i] = (SecurityType) await reader.ReadByteAsync();
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

        private static async Task SendSecurityTypeAsync(Socket socket, SecurityType securityType)
        {
            await socket.SendAsync(new byte[] { (byte) securityType }, SocketFlags.None);
        }

        private static Task<uint> ParseSecurityResultAsync(PipeReader reader)
        {
            return reader.ReadU32BEAsync();
        }

        private static Task<byte[]> ParseSecurityChallengeAsync(PipeReader reader)
        {
            return reader.ReadBytesAsync(16);
        }

        private static Task SendSecurityResponseAsync(Socket socket, byte[] response)
        {
            return socket.SendAsync(response, SocketFlags.None);
        }

        private static Task SendClientInitAsync(Socket socket, bool shared)
        {
            return socket.SendAsync(new byte[] { (byte) (shared ? 1 : 0) }, SocketFlags.None);
        }

        private static async Task<ServerInitMessage> ParseServerInitAsync(PipeReader reader)
        {
            var result = await reader.ReadMinBytesAsync(24);
            var messageBytes = result.Buffer.Slice(0, 24).ToArray();
            reader.AdvanceTo(result.Buffer.GetPosition(24));
            var nameLength = BinaryPrimitives.ReadUInt32BigEndian(messageBytes.AsSpan(20, 4));
            var nameBytes = await reader.ReadBytesAsync((int) nameLength);
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
                Memory<byte> memory = writer.GetMemory(4096);
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