using System;
using System.IO;
using System.Xml.Serialization;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.I2C;
using Antmicro.Renode.Peripherals.UART;
using Antmicro.Renode.Logging;
using Antmicro.Migrant;

namespace Antmicro.Renode.Peripherals.Sound
{
    /*
     * Cirrus Logic WM8994 audio codec.  
     *
     * Only some registers are implemented, they are those related to:
     *  - Software reset.
     *  - Write sequencer registers for speaker startup and shutdown sequences.
     *  - AIF1.
     *  - DAC1L/R.
     *  - SPKMIXL/R.
     *  - SPKOUTL/R.
     *  - Bias and VMID.
     *  - System clock.
     *
     * This peripheral includes a debug UART interface to enable the retrieval of the current codec configuration from
     * an external test script. The configuration is sent each time any byte is received from the UART interface.
     */
    public class WM8994 : II2CPeripheral, IUART, IProvidesRegisterCollection<WordRegisterCollection>
    {
        public WM8994(uint uartBaudRate, Bits uartStopBits, Parity uartParityBit)
        {
            BaudRate = uartBaudRate;
            StopBits = uartStopBits;
            ParityBit = uartParityBit;

            RegistersCollection = new WordRegisterCollection(this);
            DefineRegisters();

            Reset();
        }

        public void Reset()
        {
            RegistersCollection.Reset();

            readAddress = 0;
            actualAif1dac1lVol = aif1dac1lVol.Value;
            actualAif1dac1rVol = aif1dac1rVol.Value;
            actualDac1lVol = dac1lVol.Value;
            actualDac1rVol = dac1rVol.Value;
            actualSpkOutlVol = spkOutlVol.Value;
            actualSpkOutrVol = spkOutrVol.Value;
        }

        public void Write(byte[] data)
        {
            if (data.Length < 2)
            {
                this.Log(LogLevel.Error, "Invalid request received, {0} byte(s) ignored.", data.Length);
                return;
            }

            int addr = (data[0] << 8) | data[1];

            if (data.Length == 2)
            {
                /* Read request */
                readAddress = addr;
            }
            else
            {
                int i;

                /* Write request */
                for (i = 2; i < data.Length; i += 2)
                {
                    ushort val = (ushort) ((data[i] << 8) | data[i + 1]);
                    RegistersCollection.Write(addr, val);
                    ++addr;
                }

                if (i != data.Length)
                {
                    this.Log(LogLevel.Error,
                             "Invalid write at address {0}, single byte received for 16-bit register.", addr);
                }
            }
        }

        public byte[] Read(int count = 1)
        {
            int i;
            byte[] data = new byte[count];

            for (i = 0; i < count; i += 2)
            {
                ushort regValue = RegistersCollection.Read(readAddress);
                data[0] = (byte) (regValue >> 8);
                data[1] = (byte) regValue;
                ++readAddress;
            }

            if (i != count)
            {
                this.Log(LogLevel.Error,
                         "Invalid read at address {0}, receiver expect single byte but register is 16-bit.", readAddress);
            }

            return data;
        }

        public void FinishTransmission()
        {
            /* Empty */
        }

        public void WriteChar(byte value)
        {
            Config conf = new Config();

            conf.AnalogCircuitsPowered = biasEnable.Value &&
                                         vmidMode.Value == VmidMode.Normal;

            conf.Aif1ToSpkOutlEnabled = aif1dac1lEnable.Value &&
                                        aif1dac1lDac1lEnable.Value &&
                                        dac1lEnable.Value &&
                                        dac1lSpkMixlEnable.Value &&
                                        spkMixlSpkOutlEnable.Value &&
                                        spkOutlEnable.Value;

            conf.Aif1ToSpkOutrEnabled = aif1dac1rEnable.Value &&
                                        aif1dac1rDac1rEnable.Value &&
                                        dac1rEnable.Value &&
                                        dac1rSpkMixrEnable.Value &&
                                        spkMixrSpkOutrEnable.Value &&
                                        spkOutrEnable.Value;
            
            conf.DspClkEnabled = aif1ClkSource.Value == AifClockSource.MCLK1 &&
                                 sysClkSource.Value == SysClockSource.AIF1CLK &&
                                 aif1ClkEnable.Value &&
                                 aif1DspClkEnable.Value &&
                                 sysDspClkEnable.Value;

            conf.SpklAmpli = ComputeVolumeAmplification(
                                actualAif1dac1lVol, actualDac1lVol, spkMixlVol.Value,
                                spkMixlAttenuation.Value, actualSpkOutlVol);

            conf.SpkrAmpli = ComputeVolumeAmplification(
                                actualAif1dac1rVol, actualDac1rVol, spkMixrVol.Value,
                                spkMixrAttenuation.Value, actualSpkOutrVol);

            conf.SpklMuted = float.IsNegativeInfinity(conf.SpklAmpli) ||
                             aif1dac1Mute.Value ||
                             dac1lMute.Value ||
                             !spkOutlUnmute.Value;

            conf.SpkrMuted = float.IsNegativeInfinity(conf.SpkrAmpli) ||
                             aif1dac1Mute.Value ||
                             dac1rMute.Value ||
                             !spkOutrUnmute.Value;

            conf.MclkSamplingFreqRatio = GetMclkSamplingFrequencyRatio();
            conf.Resolution = GetResolution();
            conf.SampleRate = GetSampleRate();
            conf.AifFormat = (byte) aif1Format.Value;

            XmlSerializer serializer = new XmlSerializer(typeof(Config));
            string serializedConf;

            using (StringWriter writer = new StringWriter())
            {
                serializer.Serialize(writer, conf);
                serializedConf = writer.ToString();
            }

            for (int i = 0; i < serializedConf.Length; ++i)
                TransmitByte((byte) serializedConf[i]);
            
            TransmitByte((byte) '\0');
        }

        private void DefineRegisters()
        {
            Registers.Dac1lMixerRouting.Define16(this, 0x0000, "R1537")
                .WithFlag(0, out aif1dac1lDac1lEnable, name: "AIF1DAC1L_TO_DAC1L")
                .WithTaggedFlag("AIF1DAC2L_TO_DAC1L", 1)
                .WithTaggedFlag("AIF2DACL_TO_DAC1L", 2)
                .WithReservedBits(3, 1)
                .WithTaggedFlag("ADCL_TO_DAC1L", 4)
                .WithTaggedFlag("ADCR_TO_DAC1L", 5)
                .WithReservedBits(6, 10);
            
            Registers.Dac1rMixerRouting.Define16(this, 0x0000, "R1538")
                .WithFlag(0, out aif1dac1rDac1rEnable, name: "AIF1DAC1R_TO_DAC1R")
                .WithTaggedFlag("AIF1DAC2R_TO_DAC1R", 1)
                .WithTaggedFlag("AIF2DACR_TO_DAC1R", 2)
                .WithReservedBits(3, 1)
                .WithTaggedFlag("ADCL_TO_DAC1R", 4)
                .WithTaggedFlag("ADCR_TO_DAC1R", 5)
                .WithReservedBits(6, 10);

            Registers.Aif1Dac1lVolume.Define16(this, 0x00C0, "R1026")
                .WithValueField(0, 8, out aif1dac1lVol, name: "AIF1DAC1L_VOL")
                .WithFlag(8, FieldMode.Write, writeCallback: OnAif1Dac1VolUpdate, name: "AIF1DAC1_VU")
                .WithReservedBits(9, 7);
            
            Registers.Aif1Dac1rVolume.Define16(this, 0x00C0, "R1027")
                .WithValueField(0, 8, out aif1dac1rVol, name: "AIF1DAC1R_VOL")
                .WithFlag(8, FieldMode.Write, writeCallback: OnAif1Dac1VolUpdate, name: "AIF1DAC1_VU")
                .WithReservedBits(9, 7);
            
            Registers.Aif1Dac1Filters1.Define16(this, 0x0200, "R1056")
                .WithReservedBits(0, 1)
                .WithTag("AIF1DAC1_DEEMP", 1, 2)
                .WithReservedBits(3, 1)
                .WithTaggedFlag("AIF1DAC1_UNMUTE_RAMP", 4)
                .WithTaggedFlag("AIF1DAC1_MUTERATE", 5)
                .WithReservedBits(6, 1)
                .WithTaggedFlag("AIF1DAC1_MONO", 7)
                .WithReservedBits(8, 1)
                .WithFlag(9, out aif1dac1Mute, name: "AIF1DAC1_MUTE");

            Registers.PowerManagement5.Define16(this, 0x0000, "R5")
                .WithFlag(0, out dac1rEnable, name: "DAC1R_ENA")
                .WithFlag(1, out dac1lEnable, name: "DAC1L_ENA")
                .WithTaggedFlag("DAC2R_ENA", 2)
                .WithTaggedFlag("DAC2L_ENA", 3)
                .WithReservedBits(4, 4)
                .WithFlag(8, out aif1dac1rEnable, name: "AIF1DAC1R_ENA")
                .WithFlag(9, out aif1dac1lEnable, name: "AIF1DAC1L_ENA")
                .WithTaggedFlag("AIF1DAC2R_ENA", 10)
                .WithTaggedFlag("AIF1DAC2L_ENA", 11)
                .WithTaggedFlag("AIF2DACR_ENA", 12)
                .WithTaggedFlag("AIF2DACL_ENA", 13)
                .WithReservedBits(14, 2);
            
            Registers.Dac1lVolume.Define16(this, 0x02C0, "R1552")
                .WithValueField(0, 8, out dac1lVol, name: "DAC1L_VOL")
                .WithFlag(8, FieldMode.Write, writeCallback: OnDac1VolUpdate, name: "DAC1_VU")
                .WithFlag(9, out dac1lMute, name: "DAC1L_MUTE");
            
            Registers.Dac1rVolume.Define16(this, 0x02C0, "R1553")
                .WithValueField(0, 8, out dac1rVol, name: "DAC1R_VOL")
                .WithFlag(8, FieldMode.Write, writeCallback: OnDac1VolUpdate, name: "DAC1_VU")
                .WithFlag(9, out dac1rMute, name: "DAC1R_MUTE");

            Registers.PowerManagement1.Define16(this, 0x0000, "R1")
                .WithFlag(0, out biasEnable, name: "BIAS_ENA")
                .WithEnumField(1, 2, out vmidMode, name: "VMID_SEL")
                .WithReservedBits(3, 1)
                .WithTaggedFlag("MICB1_ENA", 4)
                .WithTaggedFlag("MICB2_ENA", 5)
                .WithReservedBits(6, 2)
                .WithTaggedFlag("HPOUT1R_ENA", 8)
                .WithTaggedFlag("HPOUT1L_ENA", 9)
                .WithReservedBits(10, 1)
                .WithTaggedFlag("HPOUT2_ENA", 11)
                .WithFlag(12, out spkOutlEnable, name: "SPKOUTL_ENA")
                .WithFlag(13, out spkOutrEnable, name: "SPKOUTR_ENA")
                .WithReservedBits(14, 2);

            Registers.SpkMixlAttenuation.Define16(this, 0x0003, "R34")
                .WithValueField(0, 2, out spkMixlVol, name: "SPKMIXL_VOL")
                .WithFlag(2, out spkMixlAttenuation, name: "DAC1L_SPKMIXL_VOL")
                .WithTaggedFlag("MIXOUTL_SPKMIXL_VOL", 3)
                .WithTaggedFlag("IN1LP_SPKMIXL_VOL", 4)
                .WithTaggedFlag("MIXINL_SPKMIXL_VOL", 5)
                .WithTaggedFlag("DAC2L_SPKMIXL_VOL", 6)
                .WithReservedBits(7, 1)
                .WithTaggedFlag("SPK_AB_REF_SEL", 8)
                .WithReservedBits(9, 7);
            
            Registers.SpkMixrAttenuation.Define16(this, 0x0003, "R35")
                .WithValueField(0, 2, out spkMixrVol, name: "SPKMIXR_VOL")
                .WithFlag(2, out spkMixrAttenuation, name: "DAC1R_SPKMIXR_VOL")
                .WithTaggedFlag("MIXOUTR_SPKMIXR_VOL", 3)
                .WithTaggedFlag("IN1RP_SPKMIXR_VOL", 4)
                .WithTaggedFlag("MIXINR_SPKMIXR_VOL", 5)
                .WithTaggedFlag("DAC2R_SPKMIXR_VOL", 6)
                .WithReservedBits(7, 1)
                .WithTaggedFlag("SPKOUT_CLASSAB", 8)
                .WithReservedBits(9, 7);
            
            Registers.SpeakerMixer.Define16(this, 0x0000, "R54")
                .WithFlag(0, out dac1rSpkMixrEnable, name: "DAC1R_TO_SPKMIXR")
                .WithFlag(1, out dac1lSpkMixlEnable, name: "DAC1L_TO_SPKMIXL")
                .WithTaggedFlag("MIXOUTR_TO_SPKMIXR", 2)
                .WithTaggedFlag("MIXOUTL_TO_SPKMIXL", 3)
                .WithTaggedFlag("IN1RP_TO_SPKMIXR", 4)
                .WithTaggedFlag("IN1LP_TO_SPKMIXL", 5)
                .WithTaggedFlag("MIXINR_TO_SPKMIXR", 6)
                .WithTaggedFlag("MIXINL_TO_SPKMIXL", 7)
                .WithTaggedFlag("DAC2R_TO_SPKMIXR", 8)
                .WithTaggedFlag("DAC2L_TO_SPKMIXL", 9)
                .WithReservedBits(10, 6);

            Registers.SpklVolume.Define16(this, 0x0079, "R38")
                .WithValueField(0, 6, out spkOutlVol, name: "SPKOUTL_VOL")
                .WithFlag(6, out spkOutlUnmute, name: "SPKOUTL_MUTE_N")
                .WithTaggedFlag("SPKOUTL_ZC", 7)
                .WithFlag(8, FieldMode.Write, writeCallback: OnSpkOutVolUpdate, name: "SPKOUT_VU");
            
            Registers.SpkrVolume.Define16(this, 0x0079, "R39")
                .WithValueField(0, 6, out spkOutrVol, name: "SPKOUTR_VOL")
                .WithFlag(6, out spkOutrUnmute, name: "SPKOUTL_MUTE_N")
                .WithTaggedFlag("SPKOUTL_ZC", 7)
                .WithFlag(8, FieldMode.Write, writeCallback: OnSpkOutVolUpdate, name: "SPKOUT_VU");

            Registers.SpkOutMixers.Define16(this, 0x0011, "R36")
                .WithFlag(0, out spkMixrSpkOutrEnable, name: "SPKMIXR_TO_SPKOUTR")
                .WithTaggedFlag("SPKMIXL_TO_SPKOUTR", 1)
                .WithTaggedFlag("IN2LRP_TO_SPKOUTR", 2)
                .WithTaggedFlag("SPKMIXR_TO_SPKOUTL", 3)
                .WithFlag(4, out spkMixlSpkOutlEnable, name: "SPKMIXL_TO_SPKOUTL")
                .WithTaggedFlag("IN2LRP_TO_SPKOUTL", 5)
                .WithReservedBits(6, 10);

            Registers.Aif1Control1.Define16(this, 0x4050, name: "R768")
                .WithReservedBits(0, 3)
                .WithEnumField(3, 2, out aif1Format, name: "AIF1_FMT")
                .WithEnumField(5, 2, out aif1WordLength, name: "AIF1_WL")
                .WithTaggedFlag("AIF1_LRCLK_INV", 7)
                .WithTaggedFlag("AIF1_BCLK_INV", 8)
                .WithReservedBits(9, 4)
                .WithTaggedFlag("AIF1ADC_TDM", 13)
                .WithTaggedFlag("AIF1ADCR_SRC", 14)
                .WithTaggedFlag("AIF1ADCL_SRC", 15);

            Registers.Aif1Control2.Define16(this, 0x4000, name: "R769")
                .WithTaggedFlag("AIF1_LOOPBACK", 0)
                .WithTaggedFlag("AIF1ADC_COMPMODE", 1)
                .WithTaggedFlag("AIF1ADC_COMP", 2)
                .WithFlag(3, out aif1DacCompandingMode, name: "AIF1DAC_COMPMODE")
                .WithFlag(4, out aif1DacCompandingEnable, name: "AIFDAC_COMP")
                .WithReservedBits(5, 3)
                .WithTaggedFlag("AIF1_MONO", 8)
                .WithReservedBits(9, 1)
                .WithTag("AIF1DAC_BOOST", 10, 2)
                .WithReservedBits(12, 2)
                .WithTaggedFlag("AIF1DACR_SRC", 14)
                .WithTaggedFlag("AIF1DACL_SRC", 15);
            
            Registers.Aif1Clocking1.Define16(this, 0x0000, name: "R512")
                .WithFlag(0, out aif1ClkEnable, name: "AIF1CLK_ENA")
                .WithFlag(1, out aif1ClkDivider, name: "AIFCLK_DIV")
                .WithTaggedFlag("AIF1CLK_INV", 2)
                .WithEnumField(3, 2, out aif1ClkSource, name: "AIF1CLK_SRC")
                .WithReservedBits(5, 11);
            
            Registers.Aif1Rate.Define16(this, 0x0083, name: "R528")
                .WithEnumField(0, 4, out aif1ClkRate, name: "AIF1CLK_RATE")
                .WithEnumField(4, 4, out aif1SampleRate, name: "AIF1_SR")
                .WithReservedBits(8, 8);

            Registers.Clocking1.Define16(this, 0x0000, name: "R520")
                .WithEnumField(0, 1, out sysClkSource, name: "SYSCLK_SRC")
                .WithFlag(1, out sysDspClkEnable, name: "SYSDSPCLK_ENA")
                .WithTaggedFlag("AIF2DSPCLK_ENA", 2)
                .WithFlag(3, out aif1DspClkEnable, name: "AIF1DSPCLK_ENA")
                .WithTaggedFlag("TOCLK_ENA", 4)
                .WithReservedBits(5, 11);
        
            Registers.WriteSequencerControl1.Define16(this, 0x0000, name: "R272")
                .WithValueField(0, 7, out wseqStartIndex, name: "WSEQ_START_INDEX")
                .WithReservedBits(7, 1)
                .WithFlag(8, out wseqStart, writeCallback: OnWseqStartWrite, name: "WSEQ_START")
                .WithFlag(9, valueProviderCallback: _ => false, writeCallback: OnWseqAbortWrite, name: "WSEQ_ABORT")
                .WithReservedBits(10, 5)
                .WithFlag(15, out wseqEnable, changeCallback: OnWseqEnableUpdate, name: "WSEQ_ENA");
            
            Registers.WriteSequencerControl2.Define16(this, 0x0000, name: "R273")
                .WithTag("WSEQ_CURRENT_INDEX", 0, 7)
                .WithReservedBits(7, 1)
                .WithFlag(8, FieldMode.Read, name: "WSEQ_BUSY")
                .WithReservedBits(9, 7);

            Registers.SoftwareReset.Define16(this, 0x8994, name: "R0")
                .WithValueField(0, 16, valueProviderCallback: _ => 0x8994, name: "SW_RESET")
                .WithWriteCallback((_, __) => Reset());

            Registers.Oversampling.Define16(this, 0x0002, name: "R1568")
                .WithFlag(0, name: "DAC_OSR128")
                .WithFlag(1, name: "ADC_OSR128")
                .WithReservedBits(2, 14);

            Registers.Errata1.Define16(this)
                .WithIgnoredBits(0, 16);

            Registers.Errata2.Define16(this)
                .WithIgnoredBits(0, 16);

            Registers.Errata3.Define16(this)
                .WithIgnoredBits(0, 16);
        }

        private void OnAif1Dac1VolUpdate(bool oldVal, bool newVal)
        {
            if (newVal)
            {
                actualAif1dac1lVol = aif1dac1lVol.Value;
                actualAif1dac1rVol = aif1dac1rVol.Value;
            }
        }

        private void OnDac1VolUpdate(bool oldVal, bool newVal)
        {
            if (newVal)
            {
                actualDac1lVol = dac1lVol.Value;
                actualDac1rVol = dac1rVol.Value;
            }
        }

        private void OnSpkOutVolUpdate(bool oldVal, bool newVal)
        {
            if (newVal)
            {
                actualSpkOutlVol = spkOutlVol.Value;
                actualSpkOutrVol = spkOutrVol.Value;
            }
        }

        private void OnWseqStartWrite(bool oldVal, bool newVal)
        {
            if (!newVal || !wseqEnable.Value)
                return;
            
            ExecuteWriteSequence(wseqStartIndex.Value);
        }

        private void OnWseqAbortWrite(bool oldVal, bool newVal)
        {
            if (newVal)
                wseqStart.Value = false;
        }

        private void OnWseqEnableUpdate(bool oldVal, bool newVal)
        {
            if (!newVal || !wseqStart.Value)
                return;

            ExecuteWriteSequence(wseqStartIndex.Value);
        }

        private void ExecuteWriteSequence(uint index)
        {
            switch (index)
            {
                case SPEAKER_STARTUP_SEQ_INDEX:
                    RunSpeakerStartupSequence();
                    break;
                case SPEAKER_SHUTDOWN_SEQ_INDEX:
                    RunSpeakerShutdownSequence();
                    break;
                default:
                    this.Log(LogLevel.Warning, 
                             "The specified write sequence is not implemented: {0}", wseqStartIndex.Value);
                    break;
            }

            wseqStart.Value = false;
        }

        private void RunSpeakerStartupSequence()
        {
            biasEnable.Value = true;
            vmidMode.Value = VmidMode.Normal;
            spkOutlEnable.Value = true;
            spkOutrEnable.Value = true;
        }

        private void RunSpeakerShutdownSequence()
        {
            spkOutlEnable.Value = false;
            spkOutrEnable.Value = false;
            vmidMode.Value = VmidMode.Standby;
            biasEnable.Value = false;
        }

        private float ComputeVolumeAmplification(uint aifVol, uint dacVol, uint mixVol, bool mixAttenuation, uint outVol)
        {
            float ampli = 0.0f;

            if (aifVol == 0)
                return float.NegativeInfinity;
            else if (aifVol < 0xC0)
                ampli += 0.375f * (aifVol - 0xC0);
            
            if (dacVol == 0)
                return float.NegativeInfinity;
            else if (dacVol < 0xC0)
                ampli += 0.375f * (dacVol - 0xC0);
            
            if (mixVol == 0b11)
                return float.NegativeInfinity;
            else
                ampli += -6.0f * mixVol;
            
            if (mixAttenuation)
                ampli += -3.0f;
            
            ampli += 6.0f - (0x3F - outVol);
            return ampli;
        }

        private ushort GetMclkSamplingFrequencyRatio()
        {
            ushort mclkSamplingFreqRatio;

            switch (aif1ClkRate.Value)
            {
                case AifClockRate.Rate128:
                    mclkSamplingFreqRatio = 128;
                    break;
                case AifClockRate.Rate192:
                    mclkSamplingFreqRatio = 192;
                    break;
                case AifClockRate.Rate256:
                    mclkSamplingFreqRatio = 256;
                    break;
                case AifClockRate.Rate384:
                    mclkSamplingFreqRatio = 384;
                    break;
                case AifClockRate.Rate512:
                    mclkSamplingFreqRatio = 512;
                    break;
                case AifClockRate.Rate768:
                    mclkSamplingFreqRatio = 768;
                    break;
                case AifClockRate.Rate1024:
                    mclkSamplingFreqRatio = 1024;
                    break;
                case AifClockRate.Rate1408:
                    mclkSamplingFreqRatio = 1408;
                    break;
                case AifClockRate.Rate1536:
                    mclkSamplingFreqRatio = 1536;
                    break;
                default:
                    this.Log(LogLevel.Error, "Invalid AIF1CLK rate: {0}", aif1ClkRate.Value);
                    mclkSamplingFreqRatio = 0;
                    break;
            }

            if (aif1ClkDivider.Value)
                mclkSamplingFreqRatio *= 2;

            return mclkSamplingFreqRatio;
        }

        private byte GetResolution()
        {
            if (!aif1DacCompandingEnable.Value && aif1DacCompandingMode.Value)
                return 8;
            
            switch (aif1WordLength.Value)
            {
                case AifWordLength.Bits16:
                    return 16;
                case AifWordLength.Bits20:
                    return 20;
                case AifWordLength.Bits24:
                    return 24;
                case AifWordLength.Bits32:
                    return 32;
                default:
                    this.Log(LogLevel.Error, "Invalid word length: {0}", aif1WordLength.Value);
                    return 0;
            }
        }

        private uint GetSampleRate()
        {
            switch (aif1SampleRate.Value)
            {
                case AifSampleRate.Rate8k:
                    return 8000;
                case AifSampleRate.Rate11k025:
                    return 11025;
                case AifSampleRate.Rate12k:
                    return 12000;
                case AifSampleRate.Rate16k:
                    return 16000;
                case AifSampleRate.Rate22k05:
                    return 22050;
                case AifSampleRate.Rate24k:
                    return 24000;
                case AifSampleRate.Rate32k:
                    return 32000;
                case AifSampleRate.Rate44k1:
                    return 44100;
                case AifSampleRate.Rate48k:
                    return 48000;
                case AifSampleRate.Rate88k2:
                    return 882000;
                case AifSampleRate.Rate96k:
                    return 96000;
                default:
                    this.Log(LogLevel.Error, "Invalid sample rate: {0}", aif1SampleRate.Value);
                    return 0; 
            }
        }

        private void TransmitByte(byte character)
        {
            CharReceived?.Invoke(character);
        }

        [field: Transient]
        public event Action<byte> CharReceived;

        public WordRegisterCollection RegistersCollection { get; }
        public uint BaudRate { get; }
        public Bits StopBits { get; }
        public Parity ParityBit { get; }

        private IValueRegisterField aif1dac1lVol;
        private IValueRegisterField aif1dac1rVol;
        private IValueRegisterField dac1lVol;
        private IValueRegisterField dac1rVol;
        private IValueRegisterField spkMixlVol;
        private IValueRegisterField spkMixrVol;
        private IValueRegisterField spkOutlVol;
        private IValueRegisterField spkOutrVol;
        private IValueRegisterField wseqStartIndex;
        private IEnumRegisterField<VmidMode> vmidMode;
        private IEnumRegisterField<AifFormat> aif1Format;
        private IEnumRegisterField<AifWordLength> aif1WordLength;
        private IEnumRegisterField<AifClockRate> aif1ClkRate;
        private IEnumRegisterField<AifSampleRate> aif1SampleRate;
        private IEnumRegisterField<AifClockSource> aif1ClkSource;
        private IEnumRegisterField<SysClockSource> sysClkSource;
        private IFlagRegisterField biasEnable;
        private IFlagRegisterField aif1dac1lEnable;
        private IFlagRegisterField aif1dac1rEnable;
        private IFlagRegisterField aif1dac1lDac1lEnable;
        private IFlagRegisterField aif1dac1rDac1rEnable;
        private IFlagRegisterField aif1DacCompandingMode;
        private IFlagRegisterField aif1DacCompandingEnable;
        private IFlagRegisterField aif1ClkEnable;
        private IFlagRegisterField aif1ClkDivider;
        private IFlagRegisterField aif1DspClkEnable;
        private IFlagRegisterField aif1dac1Mute;
        private IFlagRegisterField sysDspClkEnable;
        private IFlagRegisterField dac1lEnable;
        private IFlagRegisterField dac1rEnable;
        private IFlagRegisterField dac1lMute;
        private IFlagRegisterField dac1rMute;
        private IFlagRegisterField dac1lSpkMixlEnable;
        private IFlagRegisterField dac1rSpkMixrEnable;
        private IFlagRegisterField spkMixlSpkOutlEnable;
        private IFlagRegisterField spkMixrSpkOutrEnable;
        private IFlagRegisterField spkMixlAttenuation;
        private IFlagRegisterField spkMixrAttenuation;
        private IFlagRegisterField spkOutlEnable;
        private IFlagRegisterField spkOutrEnable;
        private IFlagRegisterField spkOutlUnmute;
        private IFlagRegisterField spkOutrUnmute;
        private IFlagRegisterField wseqStart;
        private IFlagRegisterField wseqEnable;

        private int readAddress;
        private uint actualAif1dac1lVol;
        private uint actualAif1dac1rVol;
        private uint actualDac1lVol;
        private uint actualDac1rVol;
        private uint actualSpkOutlVol;
        private uint actualSpkOutrVol;

        private const uint SPEAKER_STARTUP_SEQ_INDEX = 0x10;
        private const uint SPEAKER_SHUTDOWN_SEQ_INDEX = 0x22;

        private enum VmidMode
        {
            Disabled,
            Normal,
            Standby
        }

        private enum AifFormat
        {
            RightJustified = 0b00,
            LeftJustified = 0b01,
            I2sFormat = 0b10,
            DspMode = 0b11
        }

        private enum AifWordLength
        {
            Bits16 = 0b00,
            Bits20 = 0b01,
            Bits24 = 0b10,
            Bits32 = 0b11
        }

        private enum AifClockSource
        {
            MCLK1 = 0b00,
            MCLK2 = 0b01,
            FLL1 = 0b10,
            FLL2 = 0b11
        }

        private enum AifClockRate
        {
            Rate128 = 0b0001,
            Rate192 = 0b0010,
            Rate256 = 0b0011,
            Rate384 = 0b0100,
            Rate512 = 0b0101,
            Rate768 = 0b0110,
            Rate1024 = 0b0111,
            Rate1408 = 0b1000,
            Rate1536 = 0b1001
        }

        private enum AifSampleRate
        {
            Rate8k = 0b0000,
            Rate11k025 = 0b0001,
            Rate12k = 0b0010,
            Rate16k = 0b0011,
            Rate22k05 = 0b0100,
            Rate24k = 0b0101,
            Rate32k = 0b0110,
            Rate44k1 = 0b0111,
            Rate48k = 0b1000,
            Rate88k2 = 0b1001,
            Rate96k = 0b1010
        }

        private enum SysClockSource
        {
            AIF1CLK = 0,
            AIF2CLK = 1
        }
        
        private enum Registers
        {
            SoftwareReset = 0x0000,
            PowerManagement1 = 0x0001,
            PowerManagement5 = 0x0005,
            SpkMixlAttenuation = 0x0022,
            SpkMixrAttenuation = 0x0023,
            SpkOutMixers = 0x0024,
            SpklVolume = 0x0026,
            SpkrVolume = 0x0027,
            SpeakerMixer = 0x0036,
            WriteSequencerControl1 = 0x0110,
            WriteSequencerControl2 = 0x0111,
            Aif1Clocking1 = 0x0200,
            Clocking1 = 0x0208,
            Aif1Rate = 0x0210,
            Aif1Control1 = 0x0300,
            Aif1Control2 = 0x0301,
            Aif1Dac1lVolume = 0x0402,
            Aif1Dac1rVolume = 0x0403,
            Aif1Dac1Filters1 = 0x0420,
            Dac1lMixerRouting = 0x0601,
            Dac1rMixerRouting = 0x0602,
            Dac1lVolume = 0x0610,
            Dac1rVolume = 0x0611,
            Oversampling = 0x0620,

            /* WM8994 rev C errata work-around registers, content unknown */
            Errata1 = 0x0056,
            Errata2 = 0x0102,
            Errata3 = 0x0817
        }

        public sealed class Config
        {
            public Config()
            {
                /* Empty */
            }

            public bool AnalogCircuitsPowered { get; set; }
            public bool Aif1ToSpkOutlEnabled { get; set; }
            public bool Aif1ToSpkOutrEnabled { get; set; }
            public bool DspClkEnabled { get; set; }
            public bool SpklMuted { get; set; }
            public bool SpkrMuted { get; set; }
            public ushort MclkSamplingFreqRatio { get; set; }
            public float SpklAmpli { get; set; }
            public float SpkrAmpli { get; set; } 
            public uint SampleRate { get; set; } 
            public byte AifFormat { get; set; }
            public byte Resolution { get; set; }
        }
    }
}
