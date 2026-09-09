# ModbusSniffer

A .NET 8 passive sniffer for Modbus RTU traffic on a serial port, plus a WPF log
viewer. Built to investigate communication faults on an RS-485 Modbus link.

## Features

- Captures serial traffic from a configured COM port.
- Detects CRC-valid Modbus RTU frames.
- Classifies requests, responses, ambiguous frames, and incomplete traffic.
- Frames every standard function code (0x01-0x18, 0x2B) and tags each record with its name, for example `Read Holding Registers`.
- Matches responses to requests and reports response timing.
- Reports missing responses, mismatches, and master delays.
- Reads the serial port on a dedicated high-priority thread that only pulls bytes
  and timestamps them; framing, CRC checks, and logging run on a separate thread
  so their cost does not distort the inter-read timing.
- Flags a frame `SUSPECT_GAP` when the largest gap between the USB reads that make
  it up reaches the Modbus t3.5 end-of-frame interval (1.75 ms above 19200 baud).
  This is a host-side approximation, not an on-wire measurement - see
  [INVESTIGATION.md](INVESTIGATION.md) for the timing model and its limits.
- Programs the FTDI latency timer to 1 ms through the D2XX API on every run,
  before the port is opened, because the driver resets it to 16 ms on each
  reboot, replug, or USB-port change.
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

### Timing settings

| Key | Default | Meaning |
| --- | --- | --- |
| `PartialFrameTimeoutMilliseconds` | `100` | How long to wait for more bytes before a partial frame is logged `INCOMPLETE`. |
| `MasterDelayThresholdMilliseconds` | `400` | A gap larger than this between a response and the next request is logged `MASTER_DELAY_AFTER_RESPONSE`. |
| `FrameGapSuspectMilliseconds` | `0` | Intra-frame USB read gap at or above which a frame is flagged `SUSPECT_GAP`. `0` derives it from the baud rate (Modbus t3.5: 1.75 ms above 19200 baud, otherwise 3.5 character times). |
| `FtdiLatencyTimerMilliseconds` | `1` | Latency timer programmed into an FTDI adapter through the D2XX API at startup. `0` disables the call - use it for non-FTDI adapters, or set the latency timer manually in the driver's Advanced settings. |

`FtdiLatencyTimerMilliseconds` needs `FTD2XX.dll` (installed with the FTDI D2XX
runtime). If it is missing the program prints a note and continues at whatever
latency the driver chose. The applied value is shown in the startup banner.

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

- `ModbusSniffer_yyyy-MM-dd_HH-mm-ss.log` - one JSON record per line, plus a
  human-readable console line.
- `ModbusSniffer.summary_yyyy-MM-dd_HH-mm-ss.txt` - counts by record type and
  function code, per-address error tallies, and the largest intra-frame USB gaps.

Build output and runtime logs are excluded from Git by `.gitignore`.

## Log viewer

`ModbusSnifferViewer` is a WPF app (Windows only) that opens a `.log` file, shows
every record in a sortable grid with overview, traffic, error, and timing panels,
and supports search and an errors-only filter. Error rows are shaded red;
`SUSPECT_GAP` rows are shaded yellow and their gap value is highlighted.

```text
dotnet run --project ModbusSnifferViewer/ModbusSnifferViewer.csproj
```

## License

MIT - see [LICENSE](LICENSE).
