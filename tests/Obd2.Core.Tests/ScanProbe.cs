using Obd2.Core.Obd;
using Obd2.Core.Transport;

namespace Obd2.Core.Tests;

public class ScanProbe
{
    [Fact]
    public void DemoScanFindsTheSimulatedEcus()
    {
        var t = new SimulatedElmTransport();
        t.Open();
        using var obd = new ObdService(t);
        obd.Initialize(6);
        var ids = Enumerable.Range(0x7E0, 8).Concat(Enumerable.Range(0x7B0, 8)).Concat(Enumerable.Range(0x7C0, 8));
        var found = obd.ScanEcus(ids);
        Assert.Equal(3, found.Count);
        Assert.Equal(SimulatedElmTransport.SimulatedVin, found[0].Fields["VIN"]);
    }
}
