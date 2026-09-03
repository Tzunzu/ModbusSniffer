# ModbusSniffer

A .NET 8 console application for monitoring and analyzing Modbus RTU traffic on a serial port.

## Features

- Captures serial traffic from a configured COM port.
- Detects CRC-valid Modbus RTU frames.
- Classifies requests, responses, ambiguous frames, and incomplete traffic.
- Matches responses to requests and reports response timing.
- Reports missing responses, mismatches, and master delays.
- Lists available COM ports with Windows device names when the configured port cannot be opened.
- Creates a new timestamped log and summary for every session.

## Requirements

- Windows, when using COM ports and Windows device names.
- .NET 8 SDK.
- A serial adapter connected to the Modbus RTU network.

## Configuration

Edit `ModbusSniffer.ini` to select the serial port and other settings. The default port is `COM7`.

The INI file uses simple `property=value` lines. Lines beginning with `;` are comments.

The serial settings are currently:

- Baud rate: `57600`
- Data bits: `8`
- Parity: `None`
- Stop bits: `1`
- Handshake: `None`

If the configured port cannot be opened, the program displays available ports and lets you select another port by number or name.

For lower USB latency, set the adapter's latency timer to `1 ms` in its driver settings.

## Run

```text
dotnet run --project ModbusSniffer.csproj
```

Press `Ctrl+C` to stop a capture.

## Build Release

```text
dotnet publish ModbusSniffer.csproj -c Release -o release
```

The Release configuration creates a self-contained, compressed single-file Windows x64 executable with its dependencies embedded.

## Output

Session files are written to the `log` folder beside the application:

- `ModbusSniffer_yyyy-MM-dd_HH-mm-ss.log`
- `ModbusSniffer.summary_yyyy-MM-dd_HH-mm-ss.txt`

Build output and runtime logs are excluded from Git by `.gitignore`.
