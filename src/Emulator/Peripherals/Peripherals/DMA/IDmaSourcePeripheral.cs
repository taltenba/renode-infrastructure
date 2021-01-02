using System;

namespace Antmicro.Renode.Peripherals.DMA
{
    public interface IDmaSourcePeripheral : IPeripheral
    {
        event Action<IDmaSourcePeripheral> DmaDataReady;
        byte[] DmaGetData(uint size);

        ulong DataRegOffset { get; }
    }
}