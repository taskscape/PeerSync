# Reference hardware

Measured on 2026-10-18. Hardware labels are local evidence, not performance guarantees.

| Host | CPU | Storage | Network | OS / SDK |
| --- | --- | --- | --- | --- |
| Development PC (R5-7600X) | AMD Ryzen 5 7600X, 6 cores / 12 threads | Crucial CT2000P310SSD8, 2 TB NVMe SSD | Realtek PCIe 2.5GbE, negotiated at 1 Gbit/s; Wi-Fi disconnected | Windows build 26300; .NET SDK 10.0.402, runtime 10.0.13, x64 |
| Second physical PC | Not provided | Not provided | Not provided | Not provided |
| Windows Server 2025 VM / taskscape.com | Not available in this workspace | Not measured | TCP/UDP 99 not yet measured from outside | Not verified |

Docker Desktop 4.95.0 is using `desktop-linux`, with a Linux/WSL2 engine.
It cannot run Windows containers in this configuration. No VM or container
results are represented as physical LAN or WAN validation.

P0.1.4 remains open until the other reference machines are recorded.
