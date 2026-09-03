# Modbus Capture Investigation

## Purpose

This project passively records Modbus RTU traffic from an FTDI USB serial adapter. It is being used to investigate communication faults reported by a B&R PLC using a CS1030 serial interface.

## Terminology

### Modbus RTU request

A master sends a request containing:

```text
[slave address] [function code] [data] [CRC low] [CRC high]
```

Example: `5D 03 00 00 00 2C 48 8B`

- `5D` is address 93.
- `03` is Read Holding Registers.
- `00 00` is start offset 0.
- `00 2C` requests 44 registers.

### Modbus RTU response

A slave replies using the same address and function code. For a successful function `03` response, the third byte is the number of payload bytes.

Example: `5D 03 58 ...`

- `58` hexadecimal is 88 decimal bytes.
- 44 registers require $44 \times 2 = 88$ data bytes.
- The complete response is $1 + 1 + 1 + 88 + 2 = 93$ bytes, including CRC.

### CRC

Modbus RTU uses CRC-16. The sniffer only labels a frame as a valid request or response when the expected frame length is available and the CRC matches. A valid CRC means the received bytes are internally consistent; it does not prove that the frame belongs to the request currently awaited by the master.

### $t_{3.5}$ frame gap

Modbus RTU does not have a special start byte. Frames are separated on the physical serial line by a silent interval of at least 3.5 character times.

At the configured `57600, 8N1` link:

$$
t_{\text{character}} = \frac{10}{57600} \approx 0.174\text{ ms}
$$

$$
t_{3.5} \approx 0.61\text{ ms}
$$

Once the PLC receives the first byte of a response, an on-wire interruption longer than this can make it treat the frame as complete or invalid.

### PLC first-response timeout

The PLC waits up to 1000 ms for the first response byte after it sends a request. This is separate from the $t_{3.5}$ inter-character/frame-gap rule used after response data begins arriving.

### `ResponseTimeMilliseconds`

`ResponseTimeMilliseconds` is written for a `MATCHED_RESPONSE`. It is the elapsed time from when this sniffer recognizes a complete CRC-valid request to when it recognizes the complete CRC-valid response with the same address and function code.

```text
[MATCHED_RESPONSE 15.8ms ...]
```

This is useful for identifying unusually late complete transactions and comparing them with the PLC's 1000 ms first-response timeout. It is not the exact delay from the master transmitting the last request byte to the slave transmitting its first response byte. Both the request and response may be delivered late or in groups by the FTDI adapter and Windows.

For that reason, a very small value, such as 0.2 ms, usually means the request and response were made available to the sniffer together or nearly together. It does not mean the slave physically responded in 0.2 ms. A large value is more useful: a value close to 1000 ms indicates that the complete response was observed near the PLC's configured first-response timeout, but still needs correlation with PLC diagnostics.

### Host USB transmission gap

`usbTransmissions`, `count`, and `maxGap` in the capture describe when data becomes available through `.NET SerialPort.Read`:

```text
usbTransmissions=#17-#19 count=3 maxGap=3.711ms
```

This is a Windows/FTDI host-delivery measurement, not a direct measurement of individual bytes on RS-485. FTDI FIFO buffering, USB transfer scheduling, the driver latency timer, and Windows thread scheduling can all affect it.

## Capture Labels

| Label | Meaning |
| --- | --- |
| `REQUEST` | CRC-valid Modbus request. |
| `MATCHED_RESPONSE` | CRC-valid response matching the outstanding request address and function. |
| `MATCHED_EXCEPTION_RESPONSE` | Valid Modbus exception response matching the outstanding request. |
| `NO_RESPONSE` | A new request was received while the preceding request was still awaiting a matching response. This is an error. |
| `RESPONSE_MISMATCH` | A CRC-valid response was received for a different address or function than the outstanding request. This is an error. |
| `RESPONSE_WITHOUT_REQUEST` | A CRC-valid response was received while no request was recorded as pending. This is an error for the observed master behavior. |
| `TRUNCATED_BY_REQUEST` | Buffered bytes did not form a CRC-valid frame before a later CRC-valid request began. This is suspicious partial/corrupt/interrupted traffic. |
| `INCOMPLETE` | Buffered bytes did not form a CRC-valid frame before the sniffer's host-side timeout. |
| `MASTER_DELAY_AFTER_RESPONSE` | More than 400 ms elapsed between the previous valid response and the next observed request. This is a timing marker, not a byte/CRC error. |

## Findings So Far

1. Swapping the passive listener's RS-485 A/B pair corrected the decode. CRC-valid Modbus requests and responses now appear, including the expected `5D 03 58` 93-byte response pattern.
2. The USB device is an FTDI FT232R-class serial interface (`VID_0403`, `PID_6001`).
3. Reducing the FTDI latency timer to 1 ms materially reduced normal host USB transmission gaps. Normal matched responses now commonly show approximately 2-4 ms host gaps.
4. The current session summary shows 330 matched responses from 359 requests, plus 28 `NO_RESPONSE` events, 10 `TRUNCATED_BY_REQUEST` events, 5 `RESPONSE_WITHOUT_REQUEST` events, and 1 `RESPONSE_MISMATCH` event.
5. The largest recorded host USB transmission gaps, approximately 56-104 ms, occur on `TRUNCATED_BY_REQUEST` records rather than normal completed responses. This correlation is suspicious and is consistent with the PLC seeing incomplete transactions.
6. Addresses 93 (`0x5D`), 106 (`0x6A`), 104 (`0x68`), and 211 (`0xD3`) occur repeatedly in the current error totals. The address on a truncated frame is only the first received byte and must be treated as best-effort evidence.

## Interpretation And Limits

The capture supports a real transaction-level problem: the master sometimes proceeds to a new request without the preceding request receiving a matched CRC-valid response. It also shows partial traffic before a later request in several cases.

The capture does not prove an on-wire $t_{3.5}$ violation. A USB serial adapter cannot provide per-byte RS-485 timestamps, even with a 1 ms FTDI latency timer. A differential RS-485 protocol analyzer, logic analyzer with a differential receiver, or oscilloscope is required to prove a sub-millisecond pause within a frame.

The most useful correlation is between the B&R CS1030 fault timestamp and capture records immediately before it. `NO_RESPONSE`, `TRUNCATED_BY_REQUEST`, and `RESPONSE_MISMATCH` around that time are stronger evidence than host USB transmission timing alone.