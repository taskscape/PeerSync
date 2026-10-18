# Reference hardware

Measured on 2026-10-18. Hardware labels are local evidence, not performance guarantees.

| Host | CPU | Storage | Network | OS / SDK |
| --- | --- | --- | --- | --- |
| Development PC (R5-7600X) | AMD Ryzen 5 7600X, 6 cores / 12 threads | Crucial CT2000P310SSD8, 2 TB NVMe SSD | Realtek PCIe 2.5GbE, negotiated at 1 Gbit/s; Wi-Fi disconnected | Windows build 26300; .NET SDK 10.0.402, runtime 10.0.13, x64 |
| GitHub-hosted Server 2022 VM | AMD EPYC 7763 host CPU, 1 exposed core / 2 logical processors | Msft Virtual Disk, SAS, 80,530,636,800 + 274,877,906,944 bytes; underlying media unspecified | Virtual adapters report 50 Gbit/s and 10 Gbit/s; inter-machine throughput not measured | Server 2022 Datacenter, build 20348; SDK 10.0.402 |
| GitHub-hosted Server 2025 VM | AMD EPYC 7763 host CPU, 1 exposed core / 2 logical processors | Msft Virtual Disk, SAS, 80,530,636,800 + 161,061,273,600 bytes; underlying media unspecified | Virtual adapters report 50 Gbit/s and 10 Gbit/s; inter-machine throughput not measured | Server 2025 Datacenter, build 26100; SDK 10.0.402 |

Docker Desktop 4.95.0 is using `desktop-linux`, with a Linux/WSL2 engine.
It cannot run Windows containers in this configuration. No VM or container
results are represented as physical LAN or WAN validation.

These snapshots cover three test environments (one physical PC and two
temporary VMs), as permitted by the user's environment-testing instruction.
CI allocations vary; compare benchmarks only with their accompanying
`environment.json`. The VM snapshots came from run 39138828095's service
artifacts. There is no second physical-PC LAN measurement or real NAT matrix.
