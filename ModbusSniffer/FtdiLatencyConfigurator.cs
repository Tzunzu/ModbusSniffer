using System.Runtime.InteropServices;

namespace ModbusSniffer;

// The FTDI USB serial driver re-applies its registry LatencyTimer default (16 ms)
// every time the device enumerates - a reboot, a replug, or moving to another USB
// port. That 16 ms batching window hides the sub-frame timing this sniffer tries
// to observe. Rather than depend on a persistent registry edit, the latency timer
// is set through the D2XX API on every run, before the COM port is opened.
//
// FTD2XX.dll ships with the FTDI driver. If it is missing (a non-FTDI adapter, or
// the D2XX runtime was not installed) the call is skipped and capture continues
// at whatever latency the driver chose.
internal static class FtdiLatencyConfigurator
{
    private const uint FtOk = 0;

    public static string Apply(string portName, int latencyMilliseconds)
    {
        if (latencyMilliseconds <= 0)
        {
            return "FTDI latency timer left unchanged (FtdiLatencyTimerMilliseconds=0).";
        }

        if (!OperatingSystem.IsWindows())
        {
            return "FTDI latency timer can only be programmed on Windows; left unchanged.";
        }

        byte requested = (byte)Math.Clamp(latencyMilliseconds, 1, 255);
        try
        {
            uint deviceCount = 0;
            if (FT_CreateDeviceInfoList(ref deviceCount) != FtOk || deviceCount == 0)
            {
                return "No FTDI device found; latency timer unchanged.";
            }

            for (uint index = 0; index < deviceCount; index++)
            {
                nint handle = 0;
                if (FT_Open(index, ref handle) != FtOk || handle == 0)
                {
                    continue;
                }

                try
                {
                    int comPortNumber = -1;
                    if (FT_GetComPortNumber(handle, ref comPortNumber) != FtOk ||
                        !string.Equals($"COM{comPortNumber}", portName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    uint status = FT_SetLatencyTimer(handle, requested);
                    if (status != FtOk && requested < 2)
                    {
                        // Some driver versions reject 1 ms and document a 2 ms
                        // floor; 2 ms is still far better than the 16 ms default.
                        requested = 2;
                        status = FT_SetLatencyTimer(handle, requested);
                    }

                    byte applied = 0;
                    FT_GetLatencyTimer(handle, ref applied);
                    return status == FtOk
                        ? $"FTDI latency timer on {portName} set to {applied} ms."
                        : $"FTDI latency timer on {portName} unchanged (driver status {status}); currently {applied} ms.";
                }
                finally
                {
                    FT_Close(handle);
                }
            }

            return $"{portName} is not an FTDI device; latency timer unchanged.";
        }
        catch (DllNotFoundException)
        {
            return "FTD2XX.dll not available; install the FTDI D2XX runtime to control the latency timer.";
        }
        catch (EntryPointNotFoundException)
        {
            return "Installed FTD2XX.dll lacks the latency-timer entry points; latency timer unchanged.";
        }
    }

    [DllImport("FTD2XX.dll")]
    private static extern uint FT_CreateDeviceInfoList(ref uint numDevices);

    [DllImport("FTD2XX.dll")]
    private static extern uint FT_Open(uint deviceNumber, ref nint ftHandle);

    [DllImport("FTD2XX.dll")]
    private static extern uint FT_Close(nint ftHandle);

    [DllImport("FTD2XX.dll")]
    private static extern uint FT_GetComPortNumber(nint ftHandle, ref int comPortNumber);

    [DllImport("FTD2XX.dll")]
    private static extern uint FT_SetLatencyTimer(nint ftHandle, byte latency);

    [DllImport("FTD2XX.dll")]
    private static extern uint FT_GetLatencyTimer(nint ftHandle, ref byte latency);
}
