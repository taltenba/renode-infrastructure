using System;
using System.Text;
using System.Collections.Generic;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Peripherals.UART;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Core;
using Antmicro.Migrant;

namespace Antmicro.Renode.Peripherals.Wireless
{
    /*
     * This peripheral is intended to simulate the behavior of the ESP32 device in the WIFIS project. It therefore
     * implements the custom firmware used in this project. The peripheral includes an SPI interface and two IRQ lines
     * to exchange data with the WIFIS main MCU, in addition to a UART interface enabling the simulation of wireless
     * connections with clients by communicating with an external script.
     */
    public class ESP32 : ISPIPeripheral, IUART, IGPIOSender
    {
        public ESP32(string ipAddress, int udpServerPort, int udpMessageMagic, int minUdpMessageSize, int maxUdpMessageSize, int defaultAudioBlockSize,
                     uint uartBaudRate, Bits uartStopBits, Parity uartParityBit)
        {
            if (udpServerPort < 0 || udpServerPort > 65535)
                throw new ArgumentOutOfRangeException("udpServerPort", udpServerPort, "Invalid port value.");

            if (minUdpMessageSize <= 0 || minUdpMessageSize < 4)
                throw new ArgumentOutOfRangeException("minUdpMessageSize", minUdpMessageSize, "Invalid size, must be at least 4 bytes.");

            if (maxUdpMessageSize <= 0 || (maxUdpMessageSize & 0x03) != 0)
                throw new ArgumentOutOfRangeException("maxUdpMessageSize", maxUdpMessageSize, "Invalid size, must be a positive multiple of 4.");

            if (defaultAudioBlockSize <= 0 || (defaultAudioBlockSize & 0x03) != 0)
                throw new ArgumentOutOfRangeException("defaultAudioBlockSize", defaultAudioBlockSize, "Invalid size, must be a positive multiple of 4.");

            System.Net.IPAddress addr = System.Net.IPAddress.Parse(ipAddress);

            if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new ArgumentOutOfRangeException("ipAddress", ipAddress, "The specified IP address is not a valid IPv4 address.");

            byte[] addrBytes = addr.GetAddressBytes();
            Array.Reverse(addrBytes);
            IpAddress = ParseInt32(addrBytes);

            UdpServerPort = udpServerPort;
            UdpMessageMagic = udpMessageMagic;
            MinUdpMessageSize = minUdpMessageSize;
            MaxUdpMessageSize = maxUdpMessageSize;
            DefaultAudioBlockSize = defaultAudioBlockSize;
            BaudRate = uartBaudRate;
            StopBits = uartStopBits;
            ParityBit = uartParityBit;
            AckPin = new GPIO();
            StatusPin = new GPIO();

            remoteClient = new WirelessInterface(this);
            transaction = new Transaction(this);
            msgQueue = new Queue<WirelessMessage>();
            audioBuffer = new Queue<byte>();
            stateLock = new object();

            Reset();
        }

        public void Reset()
        {
            transaction.Reset();

            lock (stateLock)
            {
                status = 0;
                audioBlockSize = DefaultAudioBlockSize;
                isConnectedToAp = false;
                audioState = AudioState.Disconnected;
                msgQueue.Clear();
                audioBuffer.Clear();

                AckPin.Set();
                StatusPin.Unset(); /* Assert Status line when initialized */
            }
        }

        public byte Transmit(byte data)
        {
            return transaction.HandleByte(data);
        }

        public void FinishTransmission()
        {
            /* Empty */
        }

        public void WriteChar(byte value)
        {
            remoteClient.WriteChar(value);
        }

        private StatusFlag ReadStatus()
        {
            lock (stateLock)
            {
                StatusFlag s = status;
                ClearStatusFlag(StatusFlag.AudioSrcConnectionError);
                StatusPin.Set(); /* De-assert Status IRQ line */
                return s;
            }
        }

        private byte[] ReadMessage()
        {
            WirelessMessage msg;

            lock (stateLock)
            {
                if (msgQueue.Count == 0)
                {
                    msg = null;
                }
                else
                {
                    msg = msgQueue.Dequeue();

                    if (msgQueue.Count == 0)
                        ClearStatusFlag(StatusFlag.HasMessage);
                    else
                        StatusPin.Unset();
                }
            }

            if (msg == null)
            {
                this.Log(LogLevel.Warning, "Tried to read message queue when empty.");
                return null;
            }

            /* Replace magic number by client source address in message data */
            GetBytesInt32(msg.Data, msg.MessageHeader.SourceAddress);

            return msg.Data;
        }

        private int ReadAudioBlock(List<byte> outBuffer)
        {
            int ret;

            lock (stateLock)
            {
                if (audioBuffer.Count < audioBlockSize)
                {
                    ret = -1;
                }
                else
                {
                    for (int i = 0; i < audioBlockSize; ++i)
                        outBuffer.Add(audioBuffer.Dequeue());

                    if (audioBuffer.Count < audioBlockSize)
                        ClearStatusFlag(StatusFlag.HasAudio);
                    else
                        StatusPin.Unset();

                    ret = 0;
                }
            }

            if (ret != 0)
            {
                this.Log(LogLevel.Warning, "No audio block ready.");
                return -1;
            }

            return 0;
        }

        private bool ProcessMessage(byte[] msgData)
        {
            lock (stateLock)
            {
                if (!isConnectedToAp)
                    return true;
            }

            /* Replace destination IP address by magic number */
            GetBytesInt32(msgData, UdpMessageMagic);

            WirelessMessage msg = new WirelessMessage(Channel.Control, IpAddress, UdpServerPort, msgData);
            remoteClient.TransmitMessage(msg);
            return true;
        }

        private bool ProcessAudioSrc(byte[] srcBytes)
        {
            AudioSource src = AudioSource.FromBytes(srcBytes);
            bool isDisconnectRequest = src.Address == 0 && src.Port == 0;

            lock (stateLock)
            {
                if (isDisconnectRequest && audioState == AudioState.Disconnected)
                    return true;

                audioBuffer.Clear();
                ClearStatusFlag(StatusFlag.HasAudio);

                if (isDisconnectRequest)
                    audioState = AudioState.Disconnected;
                else
                    audioState = AudioState.Connecting;
            }

            byte[] msgData = src.ToBytes();
            WirelessMessage msg = new WirelessMessage(Channel.AudioConnectionManagement, IpAddress, 0, msgData);
            remoteClient.TransmitMessage(msg);

            return isDisconnectRequest;
        }

        private bool ProcessAudioBlockSize(int size)
        {
            if ((size & 0x03) != 0)
            {
                this.Log(LogLevel.Warning,
                         "Invalid audio block size, must be a multiple of 4: {0}."
                          + " Size will be rounded to next multiple of 4.",
                          size);

                size = (size + 0x03) & ~0x03;
            }

            lock (stateLock)
            {
                if (size == audioBlockSize)
                    return true;

                audioBlockSize = size;

                if (audioBuffer.Count >= audioBlockSize)
                    SetStatusFlag(StatusFlag.HasAudio);
                else
                    ClearStatusFlag(StatusFlag.HasAudio);
            }

            return true;
        }

        private void RemoteMessageReceived(WirelessMessage msg)
        {
            switch (msg.MessageHeader.Channel)
            {
                case Channel.Config:
                    HandleApConfig(msg.Data);
                    break;
                case Channel.Control:
                    HandleControlMessage(msg);
                    break;
                case Channel.Audio:
                    HandleAudio(msg.Data);
                    break;
                case Channel.AudioConnectionManagement:
                    HandleAudioConnectionStatus(msg.Data);
                    break;
                default:
                    this.Log(LogLevel.Warning, "Wrong channel: {0}", msg.MessageHeader.Channel);
                    break;
            }
        }

        private void HandleApConfig(byte[] msgData)
        {
            if (msgData.Length == 0)
            {
                this.Log(LogLevel.Warning, "Invalid config message ({0} bytes).", msgData.Length);
                return;
            }

            bool isAlreadyConnected;

            lock (stateLock)
            {
                isAlreadyConnected = isConnectedToAp;
                isConnectedToAp = true;
            }

            if (isAlreadyConnected)
            {
                this.Log(LogLevel.Warning, "Unexpected data on config channel ({0} bytes).", msgData.Length);
                return;
            }

            string apConf = Encoding.UTF8.GetString(msgData);
            this.Log(LogLevel.Info, "Connected to AP: {0}", apConf);
        }

        private void HandleControlMessage(WirelessMessage msg)
        {
            lock (stateLock)
            {
                if (!isConnectedToAp)
                {
                    this.Log(LogLevel.Warning, "Control message received when not connected to AP.");
                    return;
                }

                if (msg.Data.Length < MinUdpMessageSize || msg.Data.Length > MaxUdpMessageSize)
                {
                    this.Log(LogLevel.Warning, "Unexpected control message size: {0}", msg.Data.Length);
                    return;
                }

                int msgMagic = ParseInt32(msg.Data);

                if (msgMagic != UdpMessageMagic)
                {
                    this.Log(LogLevel.Warning, "Unexpected control message magic: {0}", msgMagic);
                    return;
                }

                msgQueue.Enqueue(msg);
                SetStatusFlag(StatusFlag.HasMessage);
            }

            this.Log(LogLevel.Noisy, "Control message received ({0} bytes).", msg.Data.Length);
        }

        private void HandleAudio(byte[] data)
        {
            lock (stateLock)
            {
                if (!isConnectedToAp)
                {
                    this.Log(LogLevel.Warning, "Audio received when not connected to AP.");
                    return;
                }

                if (audioState != AudioState.Connected)
                {
                    this.Log(LogLevel.Warning, "Audio received when not connected to source.");
                    return;
                }

                foreach (byte b in data)
                    audioBuffer.Enqueue(b);

                if (audioBuffer.Count >= audioBlockSize)
                    SetStatusFlag(StatusFlag.HasAudio);
            }

            this.Log(LogLevel.Noisy, "{0} audio bytes received.", data.Length);
        }

        private void HandleAudioConnectionStatus(byte[] statusData)
        {
            if (statusData.Length != 1)
            {
                this.Log(LogLevel.Warning,
                         "Invalid audio connection status received ({0} bytes).",
                         statusData.Length);
                return;
            }

            lock (stateLock)
            {
                switch (audioState)
                {
                    case AudioState.Connecting:
                        if (statusData[0] == 0)
                        {
                            audioState = AudioState.Connected;
                            this.Log(LogLevel.Info, "Audio connection established.");
                        }
                        else
                        {
                            audioState = AudioState.Disconnected;
                            SetStatusFlag(StatusFlag.AudioSrcConnectionError);
                            this.Log(LogLevel.Info, "Audio connection failed.");
                        }

                        transaction.NotifyDataProcessed();
                        break;
                    case AudioState.Connected:
                        if (statusData[0] == 0)
                        {
                            this.Log(LogLevel.Warning,
                                     "Unexpected audio connection status received when connected: {0}.",
                                     statusData[0]);
                        }
                        else
                        {
                            audioState = AudioState.Disconnected;
                            SetStatusFlag(StatusFlag.AudioSrcConnectionError);
                            this.Log(LogLevel.Info, "Audio connection closed.");
                        }
                        break;
                    default:
                        this.Log(LogLevel.Warning,
                                 "Unexpected audio connection status received: {0}",
                                 statusData[0]);
                        break;
                }
            }
        }

        private void SetStatusFlag(StatusFlag flag)
        {
            lock (stateLock)
            {
                StatusFlag new_status = status | flag;

                if (new_status == status)
                    return;

                status = new_status;
                StatusPin.Unset();
            }
        }

        private void ClearStatusFlag(StatusFlag flag)
        {
            lock (stateLock)
            {
                status &= ~flag;
            }
        }

        private static int ParseInt32(IList<byte> bytes, ref int start)
        {
            int val = bytes[start];
            val |= bytes[start + 1] << 8;
            val |= bytes[start + 2] << 16;
            val |= bytes[start + 3] << 24;

            start += 4;
            return val;
        }

        private static int ParseInt32(IList<byte> bytes)
        {
            int start = 0;
            return ParseInt32(bytes, ref start);
        }

        private static int ParseInt16(IList<byte> bytes, ref int start)
        {
            int val = (bytes[start + 1] << 8) | bytes[start];
            start += 2;
            return val;
        }

        private static int ParseInt16(IList<byte> bytes)
        {
            int start = 0;
            return ParseInt16(bytes, ref start);
        }

        private static void GetBytesInt32(byte[] bytes, int val, ref int start)
        {
            bytes[start] = (byte) val;
            bytes[start + 1] = (byte) (val >> 8);
            bytes[start + 2] = (byte) (val >> 16);
            bytes[start + 3] = (byte) (val >> 24);

            start += 4;
        }

        private static void GetBytesInt32(byte[] bytes, int val)
        {
            int start = 0;
            GetBytesInt32(bytes, val, ref start);
        }

        private static void GetBytesInt16(byte[] bytes, int val, ref int start)
        {
            bytes[start] = (byte) val;
            bytes[start + 1] = (byte) (val >> 8);

            start += 2;
        }

        private static void GetBytesInt16(byte[] bytes, int val)
        {
            int start = 0;
            GetBytesInt16(bytes, val, ref start);
        }

        [field: Transient]
        public event Action<byte> CharReceived;

        public int IpAddress { get; }
        public int UdpServerPort { get; }
        public int UdpMessageMagic { get; }
        public int MinUdpMessageSize { get; }
        public int MaxUdpMessageSize { get; }
        public int DefaultAudioBlockSize { get; }
        public uint BaudRate { get; }
        public Bits StopBits { get; }
        public Parity ParityBit { get; }
        public GPIO AckPin { get; }
        public GPIO StatusPin { get; }

        private readonly WirelessInterface remoteClient;
        private readonly Transaction transaction;
        private readonly Queue<WirelessMessage> msgQueue;
        private readonly Queue<byte> audioBuffer;
        private readonly object stateLock;
        private StatusFlag status;
        private int audioBlockSize;
        private bool isConnectedToAp;
        private AudioState audioState;

        private enum Channel
        {
            Config,
            Control,
            Audio,
            AudioConnectionManagement
        }

        private enum AudioState
        {
            Disconnected,
            Connecting,
            Connected
        }

        [Flags]
        private enum StatusFlag
        {
            AudioSrcConnectionError = 1,
            HasMessage = 2,
            HasAudio = 4
        }

        private sealed class WirelessMessage
        {
            public sealed class Header
            {
                public Header(Channel channel, int msgSize, int srcAddr, int srcPort)
                {
                    Channel = channel;
                    MessageSize = msgSize;
                    SourceAddress = srcAddr;
                    SourcePort = srcPort;
                }

                public byte[] GetBytes()
                {
                    byte[] bytes = new byte[SIZE];

                    int curByte = 0;
                    GetBytesInt32(bytes, MessageSize, ref curByte);
                    GetBytesInt32(bytes, SourceAddress, ref curByte);
                    GetBytesInt16(bytes, SourcePort, ref curByte);
                    bytes[SIZE - 1] = (byte) Channel;

                    return bytes;
                }

                public static Header FromBytes(List<byte> bytes)
                {
                    int curByte = 0;
                    int msgSize = ParseInt32(bytes, ref curByte);
                    int srcAddr = ParseInt32(bytes, ref curByte);
                    int srcPort = ParseInt16(bytes, ref curByte);
                    Channel chan = (Channel) bytes[curByte];

                    return new Header(chan, msgSize, srcAddr, srcPort);
                }

                public Channel Channel { get; }
                public int MessageSize { get; }
                public int SourceAddress { get; }
                public int SourcePort { get; }

                public const int SIZE = 11;
            }

            public WirelessMessage(Header header, byte[] data)
            {
                MessageHeader = header;
                Data = data;
            }

            public WirelessMessage(Channel channel, int srcAddr, int srcPort, byte[] data)
            {
                MessageHeader = new Header(channel, data.Length, srcAddr, srcPort);
                Data = data;
            }

            public Header MessageHeader { get; }
            public byte[] Data { get; }
        }

        private sealed class WirelessInterface
        {
            public WirelessInterface(ESP32 esp)
            {
                this.esp = esp;
                this.rxLock = new object();
                this.txLock = new object();
                this.msgBuffer = new List<byte>();
                this.msgHeader = null;
            }

            public void WriteChar(byte value)
            {
                lock (rxLock)
                {
                    msgBuffer.Add(value);

                    if (msgHeader == null)
                    {
                       if (msgBuffer.Count == WirelessMessage.Header.SIZE)
                       {
                           msgHeader = WirelessMessage.Header.FromBytes(msgBuffer);
                           msgBuffer.Clear();
                       }
                    }
                    else if (msgBuffer.Count == msgHeader.MessageSize)
                    {
                        byte[] msgData = msgBuffer.ToArray();
                        WirelessMessage msg = new WirelessMessage(msgHeader, msgData);
                        esp.RemoteMessageReceived(msg);

                        msgBuffer.Clear();
                        msgHeader = null;
                    }
                }
            }

            public void TransmitMessage(WirelessMessage msg)
            {
                byte[] header = msg.MessageHeader.GetBytes();

                lock (txLock)
                {
                    foreach (byte b in header)
                        TransmitByte(b);

                    foreach (byte b in msg.Data)
                        TransmitByte(b);
                }
            }

            private void TransmitByte(byte character)
            {
                esp.CharReceived?.Invoke(character);
            }

            private readonly ESP32 esp;
            private readonly object rxLock, txLock;
            private readonly List<byte> msgBuffer;
            private WirelessMessage.Header msgHeader;
        }

        private sealed class Transaction
        {
            public Transaction(ESP32 esp)
            {
                this.esp = esp;
                this.stage = Stage.Request;
                this.buffer = new List<byte>();
            }

            public void Reset()
            {
                stage = Stage.Request;
                buffer.Clear();
            }

            public byte HandleByte(byte data)
            {
                byte tx = 0;

                switch (stage)
                {
                    case Stage.Request:
                        buffer.Add(data);

                        if (buffer.Count == 4)
                        {
                            int req = ParseInt32(buffer);
                            buffer.Clear();

                            if (ParseRequest(req) == 0)
                            {
                                stage = Stage.Data;

                                esp.AckPin.Unset();

                                esp.Log(LogLevel.Noisy,
                                        "Transaction request received: {0} - {1}",
                                        direction, address);
                            } else {
                                esp.Log(LogLevel.Warning,
                                        "Bad request: {0} - {1}",
                                        direction, address);
                            }
                        }

                        break;
                    case Stage.Data:
                        tx = HandleDataByte(data);

                        if (--dataLength == 0)
                        {
                            esp.Log(LogLevel.Noisy, "All transaction data transmitted.");

                            bool done = ProcessData();

                            if (done)
                                OnDataProcessed();
                            else
                                stage = Stage.Processing;
                        }
                        break;
                    case Stage.Processing:
                        esp.Log(LogLevel.Warning, "Transaction request when not ready.");
                        break;
                }

                return tx;
            }

            public void NotifyDataProcessed()
            {
                if (stage != Stage.Processing)
                {
                    esp.Log(LogLevel.Error,
                            "NotifiyDataProcessed() called in invalid stage: {0}",
                            stage);

                    return;
                }

                OnDataProcessed();
            }

            private void OnDataProcessed()
            {
                stage = Stage.Request;
                esp.AckPin.Set();

                esp.Log(LogLevel.Noisy, "Transaction completed.");
            }

            private int ParseRequest(int request)
            {
                if ((request & 0x80) == 0)
                    direction = Direction.Read;
                else
                    direction = Direction.Write;

                address = (Address) (request & ~0x80);

                switch (address)
                {
                    case Address.Status:
                        dataLength = 4;
                        break;
                    case Address.Message:
                        dataLength = esp.MaxUdpMessageSize;
                        break;
                    case Address.AudioBlockSize:
                        dataLength = 4;
                        break;
                    case Address.AudioSrc:
                        dataLength = 8;
                        break;
                    case Address.AudioData:
                        dataLength = esp.audioBlockSize;
                        break;
                    default:
                        esp.Log(LogLevel.Warning, "Undefined address: {0}", address);
                        return -1;
                }

                if (direction == Direction.Read)
                    return PrepareData();
                else
                    return CanWrite();
            }

            private int PrepareData()
            {
                switch (address)
                {
                    case Address.Status:
                        byte[] statusBytes = new byte[4];
                        GetBytesInt32(statusBytes, (int) esp.ReadStatus());
                        buffer.AddRange(statusBytes);
                        break;
                    case Address.Message:
                        byte[] msg = esp.ReadMessage();

                        if (msg == null)
                            return -1;

                        buffer.AddRange(msg);

                        for (int i = msg.Length; i < dataLength; ++i)
                            buffer.Add(0);

                        break;
                    case Address.AudioData:
                        int ret = esp.ReadAudioBlock(buffer);

                        if (ret != 0)
                            return -1;

                        break;
                    default:
                        return -1;
                }

                return 0;
            }

            private int CanWrite()
            {
                switch (address)
                {
                    case Address.Message:
                    case Address.AudioSrc:
                    case Address.AudioBlockSize:
                        break;
                    default:
                        return -1;
                }

                return 0;
            }

            private byte HandleDataByte(byte data)
            {
                if (direction == Direction.Read)
                {
                    return buffer[buffer.Count - dataLength];
                }
                else
                {
                    buffer.Add(data);
                    return 0;
                }
            }

            private bool ProcessData()
            {
                if (direction == Direction.Read)
                {
                    buffer.Clear();
                    return true;
                }

                bool done = false;

                switch (address)
                {
                    case Address.Message:
                        done = esp.ProcessMessage(buffer.ToArray());
                        break;
                    case Address.AudioSrc:
                        done = esp.ProcessAudioSrc(buffer.ToArray());
                        break;
                    case Address.AudioBlockSize:
                        int blockSize = ParseInt32(buffer);
                        done = esp.ProcessAudioBlockSize(blockSize);
                        break;
                    default:
                        esp.Log(LogLevel.Error, "Invalid write access: {0}", address);
                        break;
                }

                buffer.Clear();
                return done;
            }

            private readonly ESP32 esp;
            private readonly List<byte> buffer;
            private Stage stage;
            private Direction direction;
            private Address address;
            private int dataLength;

            private enum Stage
            {
                Request,
                Data,
                Processing
            }

            private enum Direction
            {
                Read,
                Write
            }

            private enum Address
            {
                Status,
                Message,
                AudioSrc,
                AudioData,
                AudioBlockSize
            }
        }

        private sealed class AudioSource : IEquatable<AudioSource>
        {
            public AudioSource(int address, int port)
            {
                Address = address;
                Port = port;
            }

            public byte[] ToBytes()
            {
                byte[] bytes = new byte[6];

                int curByte = 0;
                GetBytesInt32(bytes, Address, ref curByte);
                GetBytesInt16(bytes, Port, ref curByte);

                return bytes;
            }

            public override bool Equals(object obj)
            {
                return this.Equals(obj as AudioSource);
            }

            public bool Equals(AudioSource other)
            {
                if (Object.ReferenceEquals(other, null))
                    return false;

                if (Object.ReferenceEquals(this, other))
                    return true;

                if (this.GetType() != other.GetType())
                    return false;

                return (Address == other.Address) && (Port == other.Port);
            }

            public override int GetHashCode()
            {
                return Address * 0x00010000 + Port;
            }

            public static AudioSource FromBytes(byte[] bytes)
            {
                int curByte = 0;
                int addr = ParseInt32(bytes, ref curByte);
                int port = ParseInt16(bytes, ref curByte);

                return new AudioSource(addr, port);
            }

            public static bool operator==(AudioSource lhs, AudioSource rhs)
            {
                if (Object.ReferenceEquals(lhs, null))
                {
                    if (Object.ReferenceEquals(rhs, null))
                        return true;

                    return false;
                }

                return lhs.Equals(rhs);
            }

            public static bool operator!=(AudioSource lhs, AudioSource rhs)
            {
                return !(lhs == rhs);
            }

            public int Address { get; }
            public int Port { get; }
        }
    }
}
