using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ModbusSniffer;

internal class Program
{
    private const string ConfigurationFileName = "ModbusSniffer.ini";
    private const int ConsoleBufferWidth = 16384;
    private const string LogDirectoryName = "log";
    private const string LogFilePrefix = "ModbusSniffer_";
    private const string SummaryFilePrefix = "ModbusSniffer.summary_";
    private const int StandardOutputHandle = -11;
    private const uint EnableWrapAtEndOfLineOutput = 0x0002;
    private const int ReadBufferSize = 4096;
    private static readonly List<CaptureRecord> captureRecords = [];

    private static int Main()
    {
        ConfigureConsoleBuffer();
        DisableConsoleWrapping();
        captureRecords.Clear();

        ModbusSettings settings = ModbusSettings.Load(Path.Combine(AppContext.BaseDirectory, ConfigurationFileName));
        DateTimeOffset sessionStartedAt = DateTimeOffset.Now;
        string logDirectoryPath = Path.Combine(AppContext.BaseDirectory, LogDirectoryName);
        Directory.CreateDirectory(logDirectoryPath);
        string logFilePath = Path.Combine(logDirectoryPath, $"{LogFilePrefix}{sessionStartedAt:yyyy-MM-dd_HH-mm-ss}.log");
        string summaryFilePath = Path.Combine(logDirectoryPath, $"{SummaryFilePrefix}{sessionStartedAt:yyyy-MM-dd_HH-mm-ss}.txt");
        using var logWriter = new StreamWriter(logFilePath, append: false) { AutoFlush = true };

        using var serialPort = CreateSerialPort(settings);
        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        if (!TryOpenSerialPort(serialPort))
        {
            return 2;
        }

        try
        {
            PrintCaptureBanner(serialPort, logFilePath, summaryFilePath);
            RunCaptureLoop(serialPort, logWriter, settings, cancellationSource.Token);
            Console.WriteLine("Stopped.");
            return 0;
        }
        finally
        {
            if (serialPort.IsOpen)
            {
                serialPort.Close();
            }

            WriteSummary(summaryFilePath, settings.MasterDelayThresholdMilliseconds);
        }
    }

    // A passive tap must hold the port open for the whole session. Every moment
    // the port is closed while the bus is active, Windows' serial enumerator can
    // mistake Modbus bytes for a serial mouse, attach a phantom "Microsoft Serial
    // Ballpoint", and send the real pointer jumping. Open() also drives DTR and
    // RTS to match the flags below, so they are set explicitly rather than left
    // to defaults.
    private static SerialPort CreateSerialPort(ModbusSettings settings) =>
        new(settings.PortName, settings.BaudRate, settings.PortParity, settings.DataBits, settings.PortStopBits)
        {
            Handshake = settings.PortHandshake,
            ReadTimeout = settings.PartialFrameTimeoutMilliseconds,

            // Keep RTS deasserted so the adapter never keys the RS-485 driver.
            RtsEnable = settings.RtsEnable,

            // DTR follows the INI (deasserted by default). Some USB adapters reset
            // on a DTR edge, so it is left configurable for unusual hardware.
            DtrEnable = settings.DtrEnable,
        };

    private static bool TryOpenSerialPort(SerialPort serialPort)
    {
        try
        {
            serialPort.Open();

            // Drop whatever the enumerator or earlier noise left buffered so the
            // first frame we parse starts on a clean boundary.
            serialPort.DiscardInBuffer();
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or ArgumentException)
        {
            Console.Error.WriteLine($"Could not open {serialPort.PortName}: {exception.Message}");
            PrintAvailablePorts();
            Console.WriteLine($"Set PortName in {ConfigurationFileName} to one of the ports listed above, then run again.");
            WaitForKeyIfInteractive();
            return false;
        }
    }

    private static void PrintCaptureBanner(SerialPort serialPort, string logFilePath, string summaryFilePath)
    {
        Console.WriteLine($"Listening on {serialPort.PortName} at {serialPort.BaudRate} baud. Press Ctrl+C to stop.");
        Console.WriteLine("Leave this program running for the whole capture. While the port is closed,");
        Console.WriteLine("Windows can misread bus traffic as a serial mouse and make the pointer jump.");
        Console.WriteLine("If that still happens, turn off \"Serial Enumerator\" in Device Manager under");
        Console.WriteLine("the port's Port Settings, Advanced.");
        Console.WriteLine("For lower USB latency, set the adapter latency timer to 1 ms.");
        Console.WriteLine("Frames are marked REQUEST, RESPONSE, or AMBIGUOUS when Modbus layouts overlap.");
        Console.WriteLine($"Logging to {logFilePath}");
        Console.WriteLine($"Summary will be written to {summaryFilePath}");
    }

    private static void RunCaptureLoop(
        SerialPort serialPort,
        TextWriter logWriter,
        ModbusSettings settings,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[ReadBufferSize];
        var receivedBytes = new List<byte>();
        PendingRequest? pendingRequest = null;
        LastResponse? lastResponse = null;
        long usbTransmissionNumber = 0;
        long previousUsbTransmissionTimestamp = 0;
        var frameTransport = new FrameTransport();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                int bytesRead = serialPort.Read(buffer, 0, buffer.Length);
                UsbTransmission usbTransmission = CreateUsbTransmission(++usbTransmissionNumber, bytesRead, ref previousUsbTransmissionTimestamp);
                ProcessReceivedBytes(receivedBytes, buffer.AsSpan(0, bytesRead), logWriter, ref pendingRequest, ref lastResponse, usbTransmission, frameTransport, settings.MasterDelayThresholdMilliseconds);
            }
            catch (TimeoutException)
            {
                PrintIncompleteBytes(receivedBytes, logWriter, frameTransport);
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"Serial read failed on {serialPort.PortName}: {exception.Message}");
                Console.Error.WriteLine("The adapter may have been removed. Stopping capture.");
                return;
            }
        }
    }

    private static void WaitForKeyIfInteractive()
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        Console.WriteLine("Press any key to close.");
        Console.ReadKey(intercept: true);
    }

    private static void PrintAvailablePorts()
    {
        string[] availablePorts = SerialPort.GetPortNames().OrderBy(port => port, StringComparer.OrdinalIgnoreCase).ToArray();
        Dictionary<string, string> portNames = OperatingSystem.IsWindows()
            ? GetUsbPortNames()
            : [];
        Console.WriteLine("Available COM ports:");
        if (availablePorts.Length == 0)
        {
            Console.WriteLine("  none detected");
            return;
        }

        for (int index = 0; index < availablePorts.Length; index++)
        {
            string port = availablePorts[index];
            string displayName = portNames.TryGetValue(port, out string? name)
                ? $"{port} - {name}"
                : port;
            Console.WriteLine($"  {index + 1}. {displayName}");
        }
    }

    private static UsbTransmission CreateUsbTransmission(long transmissionNumber, int byteCount, ref long previousTransmissionTimestamp)
    {
        long currentTimestamp = Stopwatch.GetTimestamp();
        double gapMilliseconds = previousTransmissionTimestamp == 0
            ? 0
            : (currentTimestamp - previousTransmissionTimestamp) * 1000d / Stopwatch.Frequency;

        previousTransmissionTimestamp = currentTimestamp;
        return new UsbTransmission(transmissionNumber, byteCount, gapMilliseconds);
    }

    private static void ProcessReceivedBytes(
        List<byte> receivedBytes,
        ReadOnlySpan<byte> bytes,
        TextWriter logWriter,
        ref PendingRequest? pendingRequest,
        ref LastResponse? lastResponse,
        UsbTransmission usbTransmission,
        FrameTransport frameTransport,
        int masterDelayThresholdMilliseconds)
    {
        receivedBytes.AddRange(bytes);

        while (true)
        {
            if (TryExtractModbusFrame(receivedBytes, out byte[]? frame) && frame is not null)
            {
                frameTransport.Add(usbTransmission);
                string label = GetModbusDirection(frame);
                double? responseTimeMilliseconds = null;
                DateTimeOffset observedAt = DateTimeOffset.UtcNow;
                if (label == "REQUEST")
                {
                    if (pendingRequest is not null)
                    {
                        PrintNoResponse(pendingRequest, observedAt, logWriter);
                    }
                    else
                    {
                        PrintMasterDelayIfNeeded(lastResponse, frame, observedAt, logWriter, masterDelayThresholdMilliseconds);
                    }

                    pendingRequest = new PendingRequest(frame[0], frame[1], observedAt);
                }
                else if (label == "RESPONSE")
                {
                    label = GetResponseLabel(frame, ref pendingRequest, out responseTimeMilliseconds);
                    lastResponse = new LastResponse(frame[0], (byte)(frame[1] & 0x7F), observedAt);
                }

                PrintFrame(label, frame, logWriter, frameTransport, includeModbusHeader: true, responseTimeMilliseconds);
                frameTransport.Reset();
                continue;
            }

            if (!TryFindNextRequestStart(receivedBytes, out int requestStart))
            {
                break;
            }

            frameTransport.Add(usbTransmission);
            PrintFrame("TRUNCATED_BY_REQUEST", CollectionsMarshal.AsSpan(receivedBytes)[..requestStart], logWriter, frameTransport);
            receivedBytes.RemoveRange(0, requestStart);
            frameTransport.Reset();
        }

        if (receivedBytes.Count > 0)
        {
            frameTransport.Add(usbTransmission);
        }
    }

    private static bool TryFindNextRequestStart(List<byte> receivedBytes, out int requestStart)
    {
        for (int index = 1; index < receivedBytes.Count - 1; index++)
        {
            List<byte> candidate = receivedBytes.GetRange(index, receivedBytes.Count - index);
            foreach (int frameLength in GetPossibleFrameLengths(candidate))
            {
                if (candidate.Count >= frameLength && HasValidModbusCrc(candidate, frameLength))
                {
                    byte[] frame = candidate.GetRange(0, frameLength).ToArray();
                    if (GetModbusDirection(frame) == "REQUEST")
                    {
                        requestStart = index;
                        return true;
                    }
                }
            }
        }

        requestStart = 0;
        return false;
    }

    private static bool TryExtractModbusFrame(List<byte> receivedBytes, out byte[]? frame)
    {
        frame = null;
        if (receivedBytes.Count < 2)
        {
            return false;
        }

        foreach (int frameLength in GetPossibleFrameLengths(receivedBytes))
        {
            if (receivedBytes.Count >= frameLength && HasValidModbusCrc(receivedBytes, frameLength))
            {
                frame = receivedBytes.GetRange(0, frameLength).ToArray();
                receivedBytes.RemoveRange(0, frameLength);
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<int> GetPossibleFrameLengths(IReadOnlyList<byte> bytes)
    {
        byte functionCode = bytes[1];
        if ((functionCode & 0x80) != 0)
        {
            yield return 5;
            yield break;
        }

        switch (functionCode)
        {
            case >= 0x01 and <= 0x04:
                yield return 8;
                if (bytes.Count >= 3)
                {
                    yield return bytes[2] + 5;
                }

                break;
            case 0x05:
            case 0x06:
                yield return 8;
                break;
            case 0x0F:
            case 0x10:
                yield return 8;
                if (bytes.Count >= 7)
                {
                    yield return bytes[6] + 9;
                }

                break;
            case 0x16:
                yield return 10;
                break;
            case 0x17:
                if (bytes.Count >= 3)
                {
                    yield return bytes[2] + 5;
                }

                if (bytes.Count >= 11)
                {
                    yield return bytes[10] + 13;
                }

                break;
            case 0x18:
                yield return 6;
                if (bytes.Count >= 3)
                {
                    yield return bytes[2] + 5;
                }

                break;
        }
    }

    private static bool HasValidModbusCrc(IReadOnlyList<byte> bytes, int frameLength)
    {
        ushort crc = 0xFFFF;
        for (int index = 0; index < frameLength - 2; index++)
        {
            crc ^= bytes[index];
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }

        return bytes[frameLength - 2] == (byte)crc && bytes[frameLength - 1] == (byte)(crc >> 8);
    }

    private static void PrintIncompleteBytes(List<byte> receivedBytes, TextWriter logWriter, FrameTransport frameTransport)
    {
        if (receivedBytes.Count == 0)
        {
            return;
        }

        PrintFrame("INCOMPLETE", CollectionsMarshal.AsSpan(receivedBytes), logWriter, frameTransport);
        receivedBytes.Clear();
        frameTransport.Reset();
    }

    private static void PrintFrame(
        string label,
        ReadOnlySpan<byte> bytes,
        TextWriter logWriter,
        FrameTransport frameTransport,
        bool includeModbusHeader = false,
        double? responseTimeMilliseconds = null,
        double? masterDelayMilliseconds = null)
    {
        string modbusHeader = includeModbusHeader
            ? $" address={bytes[0]}(0x{bytes[0]:X2}) function=0x{(bytes[1] & 0x7F):X2} length={bytes.Length}"
            : string.Empty;
        var line = new StringBuilder($"[{label}{modbusHeader} {frameTransport}] ");

        foreach (byte value in bytes)
        {
            line.Append($"{value:X2} ");
        }

        Console.WriteLine(line);
        var captureRecord = new CaptureRecord(
            DateTimeOffset.UtcNow,
            label,
            includeModbusHeader ? bytes[0] : null,
            includeModbusHeader ? (byte)(bytes[1] & 0x7F) : null,
            bytes.Length,
            frameTransport.FirstUsbTransmissionNumber,
            frameTransport.LastUsbTransmissionNumber,
            frameTransport.UsbTransmissionCount,
            frameTransport.MaximumGapMilliseconds,
            responseTimeMilliseconds,
            masterDelayMilliseconds,
            Convert.ToHexString(bytes));
        captureRecords.Add(captureRecord);
        logWriter.WriteLine(JsonSerializer.Serialize(captureRecord));
    }

    private static void PrintMasterDelayIfNeeded(
        LastResponse? lastResponse,
        ReadOnlySpan<byte> request,
        DateTimeOffset requestObservedAt,
        TextWriter logWriter,
        int masterDelayThresholdMilliseconds)
    {
        if (lastResponse is null)
        {
            return;
        }

        double delayMilliseconds = (requestObservedAt - lastResponse.ObservedAt).TotalMilliseconds;
        if (delayMilliseconds < masterDelayThresholdMilliseconds)
        {
            return;
        }

        string label = $"MASTER_DELAY_AFTER_RESPONSE {delayMilliseconds:F1}ms response={lastResponse.UnitAddress:X2}/0x{lastResponse.FunctionCode:X2}";
        string line = $"[{label} nextRequest={request[0]:X2}/0x{request[1]:X2}]";
        Console.WriteLine(line);

        var captureRecord = new CaptureRecord(
            requestObservedAt,
            "MASTER_DELAY_AFTER_RESPONSE",
            request[0],
            request[1],
            0,
            0,
            0,
            0,
            0,
            null,
            delayMilliseconds,
            string.Empty);
        captureRecords.Add(captureRecord);
        logWriter.WriteLine(JsonSerializer.Serialize(captureRecord));
    }

    private static void PrintNoResponse(
        PendingRequest pendingRequest,
        DateTimeOffset nextRequestObservedAt,
        TextWriter logWriter)
    {
        double waitMilliseconds = (nextRequestObservedAt - pendingRequest.ObservedAt).TotalMilliseconds;
        string label = $"NO_RESPONSE {waitMilliseconds:F1}ms expected={pendingRequest.UnitAddress:X2}/0x{pendingRequest.FunctionCode:X2}";
        string line = $"{nextRequestObservedAt:O} {label}";
        Console.WriteLine(line);

        var captureRecord = new CaptureRecord(
            nextRequestObservedAt,
            "NO_RESPONSE",
            pendingRequest.UnitAddress,
            pendingRequest.FunctionCode,
            0,
            0,
            0,
            0,
            0,
            null,
            null,
            string.Empty);
        captureRecords.Add(captureRecord);
        logWriter.WriteLine(line);
    }

    private static void WriteSummary(string summaryFilePath, int masterDelayThresholdMilliseconds)
    {
        var errorRecords = captureRecords.Where(IsError).ToArray();
        var recordsByType = captureRecords
            .GroupBy(GetRecordType)
            .OrderBy(group => group.Key)
            .ToArray();
        var addresses = captureRecords
            .Where(record => !record.Label.StartsWith("MASTER_DELAY_AFTER_RESPONSE", StringComparison.Ordinal))
            .Select(record => new { Record = record, Address = GetObservedAddress(record) })
            .Where(item => item.Address.HasValue)
            .GroupBy(item => item.Address!.Value)
            .Select(group => new
            {
                Address = group.Key,
                TotalRecords = group.Count(),
                ErrorCount = group.Count(item => IsError(item.Record))
            })
            .OrderByDescending(item => item.ErrorCount)
            .ThenBy(item => item.Address)
            .ToArray();
        var topUsbTransmissionGaps = captureRecords
            .OrderByDescending(record => record.MaximumGapMilliseconds)
            .ThenByDescending(record => record.UsbTransmissionCount)
            .Take(10)
            .ToArray();

        var report = new StringBuilder();
        report.AppendLine("ModbusSniffer Session Summary");
        report.AppendLine($"Generated: {DateTimeOffset.Now:G}");
        report.AppendLine($"Total records: {captureRecords.Count}");
        report.AppendLine($"Total errors: {errorRecords.Length}");
        report.AppendLine("RESPONSE_MISMATCH and RESPONSE_WITHOUT_REQUEST = CRC-valid protocol-sequence errors.");
        report.AppendLine("INCOMPLETE and TRUNCATED_BY_REQUEST = response data did not form a CRC-valid frame.");
        report.AppendLine("NO_RESPONSE = a new request arrived while the prior request was still awaiting a response.");
        report.AppendLine($"MASTER_DELAY_AFTER_RESPONSE threshold: {masterDelayThresholdMilliseconds} ms.");
        report.AppendLine();
        report.AppendLine("Records by type:");
        foreach (var group in recordsByType)
        {
            report.AppendLine($"  {group.Key,-28} {group.Count(),6}");
        }

        report.AppendLine();
        report.AppendLine("All observed addresses:");
        report.AppendLine("  Address       Records  Errors");
        if (addresses.Length == 0)
        {
            report.AppendLine("  None");
        }
        else
        {
            foreach (var address in addresses)
            {
                report.AppendLine($"  {address.Address,3} (0x{address.Address:X2}) {address.TotalRecords,8} {address.ErrorCount,7}");
            }
        }

        report.AppendLine();
        report.AppendLine("Top 10 intra-frame USB transmission gaps:");
        report.AppendLine("  Gap ms    Type                         Address  Function  Length  USB transmissions");
        foreach (CaptureRecord record in topUsbTransmissionGaps)
        {
            byte? address = GetObservedAddress(record);
            string addressText = address.HasValue ? $"{address.Value} (0x{address.Value:X2})" : "-";
            string functionText = record.Function.HasValue ? $"0x{record.Function.Value:X2}" : "-";
            report.AppendLine($"  {record.MaximumGapMilliseconds,8:F3}  {GetRecordType(record),-28} {addressText,-9} {functionText,-8} {record.Length,6}  #{record.FirstUsbTransmission}-#{record.LastUsbTransmission}");
        }

        File.WriteAllText(summaryFilePath, report.ToString());
    }

    private static string GetRecordType(CaptureRecord record) =>
        record.Label.StartsWith("MATCHED_EXCEPTION", StringComparison.Ordinal) ? "MATCHED_EXCEPTION_RESPONSE" :
        record.Label.StartsWith("MATCHED_RESPONSE", StringComparison.Ordinal) ? "MATCHED_RESPONSE" :
        record.Label.StartsWith("MASTER_DELAY_AFTER_RESPONSE", StringComparison.Ordinal) ? "MASTER_DELAY_AFTER_RESPONSE" :
        record.Label;

    private static bool IsError(CaptureRecord record) =>
        record.Label.StartsWith("INCOMPLETE", StringComparison.Ordinal) ||
        record.Label.StartsWith("TRUNCATED", StringComparison.Ordinal) ||
        record.Label.StartsWith("NO_RESPONSE", StringComparison.Ordinal) ||
        record.Label.StartsWith("RESPONSE_MISMATCH", StringComparison.Ordinal) ||
        record.Label.StartsWith("RESPONSE_WITHOUT_REQUEST", StringComparison.Ordinal) ||
        record.Label.StartsWith("MATCHED_EXCEPTION", StringComparison.Ordinal);

    private static byte? GetObservedAddress(CaptureRecord record) =>
        record.Address ?? (record.Hex.Length >= 2 && byte.TryParse(record.Hex[..2], System.Globalization.NumberStyles.HexNumber, null, out byte address)
            ? address
            : null);

    private static string GetModbusDirection(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
        {
            return "INCOMPLETE";
        }

        byte functionCode = frame[1];
        if ((functionCode & 0x80) != 0)
        {
            return "RESPONSE";
        }

        return functionCode switch
        {
            >= 0x01 and <= 0x04 => GetReadDirection(frame),
            0x0F or 0x10 => GetMultipleWriteDirection(frame),
            0x05 or 0x06 or 0x16 => "AMBIGUOUS",
            _ => "UNKNOWN"
        };
    }

    private static string GetResponseLabel(
        ReadOnlySpan<byte> frame,
        ref PendingRequest? pendingRequest,
        out double? responseTimeMilliseconds)
    {
        responseTimeMilliseconds = null;
        if (pendingRequest is null)
        {
            return "RESPONSE_WITHOUT_REQUEST";
        }

        byte responseFunctionCode = (byte)(frame[1] & 0x7F);
        if (pendingRequest.UnitAddress != frame[0] || pendingRequest.FunctionCode != responseFunctionCode)
        {
            return $"RESPONSE_MISMATCH expected={pendingRequest.UnitAddress:X2}/0x{pendingRequest.FunctionCode:X2} received={frame[0]:X2}/0x{responseFunctionCode:X2}";
        }

        TimeSpan responseTime = DateTimeOffset.UtcNow - pendingRequest.ObservedAt;
        responseTimeMilliseconds = responseTime.TotalMilliseconds;
        pendingRequest = null;
        return (frame[1] & 0x80) != 0
            ? $"MATCHED_EXCEPTION_RESPONSE {responseTime.TotalMilliseconds:F1}ms"
            : $"MATCHED_RESPONSE {responseTime.TotalMilliseconds:F1}ms";
    }

    private static string GetReadDirection(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 8)
        {
            return frame[2] + 5 == frame.Length ? "AMBIGUOUS" : "REQUEST";
        }

        return frame.Length == frame[2] + 5 ? "RESPONSE" : "AMBIGUOUS";
    }

    private static string GetMultipleWriteDirection(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 8)
        {
            return "RESPONSE";
        }

        return frame.Length >= 7 && frame.Length == frame[6] + 9 ? "REQUEST" : "AMBIGUOUS";
    }

    private static void DisableConsoleWrapping()
    {
        nint outputHandle = GetStdHandle(StandardOutputHandle);
        if (outputHandle != 0 && outputHandle != -1 && GetConsoleMode(outputHandle, out uint mode))
        {
            SetConsoleMode(outputHandle, mode & ~EnableWrapAtEndOfLineOutput);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Dictionary<string, string> GetUsbPortNames()
    {
        var portNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT DeviceID, Name FROM Win32_SerialPort");
            using System.Management.ManagementObjectCollection devices = searcher.Get();
            foreach (System.Management.ManagementObject device in devices)
            {
                string? portName = device["DeviceID"] as string;
                string? friendlyName = device["Name"] as string;
                if (portName is not null && friendlyName is not null)
                {
                    portNames[portName] = friendlyName;
                }
            }

            using var pnpSearcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name IS NOT NULL");
            using System.Management.ManagementObjectCollection pnpDevices = pnpSearcher.Get();
            foreach (System.Management.ManagementObject device in pnpDevices)
            {
                string? friendlyName = device["Name"] as string;
                if (friendlyName is null)
                {
                    continue;
                }

                foreach (string portName in SerialPort.GetPortNames())
                {
                    if (friendlyName.Contains($"({portName})", StringComparison.OrdinalIgnoreCase))
                    {
                        portNames[portName] = friendlyName;
                    }
                }
            }

            if (portNames.Count > 0)
            {
                return portNames;
            }
        }
        catch (System.Management.ManagementException)
        {
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }

        foreach (string registryPath in new[]
        {
            @"SYSTEM\CurrentControlSet\Enum\USB",
            @"SYSTEM\CurrentControlSet\Enum\FTDIBUS"
        })
        {
            try
            {
                using Microsoft.Win32.RegistryKey? usbKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(registryPath);
                if (usbKey is not null)
                {
                    FindUsbPortNames(usbKey, portNames);
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
            catch (IOException)
            {
            }
        }

        return portNames;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void FindUsbPortNames(Microsoft.Win32.RegistryKey key, Dictionary<string, string> portNames)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? deviceParameters = key.OpenSubKey("Device Parameters");
            string? portName = deviceParameters?.GetValue("PortName") as string;
            string? friendlyName = key.GetValue("FriendlyName") as string;
            if (portName is not null && friendlyName is not null)
            {
                portNames[portName] = friendlyName;
            }

            foreach (string subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using Microsoft.Win32.RegistryKey? subKey = key.OpenSubKey(subKeyName);
                    if (subKey is not null)
                    {
                        FindUsbPortNames(subKey, portNames);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (System.Security.SecurityException)
                {
                }
                catch (IOException)
                {
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (System.Security.SecurityException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static void ConfigureConsoleBuffer()
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected || Console.BufferWidth >= ConsoleBufferWidth)
        {
            return;
        }

        try
        {
            Console.BufferWidth = ConsoleBufferWidth;
        }
        catch (IOException)
        {
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint consoleHandle, out uint mode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(nint consoleHandle, uint mode);

    private sealed class ModbusSettings
    {
        public string PortName { get; private set; } = "COM7";
        public int BaudRate { get; private set; } = 57600;
        public Parity PortParity { get; private set; } = Parity.None;
        public int DataBits { get; private set; } = 8;
        public StopBits PortStopBits { get; private set; } = StopBits.One;
        public Handshake PortHandshake { get; private set; } = Handshake.None;
        public bool DtrEnable { get; private set; }
        public bool RtsEnable { get; private set; }
        public int PartialFrameTimeoutMilliseconds { get; private set; } = 100;
        public int MasterDelayThresholdMilliseconds { get; private set; } = 400;

        public static ModbusSettings Load(string filePath)
        {
            var settings = new ModbusSettings();
            if (!File.Exists(filePath))
            {
                File.WriteAllLines(filePath,
                [
                    "PortName=COM7",
                    "BaudRate=57600",
                    "Parity=None",
                    "DataBits=8",
                    "StopBits=One",
                    "Handshake=None",
                    "DtrEnable=false",
                    "RtsEnable=false",
                    "PartialFrameTimeoutMilliseconds=100",
                    "MasterDelayThresholdMilliseconds=400"
                ]);
                return settings;
            }

            foreach (string line in File.ReadLines(filePath))
            {
                string[] parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || parts[0].Length == 0 || parts[0].StartsWith(';'))
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "PortName":
                        settings.PortName = parts[1];
                        break;
                    case "BaudRate":
                        if (int.TryParse(parts[1], out int baudRate) && baudRate > 0) settings.BaudRate = baudRate;
                        break;
                    case "Parity":
                        if (Enum.TryParse(parts[1], true, out Parity parity)) settings.PortParity = parity;
                        break;
                    case "DataBits":
                        if (int.TryParse(parts[1], out int dataBits) && dataBits > 0) settings.DataBits = dataBits;
                        break;
                    case "StopBits":
                        if (Enum.TryParse(parts[1], true, out StopBits stopBits)) settings.PortStopBits = stopBits;
                        break;
                    case "Handshake":
                        if (Enum.TryParse(parts[1], true, out Handshake handshake)) settings.PortHandshake = handshake;
                        break;
                    case "DtrEnable":
                        if (bool.TryParse(parts[1], out bool dtrEnable)) settings.DtrEnable = dtrEnable;
                        break;
                    case "RtsEnable":
                        if (bool.TryParse(parts[1], out bool rtsEnable)) settings.RtsEnable = rtsEnable;
                        break;
                    case "PartialFrameTimeoutMilliseconds":
                        if (int.TryParse(parts[1], out int partialTimeout) && partialTimeout > 0) settings.PartialFrameTimeoutMilliseconds = partialTimeout;
                        break;
                    case "MasterDelayThresholdMilliseconds":
                        if (int.TryParse(parts[1], out int masterDelay) && masterDelay >= 0) settings.MasterDelayThresholdMilliseconds = masterDelay;
                        break;
                }
            }

            return settings;
        }
    }

    private sealed record PendingRequest(byte UnitAddress, byte FunctionCode, DateTimeOffset ObservedAt);

    private sealed record LastResponse(byte UnitAddress, byte FunctionCode, DateTimeOffset ObservedAt);

    private sealed record CaptureRecord(
        DateTimeOffset Timestamp,
        string Label,
        byte? Address,
        byte? Function,
        int Length,
        long FirstUsbTransmission,
        long LastUsbTransmission,
        int UsbTransmissionCount,
        double MaximumGapMilliseconds,
        double? ResponseTimeMilliseconds,
        double? MasterDelayMilliseconds,
        string Hex);

    private readonly record struct UsbTransmission(long Number, int ByteCount, double GapMilliseconds);

    private sealed class FrameTransport
    {
        private long firstUsbTransmissionNumber;
        private long lastUsbTransmissionNumber;
        private int usbTransmissionCount;
        private double maximumGapMilliseconds;

        public void Add(UsbTransmission usbTransmission)
        {
            if (usbTransmissionCount == 0)
            {
                firstUsbTransmissionNumber = usbTransmission.Number;
            }
            else if (lastUsbTransmissionNumber != usbTransmission.Number)
            {
                maximumGapMilliseconds = Math.Max(maximumGapMilliseconds, usbTransmission.GapMilliseconds);
            }

            if (lastUsbTransmissionNumber != usbTransmission.Number)
            {
                usbTransmissionCount++;
            }

            lastUsbTransmissionNumber = usbTransmission.Number;
        }

        public void Reset()
        {
            firstUsbTransmissionNumber = 0;
            lastUsbTransmissionNumber = 0;
            usbTransmissionCount = 0;
            maximumGapMilliseconds = 0;
        }

        public long FirstUsbTransmissionNumber => firstUsbTransmissionNumber;

        public long LastUsbTransmissionNumber => lastUsbTransmissionNumber;

        public int UsbTransmissionCount => usbTransmissionCount;

        public double MaximumGapMilliseconds => maximumGapMilliseconds;

        public override string ToString() => $"usbTransmissions=#{firstUsbTransmissionNumber}-#{lastUsbTransmissionNumber} count={usbTransmissionCount} maxGap={maximumGapMilliseconds:F3}ms";
    }
}
