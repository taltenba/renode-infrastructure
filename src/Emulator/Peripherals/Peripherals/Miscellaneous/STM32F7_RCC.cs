using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Logging;
using Antmicro.Migrant;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    /*
     * STM32F746NG RCC peripheral.
     *
     * Only some registers are implemented.
     */
    public class STM32F7_RCC : BasicDoubleWordPeripheral, IKnownSize
    {
        public STM32F7_RCC(Machine machine) : base(machine)
        {
            DefineRegisters();
        }

        public void DefineRegisters()
        {
            Registers.ClockControl.Define(this, 0x00000083, name: "RCC_CR")
                .WithFlag(0, out var hsion, name: "HSION")
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => hsion.Value, name: "HSIRDY")
                .WithReservedBits(2, 1)
                .WithValueField(3, 5, name: "HSITRIM")
                .WithTag("HSICAL", 8, 8)
                .WithFlag(16, out var hseon, name: "HSEON")
                .WithFlag(17, FieldMode.Read, valueProviderCallback: _ => hseon.Value, name: "HSERDY")
                .WithTag("HSEBYP", 18, 1)
                .WithTag("CSSON", 19, 1)
                .WithReservedBits(20, 4)
                .WithFlag(24, out var pllon, name: "PLLON")
                .WithFlag(25, FieldMode.Read, valueProviderCallback: _ => pllon.Value, name: "PLLRDY")
                .WithFlag(26, out var plli2son, name: "PLLI2SON")
                .WithFlag(27, FieldMode.Read, valueProviderCallback: _ => plli2son.Value, name: "PLLI2SRDY")
                .WithFlag(28, out var pllsaion, name: "PLLSAION")
                .WithFlag(29, FieldMode.Read, valueProviderCallback: _ => pllsaion.Value, name: "PLLSAIRDY")
                .WithReservedBits(30, 2);
            
            Registers.PllConfiguration.Define(this, 0x24003010, name: "RCC_PLLCFGR")
                .WithValueField(0, 6, name: "PLLM")
                .WithValueField(6, 9, name: "PLLN")
                .WithReservedBits(15, 1)
                .WithValueField(16, 2, name: "PLLP")
                .WithReservedBits(18, 4)
                .WithValueField(22, 1, name: "PLLSRC")
                .WithReservedBits(23, 1)
                .WithValueField(24, 4, name: "PLLQ")
                .WithReservedBits(28, 4);
            
            Registers.ClockConfiguration.Define(this, 0x0000000, name: "RCC_CFGR")
                .WithValueField(0, 2, out var systemClockSwitch, name: "SW")
                .WithValueField(2, 2, FieldMode.Read, name: "SWS", valueProviderCallback: _ => systemClockSwitch.Value)
                .WithValueField(4, 4, name: "HPRE")
                .WithReservedBits(8, 2)
                .WithValueField(10, 3, name: "PPRE1")
                .WithValueField(13, 3, name: "PPRE2")
                .WithValueField(16, 5, name: "RTCPRE")
                .WithValueField(21, 2, name: "MCO1")
                .WithValueField(23, 1, name: "I2SSCR")
                .WithValueField(24, 3, name: "MCO1PRE")
                .WithValueField(27, 3, name: "MCO2PRE")
                .WithValueField(30, 2, name: "MCO2");
            
            Registers.Ahb1PeripheralClockEnable.Define(this, 0x00100000, name: "RCC_AHB1ENR")
                .WithValueField(0, 11, name: "GPIOxEN")
                .WithReservedBits(11, 1)
                .WithFlag(12, name: "CRCEN")
                .WithReservedBits(13, 5)
                .WithFlag(18, name: "BKPSRAMEN")
                .WithReservedBits(19, 1)
                .WithFlag(20, name: "DTCMRAMEN")
                .WithValueField(21, 3, name: "DMAxEN")
                .WithReservedBits(24, 1)
                .WithValueField(25, 4, name: "ETHMACxEN")
                .WithValueField(29, 2, name: "OTGHSxEN")
                .WithReservedBits(31, 1);
            
            Registers.Ahb2PeripheralClockEnable.Define(this, 0x00000000, name: "RCC_AHB2ENR")
                .WithFlag(0, name: "DCMIEN")
                .WithReservedBits(1, 3)
                .WithFlag(4, name: "CRYPEN")
                .WithFlag(5, name: "HASHEN")
                .WithFlag(6, name: "RNGEN")
                .WithFlag(7, name: "OTGFSEN")
                .WithReservedBits(8, 24);

            Registers.Ahb3PeripheralClockEnable.Define(this, 0x00000000, name: "RCC_AHB3ENR")
                .WithFlag(0, name: "FMCEN")
                .WithFlag(1, name: "QSPIEN")
                .WithReservedBits(2, 30);
            
            Registers.Apb1PeripheralClockEnable.Define(this, 0x00000000, name: "RCC_APB1ENR")
                .WithValueField(0, 9, name: "TIMxEN")
                .WithFlag(9, name: "LPTIM1EN")
                .WithReservedBits(10, 1)
                .WithFlag(11, name: "WWDGEN")
                .WithReservedBits(12, 2)
                .WithValueField(14, 2, name: "SPIxEN")
                .WithFlag(16, name: "SPDIFRXEN")
                .WithValueField(17, 2, name: "USARTxEN")
                .WithValueField(19, 2, name: "UARTxEN")
                .WithValueField(21, 4, name: "I2CxEN")
                .WithValueField(25, 2, name: "CANxEN")
                .WithFlag(27, name: "CECEN")
                .WithFlag(28, name: "PWREN")
                .WithFlag(29, name: "DACEN")
                .WithValueField(30, 2, name: "UARTxEN");
            
            Registers.Apb2PeripheralClockEnable.Define(this, 0x00000000, name: "RCC_APB2ENR")
                .WithValueField(0, 2, name: "TIMxEN")
                .WithReservedBits(2, 2)
                .WithValueField(4, 2, name: "USARTxEN")
                .WithReservedBits(6, 2)
                .WithValueField(8, 3, name: "ADCxEN")
                .WithFlag(11, name: "SDMMC1EN")
                .WithValueField(12, 2, name: "SPIxEN")
                .WithFlag(14, name: "SYSCFGEN")
                .WithReservedBits(15, 1)
                .WithValueField(16, 3, name: "TIMxEN")
                .WithReservedBits(19, 1)
                .WithValueField(20, 2, name: "SPIxEN")
                .WithValueField(22, 2, name: "SAIxEN")
                .WithReservedBits(24, 2)
                .WithFlag(26, name: "LTDCEN")
                .WithReservedBits(27, 5);
            
            Registers.PllI2sConfiguration.Define(this, 0x24003000, name: "RCC_PLLI2SCFGR")
                .WithReservedBits(0, 6)
                .WithValueField(6, 9, name: "PLLI2SN")
                .WithReservedBits(15, 1)
                .WithValueField(16, 2, name: "PLLI2SP")
                .WithReservedBits(18, 6)
                .WithValueField(24, 4, name: "PLLI2SQ")
                .WithValueField(28, 3, name: "PLLI2SR")
                .WithReservedBits(31, 1);
            
            Registers.PllSaiConfiguration.Define(this, 0x24003000, name: "RCC_PLLSAICFGR")
                .WithReservedBits(0, 6)
                .WithValueField(6, 9, name: "PLLSAIN")
                .WithReservedBits(15, 1)
                .WithValueField(16, 2, name: "PLLSAIP")
                .WithReservedBits(18, 6)
                .WithValueField(24, 4, name: "PLLSAIQ")
                .WithValueField(28, 3, name: "PLLSAIR")
                .WithReservedBits(31, 1);
            
            Registers.DedicatedClockConfiguration1.Define(this, 0x00000000, name: "RCC_DCKCFGR1")
                .WithValueField(0, 5, name: "PLLI2SDIVQ")
                .WithReservedBits(5, 3)
                .WithValueField(8, 5, name: "PLLSAIDIVQ")
                .WithReservedBits(13, 3)
                .WithValueField(16, 2, name: "PLLSAIDIVR")
                .WithReservedBits(18, 2)
                .WithValueField(20, 2, name: "SAI1ASRC")
                .WithValueField(22, 2, name: "SAI1BSRC")
                .WithFlag(24, name: "TIMPRE")
                .WithReservedBits(25, 7);
            
            Registers.DedicatedClockConfiguration2.Define(this, 0x00000000, name: "RCC_DCKCFGR2")
                .WithValueField(0, 16, name: "UARTxSEL")
                .WithValueField(16, 8, name: "I2CxSEL")
                .WithValueField(24, 2, name: "LPTIM1SEL")
                .WithFlag(26, name: "CECSEL")
                .WithFlag(27, name: "CK48MSEL")
                .WithFlag(28, name: "SDMMC1SEL")
                .WithReservedBits(29, 3);
        }

        public long Size => 0x400;

        private enum Registers
        {
            ClockControl = 0x0,
            PllConfiguration = 0x4,
            ClockConfiguration = 0x8,
            Ahb1PeripheralClockEnable = 0x30,
            Ahb2PeripheralClockEnable = 0x34,
            Ahb3PeripheralClockEnable = 0x38,
            Apb1PeripheralClockEnable = 0x40,
            Apb2PeripheralClockEnable = 0x44,
            PllI2sConfiguration = 0x84,
            PllSaiConfiguration = 0x88,
            DedicatedClockConfiguration1 = 0x8C,
            DedicatedClockConfiguration2 = 0x90
        }
    }
}