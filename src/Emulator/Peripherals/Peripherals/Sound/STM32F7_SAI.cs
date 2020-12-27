using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.UART;
using Antmicro.Renode.Logging;
using Antmicro.Migrant;

namespace Antmicro.Renode.Peripherals.Sound
{
    /*
     * SAI peripheral of STM32F7 MCUs.
     *
     * Only Block A and master transmitter mode is implemented.
     *
     * This peripheral includes a UART interface to simulate the audio output interface.
     */
    [AllowedTranslations(AllowedTranslation.WordToDoubleWord | AllowedTranslation.ByteToDoubleWord)]
    public class STM32F7_SAI : BasicDoubleWordPeripheral, IKnownSize, IUART
    {
        public STM32F7_SAI(Machine machine, uint uartBaudRate, Bits uartStopBits, Parity uartParityBit) : base(machine)
        {
            BaudRate = uartBaudRate;
            StopBits = uartStopBits;
            ParityBit = uartParityBit;

            fifo = new Queue<uint>(INTERNAL_FIFO_SIZE);
            lastSlotData = new uint[2];
            txEnabled = false;

            DefineRegisters();
        }

        public override void Reset()
        {
            base.Reset();

            fifo.Clear();
            txEnabled = false;

            for (int i = 0; i < lastSlotData.Length; ++i)
                lastSlotData[i] = 0;
        }

        public void WriteChar(byte value)
        {
            /* Ignore data received through UART */
        }

        private void DefineRegisters()
        {
            Registers.Control1.Define(this, 0x00000040, "SAI_ACR1")
                .WithEnumField(0, 2, out mode, name: "MODE")
                .WithEnumField(2, 2, out protocolConfig, name: "PRTCFG")
                .WithReservedBits(4, 1)
                .WithEnumField(5, 3, out dataSize, name: "DS")
                .WithFlag(8, out lsbFirst, name: "LSBFIRST")
                .WithFlag(9, name: "CKSTR")
                .WithTag("SYNCEN", 10, 2)
                .WithFlag(12, out mono, name: "MONO")
                .WithFlag(13, name: "OUTDRIV")
                .WithReservedBits(14, 2)
                .WithFlag(16, out enable, changeCallback: OnEnableChange, name: "SAIEN")
                .WithFlag(17, name: "DMAEN")
                .WithReservedBits(18, 1)
                .WithFlag(19, name: "NODIV")
                .WithValueField(20, 4, name: "MCKDIV")
                .WithReservedBits(24, 8);
            
            Registers.Control2.Define(this, 0x00000000, "SAI_ACR2")
                .WithValueField(0, 3, name: "FTH")
                .WithFlag(3, FieldMode.Write, writeCallback: OnFlushFifoWrite, name: "FFLUSH")
                .WithFlag(4, name: "TRIS")
                .WithFlag(5, out mute, name: "MUTE")
                .WithEnumField(6, 1, out muteVal, name: "MUTEVAL")
                .WithTag("MUTECNT", 7, 6)
                .WithTaggedFlag("CPL", 13)
                .WithTag("COMP", 14, 2)
                .WithReservedBits(16, 16);

            Registers.FrameConfiguration.Define(this, 0x00000007, "SAI_AFRCR")
                .WithValueField(0, 8, out frameLength, name: "FRL")
                .WithValueField(8, 7, name: "FSALL")
                .WithReservedBits(15, 1)
                .WithFlag(16, name: "FSDEF")
                .WithFlag(17, name: "FSPOL")
                .WithFlag(18, name: "FSOFF")
                .WithReservedBits(19, 13);

            Registers.Slot.Define(this, 0x00000000, "SAI_ASLOTR")
                .WithValueField(0, 5, out firstBitOffset, name: "FBOFF")
                .WithReservedBits(5, 1)
                .WithEnumField(6, 2, out slotSize, name: "SLOTSZ")
                .WithValueField(8, 4, out slotCount, name: "NBSLOT")
                .WithReservedBits(12, 4)
                .WithValueField(16, 16, out slotEnable, name: "SLOTEN");
            
            Registers.Data.Define(this, 0x00000000, "SAI_ADR")
                .WithValueField(0, 32, valueProviderCallback: _ => 0, name: "DATA")
                .WithWriteCallback(OnDataWritten);
        }

        private void OnEnableChange(bool oldValue, bool newValue)
        {
            if (!newValue)
                txEnabled = false;
            else
                EnableSai();
        }

        private void OnFlushFifoWrite(bool oldValue, bool newValue)
        {
            if (!newValue)
                return;
            
            if (enable.Value)
            {
                this.Log(LogLevel.Warning, "Cannot flush FIFO when SAI is enabled.");
                return;
            }

            fifo.Clear();
        }

        private void OnDataWritten(uint oldVal, uint newVal)
        {
            if (mode.Value != Mode.MasterTransmitter && mode.Value != Mode.SlaveTransmitter)
            {
                this.Log(LogLevel.Warning,
                         "SAI not configured in transmitter mode, written data ignored.");
                return;
            }

            if (!txEnabled)
            {
                if (fifo.Count == INTERNAL_FIFO_SIZE)
                    this.Log(LogLevel.Warning, "Internal FIFO is full, written data ignored.");
                else
                    fifo.Enqueue(newVal);
                
                return;
            }

            HandleData(newVal);
        }

        private void EnableSai()
        {
            if (mode.Value != Mode.MasterTransmitter)
            {
                this.Log(LogLevel.Warning,
                    "Chosen SAI mode is not supported, only master transmitter mode is implemented.");
                return;
            }

            if (protocolConfig.Value != ProtocolConfiguration.Free)
            {
                this.Log(LogLevel.Warning,
                    "Chosen protocol is not supported, only free protocol is implemented.");
                return;
            }

            switch (dataSize.Value)
            {
                case DataSize.Bits8:
                    dataMask = 0xFF;
                    slotByteCount = 1;
                    break;
                case DataSize.Bits10:
                    dataMask = 0x3FF;
                    slotByteCount = 2;
                    break;
                case DataSize.Bits16:
                    dataMask = 0xFFFF;
                    slotByteCount = 2;
                    break;
                case DataSize.Bits20:
                    dataMask = 0xFFFFF;
                    slotByteCount = 3;
                    break;
                case DataSize.Bits24:
                    dataMask = 0xFFFFFF;
                    slotByteCount = 3;
                    break;
                case DataSize.Bits32:
                    dataMask = 0xFFFFFFFF;
                    slotByteCount = 4;
                    break;
                default:
                    this.Log(LogLevel.Error, "Invalid data size: {0}", dataSize.Value);
                    return;
            }

            switch (slotSize.Value)
            {
                case SlotSize.DataSize:
                    break;
                case SlotSize.HalfWord:
                    slotByteCount = 2;
                    break;
                case SlotSize.Word:
                    slotByteCount = 4;
                    break;
                default:
                    this.Log(LogLevel.Error, "Invalid slot size: {0}", slotSize.Value);
                    break;
            }

            int frameBitCount = (int) frameLength.Value + 1;
            frameByteCount = (frameBitCount + 7) / 8;
            remainingFrameByteCount = frameByteCount;

            curSlot = 0;
            txEnabled = true;

            while (fifo.Count != 0)
                HandleData(fifo.Dequeue());
        }

        private void HandleData(uint slotData)
        {
            if (curSlot >= (slotCount.Value + 1) || (slotEnable.Value & (1 << curSlot)) == 0)
            {
                slotData = 0;
            }
            else if (mute.Value)
            {
                if (muteVal.Value == MuteVal.Zero || slotCount.Value > 1)
                    slotData = 0;
                else
                    slotData = lastSlotData[curSlot];
            }

            if (slotCount.Value <= 1)
                lastSlotData[curSlot] = slotData;
            
            TransmitSlot(slotData);
            remainingFrameByteCount -= slotByteCount;

            if (mono.Value && slotCount.Value == 1 && remainingFrameByteCount >= 8)
            {
                TransmitSlot(slotData);
                remainingFrameByteCount -= slotByteCount;
                ++curSlot;
            }
            
            if (remainingFrameByteCount <= 0)
            {
                curSlot = 0;
                remainingFrameByteCount = frameByteCount;
            }
            else
            {
                ++curSlot;
            }
        }

        private void TransmitSlot(uint slotData)
        {
            slotData &= dataMask;
            slotData <<= (int) firstBitOffset.Value;

            if (lsbFirst.Value)
            {
                for (int i = 0; i < slotByteCount; ++i)
                {
                    TransmitByte((byte) slotData);
                    slotData >>= 8;
                }
            }
            else
            {
                slotData <<= (4 - slotByteCount);

                for (int i = 0; i < slotByteCount; ++i)
                {
                    TransmitByte((byte) (slotData >> 24));
                    slotData <<= 8;
                }
            }
        }

        private void TransmitByte(byte character)
        {
            CharReceived?.Invoke(character);
        }

        [field: Transient]
        public event Action<byte> CharReceived;

        public long Size => 0x400;
        public uint BaudRate { get; }
        public Bits StopBits { get; }
        public Parity ParityBit { get; }

        private IEnumRegisterField<Mode> mode;
        private IEnumRegisterField<DataSize> dataSize;
        private IEnumRegisterField<SlotSize> slotSize;
        private IEnumRegisterField<ProtocolConfiguration> protocolConfig;
        private IEnumRegisterField<MuteVal> muteVal;
        private IValueRegisterField frameLength;
        private IValueRegisterField firstBitOffset;
        private IValueRegisterField slotCount;
        private IValueRegisterField slotEnable;
        private IFlagRegisterField lsbFirst;
        private IFlagRegisterField mono;
        private IFlagRegisterField enable;
        private IFlagRegisterField mute;
        
        private readonly Queue<uint> fifo;
        private readonly uint[] lastSlotData;
        private bool txEnabled;
        private uint dataMask;
        private int frameByteCount;
        private int remainingFrameByteCount;
        private int slotByteCount;
        private int curSlot;

        private const int INTERNAL_FIFO_SIZE = 8;

        private enum Mode
        {
            MasterTransmitter = 0,
            MasterReceiver = 1,
            SlaveTransmitter = 2,
            SlaveReceiver = 3
        }

        private enum ProtocolConfiguration
        {
            Free = 0,
            Spdif = 1,
            Ac97 = 2
        }

        private enum DataSize
        {
            Bits8 = 0b010,
            Bits10 = 0b011,
            Bits16 = 0b100,
            Bits20 = 0b101,
            Bits24 = 0b110,
            Bits32 = 0b111
        }

        private enum SlotSize
        {
            DataSize = 0b00,
            HalfWord = 0b01,
            Word = 0b10
        }

        private enum MuteVal
        {
            Zero,
            LastValue
        }

        private enum Registers
        {
            Control1 = 0x04,               /* CR1 */
            Control2 = 0x08,               /* CR2 */
            FrameConfiguration = 0x0C,     /* FRCR */
            Slot = 0x10,                   /* SLOTR */
            Data = 0x20                    /* DR */
        }
    }
}
