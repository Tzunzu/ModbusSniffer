# ModbusSniffer

A .NET 8 console application for monitoring and analyzing Modbus RTU traffic on a serial port.

## Features

- Captures serial traffic from a configured COM port.
- Detects CRC-valid Modbus RTU frames.
- Classifies requests, responses, ambiguous frames, and incomplete traffic.
- Frames every standard function code (0x01-0x18, 0x2B) and tags each record with its name, for example `Read Holding Registers`.
- Matches responses to requests and reports response timing.
- Reports missing responses, mismatches, and master delays.
- Lists available COM ports with Windows device names when the configured port cannot be opened.
- Holds the port open for the whole session and keeps RTS/DTR in a fixed state, so Windows does not misdetect bus traffic as a serial mouse.
- Writes the log and console output on a background thread so disk latency does not distort the capture timing.
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
- `DtrEnable`: `false`
- `RtsEnable`: `false`

`DtrEnable` and `RtsEnable` set the control-line state that is held for the whole
session. Keep `RtsEnable=false` so the passive tap never keys the RS-485 driver.
Leave `DtrEnable=false` unless your adapter needs DTR asserted.

If the configured port cannot be opened, the program lists the available ports and exits. Set `PortName` in `ModbusSniffer.ini` to one of them and run again.

For lower USB latency, set the adapter's latency timer to `1 ms` in its driver settings.

### Jumping mouse pointer

While a COM port is closed, Windows sniffs it for a legacy serial mouse and can
attach a phantom "Microsoft Serial Ballpoint" when bus bytes match the mouse
pattern, which makes the real pointer jump and click. To avoid it:

- Leave ModbusSniffer running for the entire capture; it holds the port open.
- If it still happens, open Device Manager, expand **Ports (COM & LPT)**, open the
  adapter's properties, and under **Port Settings, Advanced** turn off
  **Serial Enumerator**.

## Run

```text
dotnet run --project ModbusSniffer/ModbusSniffer.csproj
```

Press `Ctrl+C` to stop a capture.

## Tests

```text
dotnet test
```

`ModbusSniffer.Tests` covers the frame-classification heuristics (direction
detection, CRC validation, frame extraction, and request resynchronisation)
using real captured frames.

## Build Release

```text
dotnet publish ModbusSniffer/ModbusSniffer.csproj -c Release -o release
```

The Release configuration creates a self-contained, compressed single-file Windows x64 executable with its dependencies embedded. The VS Code `release` task publishes both applications into the same `release` folder.

## Output

Session files are written to the `log` folder beside the application:

- `ModbusSniffer_yyyy-MM-dd_HH-mm-ss.log`
- `ModbusSniffer.summary_yyyy-MM-dd_HH-mm-ss.txt`

Build output and runtime logs are excluded from Git by `.gitignore`.
